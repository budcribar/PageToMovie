using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Trusted media registry: SHA-256 of gen clips / client-registered exports.
/// Bytes live on the client folder; server stores hashes + paths only (optional server file still allowed).
/// </summary>
public sealed class MediaRegistryService
{
    private readonly string _dbPath;
    private readonly ILogger<MediaRegistryService> _log;
    private readonly object _initLock = new();
    private bool _initialized;

    public MediaRegistryService(
        IOptions<PageToMovieOptions> options,
        ILogger<MediaRegistryService>? logger = null)
    {
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaRegistryService>.Instance;
        var workspace = options?.Value?.WorkspaceRoot;
        var dataDir = UserDatabaseService.ResolveDataDirectory(workspace);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");
        EnsureInitialized();
    }

    private string ConnectionString => $"Data Source={_dbPath};Cache=Shared;";

    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS media_objects (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL DEFAULT 0,
                    kind TEXT NOT NULL DEFAULT 'clip',
                    scene INTEGER,
                    clip INTEGER,
                    user_id TEXT,
                    created_at TEXT NOT NULL,
                    UNIQUE(project_id, relative_path)
                );
                CREATE INDEX IF NOT EXISTS idx_media_sha ON media_objects(sha256);
                CREATE INDEX IF NOT EXISTS idx_media_project ON media_objects(project_id);
                CREATE INDEX IF NOT EXISTS idx_media_project_path ON media_objects(project_id, relative_path);
            ";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public static string ClipRelativePath(int scene, int clip) =>
        $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";

    /// <summary>
    /// Per-clip TTS audio for narrator re-voice (server speak-batch → client media folder).
    /// </summary>
    public static string RevoiceAudioRelativePath(int scene, int clip, string ext = ".mp3")
    {
        var e = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.Trim();
        if (!e.StartsWith('.')) e = "." + e;
        return $"assets/audio/revoice/scene_{scene:D2}_clip_{clip:D2}{e.ToLowerInvariant()}";
    }

    /// <summary>
    /// Per-segment cloned-voice TTS for movie-wide voice substitution. A clip can hold more than one
    /// spoken line (multi-speaker / back-to-back lines), so each line's audio is keyed by its segment
    /// index alongside the single-line <see cref="RevoiceAudioRelativePath"/>.
    /// </summary>
    public static string RevoiceSegmentAudioRelativePath(int scene, int clip, int segment, string ext = ".mp3")
    {
        var e = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.Trim();
        if (!e.StartsWith('.')) e = "." + e;
        return $"assets/audio/revoice/scene_{scene:D2}_clip_{clip:D2}_seg_{segment:D2}{e.ToLowerInvariant()}";
    }

    /// <summary>
    /// Whole-scene cloned-voice narration: one continuous read of all the scene's narrator lines,
    /// synthesized in a single TTS call and overlaid onto the stitched scene (current strategy).
    /// </summary>
    public static string RevoiceSceneAudioRelativePath(int scene, string ext = ".mp3")
    {
        var e = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.Trim();
        if (!e.StartsWith('.')) e = "." + e;
        return $"assets/audio/revoice/scene_{scene:D2}{e.ToLowerInvariant()}";
    }

    /// <summary>One narrator line within a scene, synthesized on its own so the client can place +
    /// time-stretch it onto the detected speech window.</summary>
    public static string RevoiceSceneLineAudioRelativePath(int scene, int line, string ext = ".mp3")
    {
        var e = string.IsNullOrWhiteSpace(ext) ? ".mp3" : ext.Trim();
        if (!e.StartsWith('.')) e = "." + e;
        return $"assets/audio/revoice/scene_{scene:D2}_line_{line:D2}{e.ToLowerInvariant()}";
    }

    /// <summary>One background-music segment (IAudioClient.MaxSegmentDurationSeconds-sized) for a
    /// scene — most scenes need only segment 1; longer scenes concatenate client-side.</summary>
    public static string MusicSegmentRelativePath(int scene, int segment) =>
        $"assets/music/scene_{scene:D2}_seg_{segment:D2}.wav";

    public static string NormalizeSha256(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "";
        return hex.Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string HashBytes(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    public async Task<MediaObjectDto> UpsertAsync(
        string projectId,
        string relativePath,
        string sha256,
        long sizeBytes,
        string kind,
        int? scene,
        int? clip,
        string? userId,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        projectId = projectId.Trim();
        relativePath = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        sha256 = NormalizeSha256(sha256);
        if (sha256.Length != 64)
            throw new InvalidOperationException("sha256 must be 64 hex characters");
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("projectId and relativePath required");

        var ts = DateTimeOffset.UtcNow.ToString("o");
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO media_objects (project_id, relative_path, sha256, size_bytes, kind, scene, clip, user_id, created_at)
            VALUES (@p, @path, @sha, @sz, @kind, @sc, @cl, @uid, @ts)
            ON CONFLICT(project_id, relative_path) DO UPDATE SET
                sha256 = excluded.sha256,
                size_bytes = excluded.size_bytes,
                kind = excluded.kind,
                scene = excluded.scene,
                clip = excluded.clip,
                user_id = excluded.user_id,
                created_at = excluded.created_at;
        ";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@sha", sha256);
        cmd.Parameters.AddWithValue("@sz", sizeBytes);
        cmd.Parameters.AddWithValue("@kind", string.IsNullOrWhiteSpace(kind) ? "clip" : kind.Trim());
        cmd.Parameters.AddWithValue("@sc", (object?)scene ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cl", (object?)clip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", ts);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Sidecar marker so scene lists treat clip as present without server MP4 bytes.
        try
        {
            // Caller may also write project-relative marker; registry is source of truth for hash.
        }
        catch { /* ignore */ }

        _log.LogInformation("Media registered {Project}/{Path} sha={Sha}…", projectId, relativePath, sha256[..12]);
        return new MediaObjectDto
        {
            ProjectId = projectId,
            RelativePath = relativePath,
            Sha256 = sha256,
            SizeBytes = sizeBytes,
            Kind = kind,
            Scene = scene,
            Clip = clip,
            UserId = userId,
            CreatedAt = ts,
        };
    }

    public async Task<bool> HasClipAsync(string projectId, int scene, int clip, CancellationToken ct = default)
    {
        var path = ClipRelativePath(scene, clip);
        var row = await TryGetAsync(projectId, path, ct).ConfigureAwait(false);
        return row is not null;
    }

    /// <summary>Any background-music segment registered for this scene (client-synced — the
    /// server no longer keeps the audio bytes, only the registry row proving it was saved).</summary>
    public async Task<bool> HasSceneMusicAsync(string projectId, int scene, CancellationToken ct = default)
    {
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM media_objects
            WHERE project_id = @p AND scene = @sc AND kind = 'music' LIMIT 1";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@sc", scene);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>
    /// Mirror a renumber pass (clip/scene reorder) into the registry: rewrite each row's
    /// relative_path and its scene/clip columns, and drop rows for files the pass deleted.
    /// Two-phase (tmp-prefixed paths first) inside one transaction so swap cycles never trip the
    /// UNIQUE(project_id, relative_path) constraint.
    /// </summary>
    public async Task RenamePathsAsync(
        string projectId,
        IReadOnlyList<MediaRenameEntry> renames,
        IReadOnlyList<string> deletes,
        CancellationToken ct = default)
    {
        if (renames.Count == 0 && deletes.Count == 0) return;
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string tmpPrefix = "renumtmp::";
        foreach (var r in renames)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE media_objects SET relative_path = @tmp
                WHERE project_id = @p AND relative_path = @from";
            cmd.Parameters.AddWithValue("@p", projectId);
            cmd.Parameters.AddWithValue("@from", r.From.Replace('\\', '/'));
            cmd.Parameters.AddWithValue("@tmp", tmpPrefix + r.To.Replace('\\', '/'));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var r in renames)
        {
            var to = r.To.Replace('\\', '/');
            var m = CommonRegex.Match(
                Path.GetFileName(to), @"^scene_(\d{2,})(?:_clip_(\d{2,}))?(?=[._])");
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE media_objects SET relative_path = @to, scene = @sc, clip = @cl
                WHERE project_id = @p AND relative_path = @tmp";
            cmd.Parameters.AddWithValue("@p", projectId);
            cmd.Parameters.AddWithValue("@tmp", tmpPrefix + to);
            cmd.Parameters.AddWithValue("@to", to);
            cmd.Parameters.AddWithValue("@sc", m.Success ? int.Parse(m.Groups[1].Value) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@cl", m is { Success: true } && m.Groups[2].Success
                ? int.Parse(m.Groups[2].Value) : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var d in deletes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM media_objects WHERE project_id = @p AND relative_path = @path";
            cmd.Parameters.AddWithValue("@p", projectId);
            cmd.Parameters.AddWithValue("@path", d.Replace('\\', '/'));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<MediaObjectDto?> TryGetAsync(string projectId, string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();
        relativePath = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT project_id, relative_path, sha256, size_bytes, kind, scene, clip, user_id, created_at
            FROM media_objects WHERE project_id = @p AND relative_path = @path LIMIT 1";
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        cmd.Parameters.AddWithValue("@path", relativePath);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return Read(r);
    }

    public async Task<bool> IsTrustedShaAsync(string projectId, string sha256, CancellationToken ct = default)
    {
        EnsureInitialized();
        sha256 = NormalizeSha256(sha256);
        if (sha256.Length != 64) return false;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM media_objects
            WHERE project_id = @p AND sha256 = @sha LIMIT 1";
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        cmd.Parameters.AddWithValue("@sha", sha256);
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return o is not null && o is not DBNull;
    }

    /// <summary>All registered takes (client and/or server) for one scene/clip, newest first.</summary>
    public async Task<List<MediaObjectDto>> ListForClipAsync(string projectId, int scene, int clip, CancellationToken ct = default)
    {
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT project_id, relative_path, sha256, size_bytes, kind, scene, clip, user_id, created_at
            FROM media_objects WHERE project_id = @p AND scene = @sc AND clip = @cl
            ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        cmd.Parameters.AddWithValue("@sc", scene);
        cmd.Parameters.AddWithValue("@cl", clip);
        var list = new List<MediaObjectDto>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Read(r));
        return list;
    }

    /// <summary>All registered music segments (client and/or server) for one scene, newest first —
    /// the audio equivalent of <see cref="ListForClipAsync"/>.</summary>
    public async Task<List<MediaObjectDto>> ListForSceneMusicAsync(string projectId, int scene, CancellationToken ct = default)
    {
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT project_id, relative_path, sha256, size_bytes, kind, scene, clip, user_id, created_at
            FROM media_objects WHERE project_id = @p AND scene = @sc AND kind = 'music'
            ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        cmd.Parameters.AddWithValue("@sc", scene);
        var list = new List<MediaObjectDto>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Read(r));
        return list;
    }

    public async Task<List<MediaObjectDto>> ListProjectAsync(string projectId, CancellationToken ct = default)
    {
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT project_id, relative_path, sha256, size_bytes, kind, scene, clip, user_id, created_at
            FROM media_objects WHERE project_id = @p ORDER BY scene, clip, relative_path";
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        var list = new List<MediaObjectDto>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Read(r));
        return list;
    }

    private static MediaObjectDto Read(SqliteDataReader r) => new()
    {
        ProjectId = r.GetString(0),
        RelativePath = r.GetString(1),
        Sha256 = r.GetString(2),
        SizeBytes = r.GetInt64(3),
        Kind = r.GetString(4),
        Scene = r.IsDBNull(5) ? null : r.GetInt32(5),
        Clip = r.IsDBNull(6) ? null : r.GetInt32(6),
        UserId = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedAt = r.GetString(8),
    };
}

public sealed class MediaObjectDto
{
    public string ProjectId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Kind { get; set; } = "clip";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? UserId { get; set; }
    public string? CreatedAt { get; set; }
}

/// <summary>Short-lived map of opaque tokens → provider video URLs for client download (CORS-safe proxy).</summary>
public sealed class MediaProxyTicketStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, DateTimeOffset Exp)> _map = new();

    public string Issue(string videoUrl, TimeSpan? ttl = null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var exp = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(45));
        _map[token] = (videoUrl, exp);
        // opportunistic purge
        if (_map.Count > 500)
        {
            foreach (var kv in _map)
            {
                if (kv.Value.Exp < DateTimeOffset.UtcNow)
                    _map.TryRemove(kv.Key, out _);
            }
        }
        return token;
    }

    public string? TryTakeUrl(string token)
    {
        if (!_map.TryGetValue(token, out var e)) return null;
        if (e.Exp < DateTimeOffset.UtcNow)
        {
            _map.TryRemove(token, out _);
            return null;
        }
        return e.Url;
    }
}
