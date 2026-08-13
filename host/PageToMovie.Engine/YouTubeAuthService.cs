using System.Linq;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Manages the single shared OAuth2 connection PageToMovie uses to upload the WIP movie to
/// YouTube. One channel per instance, admin-connected via POST /api/youtube/connect —
/// not a per-user credential. Refresh token is persisted in SQLite
/// <c>oauth_data_store</c> inside <c>pagetomovie.db</c> under the resolved data directory
/// (see <see cref="UserDatabaseService.ResolveDataDirectory"/>) so it survives process restarts.
/// </summary>
public sealed class YouTubeAuthService
{
    private const string UserId = "PageToMovie";
    private const string ReviewPath = "/review";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly ProjectStore _projects;
    private readonly YouTubeOptions _opts;
    private readonly Lazy<GoogleAuthorizationCodeFlow?> _flow;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expiry, string ReturnPath)> _pendingStates = new();

    public YouTubeAuthService(ProjectStore projects, IOptions<PageToMovieOptions> opts)
    {
        _projects = projects;
        _opts = opts.Value.YouTube ?? new YouTubeOptions();
        _flow = new Lazy<GoogleAuthorizationCodeFlow?>(BuildFlow);
    }

    public string CleanClientId => ProviderApiKey.Clean(_opts.ClientId) ?? "";
    public string CleanClientSecret => ProviderApiKey.Clean(_opts.ClientSecret) ?? "";
    public string CleanRedirectUri => ProviderApiKey.Clean(_opts.RedirectUri) ?? "";

    /// <summary>Client id/secret/redirect are all set — OAuth can be attempted.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(CleanClientId) &&
        !string.IsNullOrWhiteSpace(CleanClientSecret) &&
        !string.IsNullOrWhiteSpace(CleanRedirectUri);

    private GoogleAuthorizationCodeFlow? BuildFlow()
    {
        if (!IsConfigured)
            return null;
        var dataDir = UserDatabaseService.ResolveDataDirectory(_projects.WorkspaceRoot);
        System.Diagnostics.Trace.TraceInformation(
            "YouTube OAuth token store: {0} (pagetomovie.db / oauth_data_store)", dataDir);
        return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = CleanClientId, ClientSecret = CleanClientSecret },
            // youtube.upload — insert videos
            // youtube.force-ssl — delete obsolete IDs after V2 replace (Item 11)
            Scopes = new[]
            {
                YouTubeService.Scope.YoutubeUpload,
                YouTubeService.Scope.YoutubeForceSsl,
                // List channel uploads for the public demo wall (YouTube = source of truth).
                YouTubeService.Scope.YoutubeReadonly,
            },
            DataStore = new SqliteDataStore(dataDir),
        });
    }

    /// <summary>Builds the Google consent URL. <paramref name="state"/> round-trips through the callback.</summary>
    /// <param name="returnPath">Relative path after OAuth (e.g. /admin/demos or /review).</param>
    public string BuildAuthorizationUrl(string state, string? returnPath = null)
    {
        var flow = _flow.Value ?? throw new InvalidOperationException(
            "YouTube OAuth is not configured — set PageToMovie:YouTube:ClientId/ClientSecret/RedirectUri.");
        var ret = NormalizeReturnPath(returnPath);
        _pendingStates[state] = (DateTimeOffset.UtcNow.Add(StateTtl), ret);
        PruneExpiredStates();
        var request = (Google.Apis.Auth.OAuth2.Requests.GoogleAuthorizationCodeRequestUrl)
            flow.CreateAuthorizationCodeRequest(CleanRedirectUri);
        request.State = state;
        // Force the consent screen so Google always reissues a refresh token, even on
        // a reconnect after a prior authorization — otherwise it's only granted once.
        request.Prompt = "consent";
        return request.Build().ToString();
    }

    /// <summary>Validates state and returns the post-OAuth path (default /review).</summary>
    public bool TryConsumeState(string state, out string returnPath)
    {
        returnPath = ReviewPath;
        if (string.IsNullOrWhiteSpace(state)) return false;
        if (_pendingStates.TryRemove(state, out var entry))
        {
            if (entry.Expiry < DateTimeOffset.UtcNow) return false;
            returnPath = entry.ReturnPath;
            return true;
        }
        // OAuth state is a CSRF token, not merely an opaque-looking string. If the process has
        // restarted, require the operator to begin a new authorization request instead of
        // accepting an attacker-supplied value.
        return false;
    }

    /// <summary>Legacy; prefer <see cref="TryConsumeState"/>.</summary>
    public bool ConsumeState(string state) => TryConsumeState(state, out _);

    private static string NormalizeReturnPath(string? returnPath)
    {
        var p = (returnPath ?? "").Trim();
        if (p.Length == 0) return ReviewPath;
        if (!p.StartsWith('/')) p = "/" + p;
        // Only same-site relative paths
        if (p.StartsWith("//", StringComparison.Ordinal) || p.Contains("://", StringComparison.Ordinal))
            return ReviewPath;
        if (p.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(ReviewPath, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/demo", StringComparison.OrdinalIgnoreCase)
            || p == "/")
            return p.Split('?', 2)[0];
        return ReviewPath;
    }

    private void PruneExpiredStates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pendingStates)
            if (kv.Value.Expiry < now)
                _pendingStates.TryRemove(kv.Key, out _);
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var flow = _flow.Value ?? throw new InvalidOperationException("YouTube OAuth is not configured.");
        await flow.ExchangeCodeForTokenAsync(UserId, code, CleanRedirectUri, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        var flow = _flow.Value;
        if (flow is null)
            return false;
        var token = await flow.LoadTokenAsync(UserId, ct).ConfigureAwait(false);
        return token is not null && !string.IsNullOrEmpty(token.RefreshToken);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var flow = _flow.Value;
        if (flow is null)
            return;
        await flow.DeleteTokenAsync(UserId, ct).ConfigureAwait(false);
    }

    /// <summary>Authorized YouTube client, or null if not configured/connected yet.</summary>
    public async Task<YouTubeService?> GetServiceAsync(CancellationToken ct = default)
    {
        var flow = _flow.Value;
        if (flow is null)
            return null;
        var token = await flow.LoadTokenAsync(UserId, ct).ConfigureAwait(false);
        if (token is null || string.IsNullOrEmpty(token.RefreshToken))
            return null;
        var credential = new UserCredential(flow, UserId, token);
        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PageToMovie",
        });
    }

    public record YouTubeVideoStats(ulong? LikeCount, ulong? ViewCount);

    /// <summary>Fetch video statistics (likeCount, viewCount) for a YouTube video ID.</summary>
    public async Task<YouTubeVideoStats?> GetVideoStatsAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        var youtube = await GetServiceAsync(ct).ConfigureAwait(false);
        if (youtube is null)
            return null;

        try
        {
            var req = youtube.Videos.List("statistics");
            req.Id = videoId.Trim();
            var res = await req.ExecuteAsync(ct).ConfigureAwait(false);
            var item = res.Items?.FirstOrDefault();
            if (item?.Statistics is null)
                return null;

            return new YouTubeVideoStats(item.Statistics.LikeCount, item.Statistics.ViewCount);
        }
        catch
        {
            return null;
        }
    }

    public sealed record ChannelUploadVideo(
        string VideoId,
        string Title,
        string? Description,
        DateTimeOffset? PublishedAt,
        string? ThumbnailUrl);

    /// <summary>
    /// All uploads on the connected channel (uploads playlist), newest first.
    /// Requires a connected OAuth channel (re-connect after scope change if list fails).
    /// </summary>
    public async Task<IReadOnlyList<ChannelUploadVideo>> ListChannelUploadsAsync(
        int maxResults = 50,
        CancellationToken ct = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 200);
        var youtube = await GetServiceAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "YouTube channel is not connected. Connect it from Admin → Demo gallery first.");

        var chReq = youtube.Channels.List("contentDetails,snippet");
        chReq.Mine = true;
        var chResp = await chReq.ExecuteAsync(ct).ConfigureAwait(false);
        var channel = chResp.Items?.FirstOrDefault()
            ?? throw new InvalidOperationException("No YouTube channel found for this OAuth account.");
        var uploadsPlaylist = channel.ContentDetails?.RelatedPlaylists?.Uploads;
        if (string.IsNullOrWhiteSpace(uploadsPlaylist))
            throw new InvalidOperationException("Channel has no uploads playlist.");

        var list = new List<ChannelUploadVideo>();
        string? pageToken = null;
        while (list.Count < maxResults)
        {
            var plReq = youtube.PlaylistItems.List("snippet,contentDetails");
            plReq.PlaylistId = uploadsPlaylist;
            plReq.MaxResults = Math.Min(50, maxResults - list.Count);
            if (!string.IsNullOrEmpty(pageToken))
                plReq.PageToken = pageToken;

            var plResp = await plReq.ExecuteAsync(ct).ConfigureAwait(false);
            foreach (var item in plResp.Items ?? Array.Empty<Google.Apis.YouTube.v3.Data.PlaylistItem>())
            {
                var videoId = item.ContentDetails?.VideoId
                    ?? item.Snippet?.ResourceId?.VideoId;
                if (string.IsNullOrWhiteSpace(videoId))
                    continue;
                var sn = item.Snippet;
                var title = (sn?.Title ?? "").Trim();
                // Deleted = gone from channel. "Private video" still has an id for the owner —
                // keep it and enrich via videos.list below (skipping used to empty the gallery).
                if (title.Equals("Deleted video", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (title.Length == 0 || title.Equals("Private video", StringComparison.OrdinalIgnoreCase))
                    title = videoId.Trim(); // placeholder until videos.list fills real title

                DateTimeOffset? published = null;
                try
                {
                    var prop = sn?.GetType().GetProperty("PublishedAtDateTimeOffset");
                    if (prop?.GetValue(sn) is DateTimeOffset dto)
                        published = dto;
                    else if (sn?.PublishedAt is DateTime dt)
                        published = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                }
                catch { /* optional */ }

                var thumb = sn?.Thumbnails?.Medium?.Url
                    ?? sn?.Thumbnails?.High?.Url
                    ?? sn?.Thumbnails?.Default__?.Url;

                list.Add(new ChannelUploadVideo(
                    videoId.Trim(),
                    title,
                    string.IsNullOrWhiteSpace(sn?.Description) ? null : sn.Description.Trim(),
                    published,
                    thumb));
            }

            pageToken = plResp.NextPageToken;
            if (string.IsNullOrEmpty(pageToken))
                break;
        }

        // Owner videos.list restores real titles/thumbs when playlistItems only said "Private video".
        if (list.Count > 0)
            list = await EnrichUploadsFromVideosListAsync(youtube, list, ct).ConfigureAwait(false);

        return list;
    }

    static async Task<List<ChannelUploadVideo>> EnrichUploadsFromVideosListAsync(
        Google.Apis.YouTube.v3.YouTubeService youtube,
        List<ChannelUploadVideo> list,
        CancellationToken ct)
    {
        var byId = list.ToDictionary(v => v.VideoId, StringComparer.OrdinalIgnoreCase);
        var ids = list.Select(v => v.VideoId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            var req = youtube.Videos.List("snippet");
            req.Id = string.Join(',', batch);
            var resp = await req.ExecuteAsync(ct).ConfigureAwait(false);
            foreach (var v in resp.Items ?? Array.Empty<Google.Apis.YouTube.v3.Data.Video>())
            {
                if (v.Id is null || !byId.ContainsKey(v.Id)) continue;
                var sn = v.Snippet;
                var title = (sn?.Title ?? "").Trim();
                if (title.Length == 0) continue;
                var thumb = sn?.Thumbnails?.Medium?.Url
                    ?? sn?.Thumbnails?.High?.Url
                    ?? sn?.Thumbnails?.Default__?.Url
                    ?? byId[v.Id].ThumbnailUrl;
                DateTimeOffset? published = byId[v.Id].PublishedAt;
                try
                {
                    if (sn?.PublishedAt is DateTime dt)
                        published = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                }
                catch { /* keep prior */ }
                byId[v.Id] = new ChannelUploadVideo(
                    v.Id,
                    title,
                    string.IsNullOrWhiteSpace(sn?.Description) ? byId[v.Id].Description : sn.Description.Trim(),
                    published,
                    thumb);
            }
        }
        // Preserve playlist order
        return list.Select(v => byId.TryGetValue(v.VideoId, out var e) ? e : v).ToList();
    }
}


/// <summary>
/// Persistent SQLite uploader/OAuth token storage backed by <c>pagetomovie.db</c> in persistent <c>/data</c>.
/// Guarantees YouTube OAuth refresh tokens survive app restarts, container updates, and redeploys.
/// </summary>
public sealed class SqliteDataStore : IDataStore
{
    private readonly string _connectionString;

    public SqliteDataStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "pagetomovie.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureTableInitialized();
    }

    private void EnsureTableInitialized()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS oauth_data_store (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
    }

    public Task StoreAsync<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        try
        {
            // Google.Apis TokenResponse expects Newtonsoft shape (same as FileDataStore).
            // System.Text.Json does not round-trip RefreshToken reliably → "lost" after restart.
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(value);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO oauth_data_store (key, value_json, updated_at)
                VALUES (@key, @value_json, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value_json", json);
            cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "SqliteDataStore.StoreAsync failed key={0}: {1}", key, ex.Message);
            throw; // surface failure so OAuth exchange is not silent-success
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM oauth_data_store WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(default(T));
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM oauth_data_store WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(result))
                return Task.FromResult(default(T));

            // Prefer Newtonsoft (Google.Apis FileDataStore compatible). Fallback STJ for older rows.
            try
            {
                var val = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(result);
                if (val is not null)
                    return Task.FromResult(val);
            }
            catch { /* try STJ */ }

            var stj = System.Text.Json.JsonSerializer.Deserialize<T>(result);
            return Task.FromResult(stj);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "SqliteDataStore.GetAsync failed key={0}: {1}", key, ex.Message);
            return Task.FromResult(default(T));
        }
    }

    public Task ClearAsync()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM oauth_data_store";
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }
}
