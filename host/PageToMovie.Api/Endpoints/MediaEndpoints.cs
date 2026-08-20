using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Client media sync: list available project MP4s and sidecars with proxy tickets.</summary>
        app.MapGet("/api/projects/{id}/media/sync", GetProjectsIdMediaSync);
        // <summary>Download a specific media file (MP4 clip or sidecar manifest) from a project.</summary>
        app.MapGet("/api/projects/{id}/media/file", GetProjectsIdMediaFile);
        // <summary>Register client-side media hash (clips/exports) so the server need not store MP4s.</summary>
        app.MapPost("/api/projects/{id}/media/register", PostProjectsIdMediaRegister);
        app.MapGet("/api/projects/{id}/media", GetProjectsIdMedia);
        app.MapGet("/api/media/proxy/{token}", GetMediaProxyToken);
        return app;
    }

    private static async Task<IResult> GetProjectsIdMediaSync(string id,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var projectDir = await store.GetProjectDirAsync(id, ct);
        var list = await CollectProjectMediaFilesAsync(id, projectDir, tickets, ct);
        return Results.Ok(new { ok = true, projectId = id, files = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static readonly HashSet<string> MediaSyncExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".m4v",
        ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac", ".opus",
        ".png", ".jpg", ApiText.JpegExtension, ".webp", ".gif",
    };

    private static async Task<List<object>> CollectProjectMediaFilesAsync(
        string id, string projectDir, MediaProxyTicketStore tickets, CancellationToken ct)
    {
        var list = new List<object>();
        var assetsRoot = Path.Combine(projectDir, ApiText.AssetsFolder);
        if (!Directory.Exists(assetsRoot))
            return list;

        foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
        {
            var item = await TryDescribeMediaFileAsync(id, projectDir, file, tickets, ct);
            if (item is not null)
                list.Add(item);
        }
        foreach (var entry in CollectProviderRecoveryEntries(
                     Path.Combine(assetsRoot, ApiText.VideoFolder),
                     url => tickets.Issue(url, TimeSpan.FromHours(2))))
        {
            list.Add(entry);
        }
        return list;
    }

    /// <summary>One provider-recovery row in the media sync list (serialized camelCase alongside
    /// the regular anonymous entries). <c>ProviderRecovery</c> tells the client to download only
    /// when the clip is missing locally — size/hash are unknown until the provider copy lands.
    /// <c>ProviderLeadInSeconds</c> &gt; 0 marks a combined video-extend copy: its head is the
    /// previous clip (or the full previous chain when extend was chained from a combined file —
    /// C3 = C1+C2+C3). The client slices the new tail out as this clip and hop-walks the head
    /// to recover missing previous clips (the API host never trims).
    /// <c>PredecessorLeadInSeconds</c> is nearest previous first (C2, then C1, …).</summary>
    public sealed record ProviderRecoverySyncEntry(
        string RelativePath, string FileName, long SizeBytes, string? Sha256,
        bool IsMp4, string StreamUrl, bool ProviderRecovery, double ProviderLeadInSeconds,
        IReadOnlyList<double> PredecessorLeadInSeconds);

    private static readonly Regex ClipSidecarNameRx = new(
        @"^scene_(\d{2})_clip_(\d{2}).*\.clip\.json$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Clips whose bytes exist on neither server disk nor (necessarily) the client, but whose
    /// newest sidecar still points at the provider copy (<c>source_url</c>): offered through the
    /// same proxy-ticket stream so a client sync can self-heal a missed live save. Combined
    /// video-extend copies carry their sidecar's lead-in so the client can slice the new tail
    /// out as this clip and recover a missing previous clip from the head.
    /// <paramref name="issueTicket"/> maps a provider URL to a proxy token.
    /// </summary>
    public static List<ProviderRecoverySyncEntry> CollectProviderRecoveryEntries(
        string videoDir, Func<string, string> issueTicket)
    {
        var entries = new List<ProviderRecoverySyncEntry>();
        if (!Directory.Exists(videoDir))
            return entries;

        var seen = new HashSet<(int Scene, int Clip)>();
        foreach (var sidecar in Directory.EnumerateFiles(videoDir, "*.clip.json"))
        {
            var m = ClipSidecarNameRx.Match(Path.GetFileName(sidecar));
            if (!m.Success)
                continue;
            var scene = int.Parse(m.Groups[1].Value);
            var clip = int.Parse(m.Groups[2].Value);
            if (!seen.Add((scene, clip)))
                continue;

            // Any real MP4 for this clip on server disk (take-suffixed included) → the regular
            // listing already covers it; recovery is only for byte-less sidecar-only clips.
            if (Directory.EnumerateFiles(videoDir, $"scene_{scene:D2}_clip_{clip:D2}*.mp4").Any())
                continue;

            var src = ClipProviderSource.ReadForClip(videoDir, scene, clip);
            if (src is null || string.IsNullOrWhiteSpace(src.SourceUrl))
                continue;

            var fileName = $"scene_{scene:D2}_clip_{clip:D2}.mp4";
            entries.Add(new ProviderRecoverySyncEntry(
                RelativePath: $"{ApiText.AssetsFolder}/{ApiText.VideoFolder}/{fileName}",
                FileName: fileName,
                SizeBytes: 0,
                Sha256: null,
                IsMp4: true,
                StreamUrl: $"/api/media/proxy/{issueTicket(src.SourceUrl)}",
                ProviderRecovery: true,
                ProviderLeadInSeconds: src.IsCombined ? src.LeadInSeconds : 0,
                PredecessorLeadInSeconds: CollectPredecessorLeadIns(videoDir, scene, clip)));
        }
        return entries;
    }

    /// <summary>
    /// Combined-sidecar lead-ins nearest-previous first (clip-1 of current, then older).
    /// Each value is one hop — how much of that file is its previous clip. Stops at the
    /// first non-combined sidecar. The client plans which hops still apply to this file's
    /// head (full C1+C2 chain walks; a sliced C2 hop does not put C1 in C3).
    /// </summary>
    public static List<double> CollectPredecessorLeadIns(string videoDir, int scene, int clip)
    {
        var hops = new List<double>();
        for (var c = clip - 1; c >= 1; c--)
        {
            var prev = ClipProviderSource.ReadForClip(videoDir, scene, c);
            if (prev is not { IsCombined: true })
                break;
            hops.Add(prev.LeadInSeconds);
        }
        return hops;
    }

    private static async Task<object?> TryDescribeMediaFileAsync(
        string id, string projectDir, string file, MediaProxyTicketStore tickets, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        if (name is "Thumbs.db" or ".DS_Store")
            return null;
        var ext = Path.GetExtension(file);
        var isClipJson = name.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);
        if (!isClipJson && !MediaSyncExtensions.Contains(ext))
            return null;

        var relPath = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
        var fi = new FileInfo(file);
        var isMp4 = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        var sha256 = await TryHashMediaFileAsync(file, fi.Length, ct);
        var ticketToken = tickets.Issue($"{id}:{relPath}", TimeSpan.FromHours(2));
        var streamUrl = $"/api/projects/{Uri.EscapeDataString(id)}/media/file?path={Uri.EscapeDataString(relPath)}&ticket={ticketToken}";
        return new
        {
            relativePath = relPath,
            fileName = name,
            sizeBytes = fi.Length,
            sha256,
            isMp4,
            streamUrl,
        };
    }

    private static async Task<string?> TryHashMediaFileAsync(string file, long length, CancellationToken ct)
    {
        try
        {
            if (length > 64L * 1024 * 1024)
                return null;
            using var fs = File.OpenRead(file);
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch { /* best-effort sha256 */ }
        return null;
    }

    private static async Task<IResult> GetProjectsIdMediaFile(string id,
    string path,
    string? ticket,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    IHttpClientFactory httpFactory,
    HttpContext httpContext,
    CancellationToken ct)
    {
    var ticketValid = IsValidMediaTicket(id, path, ticket, tickets);
    if (!ticketValid && AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { ok = false, error = "path parameter required" });

        var projectDir = await store.GetProjectDirAsync(id, ct);
        if (!TryResolveSafeMediaPath(projectDir, path, out var fullPath))
            return Results.BadRequest(new { ok = false, error = "Invalid media path" });

        if (!File.Exists(fullPath))
            return await ServeMissingMediaAsync(fullPath, httpFactory, httpContext, ct);

        return Results.File(fullPath, ContentTypeForMediaExtension(fullPath), Path.GetFileName(fullPath), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static bool IsValidMediaTicket(string id, string path, string? ticket, MediaProxyTicketStore tickets)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return false;
        var target = tickets.TryTakeUrl(ticket);
        return target is not null && string.Equals(target, $"{id}:{path}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveSafeMediaPath(string projectDir, string path, out string fullPath)
    {
        var cleanRelPath = path.TrimStart('/', '\\').Replace('\\', '/');
        fullPath = Path.GetFullPath(Path.Combine(projectDir, cleanRelPath.Replace('/', Path.DirectorySeparatorChar)));
        var fullProjDir = Path.GetFullPath(projectDir);
        return fullPath.StartsWith(fullProjDir, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> ServeMissingMediaAsync(
        string fullPath, IHttpClientFactory httpFactory, HttpContext httpContext, CancellationToken ct)
    {
        // Clips do not live on the server: a generated clip is provider-hosted (sidecar
        // source_url) until the browser saves it locally. Stream it through, never store it.
        if (fullPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var served = await TryServeMissingMp4Async(fullPath, httpFactory, httpContext, ct);
            if (served is not null)
                return served;
        }
        return Results.NotFound(new { ok = false, error = "File not found" });
    }

    private static async Task<IResult?> TryServeMissingMp4Async(
        string fullPath, IHttpClientFactory httpFactory, HttpContext httpContext, CancellationToken ct)
    {
        var src = ClipProviderSource.ReadForMp4(fullPath);
        var upstream = src?.SourceUrl;
        // Fakes: the "provider" is a local fixture file — serve it like the proxy does.
        if (!string.IsNullOrWhiteSpace(upstream) && TryServeFixture(upstream) is { } fixtureServed)
            return fixtureServed;
        if (string.IsNullOrWhiteSpace(upstream)
            || !Uri.TryCreate(upstream, UriKind.Absolute, out var up)
            || (up.Scheme != Uri.UriSchemeHttps && up.Scheme != Uri.UriSchemeHttp))
            return null;

        // Video-extend clip: the provider copy is the combined video. Stream it and advertise
        // the lead-in so the browser (ClipSummary.ProviderLeadInSeconds / ffmpeg.wasm) slices
        // the head. The API host never downloads to trim with native ffmpeg.
        if (src!.IsCombined)
        {
            httpContext.Response.Headers[LeadInHeader] = src.LeadInSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        return await ProxyUpstreamMediaAsync(upstream, httpFactory, httpContext, ct);
    }

    private static string ContentTypeForMediaExtension(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => SpecializedMimeType.VideoMp4.ToMimeTypeString(),
            ".json" => JsonKeys.ApplicationJson,
            ".png" => SpecializedMimeType.ImagePng.ToMimeTypeString(),
            ".jpg" or ApiText.JpegExtension => "image/jpeg",
            ".mp3" => SpecializedMimeType.AudioMpeg.ToMimeTypeString(),
            ".wav" => SpecializedMimeType.AudioWav.ToMimeTypeString(),
            ".m4a" => "audio/mp4",
            ".webm" => "audio/webm",
            _ => SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString()
        };
    }

    private static async Task<IResult> PostProjectsIdMediaRegister(string id,
    MediaRegisterRequest body,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaRegistryService media,
    ProjectStore store,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Sha256) || string.IsNullOrWhiteSpace(body.RelativePath))
            return Results.BadRequest(new { ok = false, error = "relativePath and sha256 required" });

        var dto = await media.UpsertAsync(
            id,
            body.RelativePath,
            body.Sha256,
            body.SizeBytes,
            body.Kind ?? "clip",
            body.Scene,
            body.Clip,
            user.UserId,
            ct);

        await MaybeOffloadServerMediaAsync(id, dto, user.UserId, store, ct);
        return Results.Ok(new { ok = true, media = dto });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task MaybeOffloadServerMediaAsync(
        string id, MediaObjectDto dto, string? userId, ProjectStore store, CancellationToken ct)
    {
        // Character reference images are kept server-side (small; Cast readiness + thumbnails depend on
        // the ref file surviving reload). Client-storage offload is for large video clips only.
        var isCharacterImage = dto.RelativePath.Replace('\\', '/')
            .Contains("assets/characters/", StringComparison.OrdinalIgnoreCase);

        // Sidecar so scene lists treat clip as present without server MP4.
        try
        {
            var dir = await store.GetProjectDirAsync(id, ct);
            var rel = dto.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");

            // Clips: the user's folder + the provider (sidecar source_url / file_id) are the durable
            // homes; the server copy is released once the browser has it. (The former
            // "keep_media_on_server" opt-out for curated sources is retired — forks and shares
            // reference clips by source_url / file_id, and the demo gallery keeps its own movie.mp4.)
            // Only the media bytes themselves are offloaded. Sidecars (.clip.json), markers and any
            // other metadata the sync also mirrors must stay: the sidecar is the project's only
            // pointer to the provider-hosted video — deleting it after a sync made the clips vanish
            // server-side (Mary19, 2026-08-19).
            var ext = Path.GetExtension(full);
            var isMediaBytes = MediaSyncExtensions.Contains(ext);
            if (!isCharacterImage && isMediaBytes)
                await WriteClientStorageMarkerAndDeleteServerCopyAsync(full, dto, userId, ct);

            store.InvalidateSceneListCache(id);
        }
        catch { /* non-fatal */ }
    }

    private static async Task WriteClientStorageMarkerAndDeleteServerCopyAsync(
        string full, MediaObjectDto dto, string? userId, CancellationToken ct)
    {
        var marker = full + ".client.json";
        await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(new
        {
            storage = "client",
            sha256 = dto.Sha256,
            sizeBytes = dto.SizeBytes,
            registeredAt = dto.CreatedAt,
            userId,
        }) + "\n", ct);

        // Reclaim server volume storage: if server MP4 exists and matches verified client registration size, delete server copy.
        if (!File.Exists(full))
            return;
        var fi = new FileInfo(full);
        if (dto.SizeBytes <= 0 || fi.Length == dto.SizeBytes)
            File.Delete(full);
    }

    private static async Task<IResult> GetProjectsIdMedia(string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaRegistryService media,
    ProjectStore store,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var list = await media.ListProjectAsync(id, ct);
        return Results.Ok(new { ok = true, media = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetMediaProxyToken(string token,
    MediaProxyTicketStore tickets,
    IHttpClientFactory httpFactory,
    HttpContext httpContext,
    CancellationToken ct)
    {
    var url = tickets.TryTakeUrl(token);
    if (string.IsNullOrWhiteSpace(url))
        return Results.NotFound(new { ok = false, error = "Media ticket expired or invalid" });

    if (TryServeDataUrl(url) is { } dataResult)
        return dataResult;
    if (TryServeFixture(url) is { } fixtureResult)
        return fixtureResult;
    return await ProxyUpstreamMediaAsync(url, httpFactory, httpContext, ct);
}

    private static IResult? TryServeDataUrl(string url)
    {
        // Inline provider bytes (e.g. ElevenLabs Music streams audio back rather than hosting a URL):
        // decode the self-contained data: URL and serve it, so no media is persisted on the API host.
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        var comma = url.IndexOf(',');
        var meta = comma > 0 ? url[5..comma] : "";
        if (comma < 0 || !meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, error = "Unsupported data URL" });
        var dataCtype = meta.Split(';')[0];
        if (string.IsNullOrWhiteSpace(dataCtype)) dataCtype = SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString();
        byte[] dataBytes;
        try { dataBytes = Convert.FromBase64String(url[(comma + 1)..]); }
        catch { return Results.BadRequest(new { ok = false, error = "Malformed data URL" }); }
        string ext;
        if (dataCtype.Contains("mpeg", StringComparison.OrdinalIgnoreCase))
            ext = ".mp3";
        else if (dataCtype.Contains("wav", StringComparison.OrdinalIgnoreCase))
            ext = ".wav";
        else
            ext = ".bin";
        return Results.Bytes(dataBytes, contentType: dataCtype, fileDownloadName: "track" + ext);
    }

    /// <summary>Serve a fixture:/local: URL from disk (fakes fixtures); null for other URLs.</summary>
    internal static IResult? TryServeFixtureUrl(string url) => TryServeFixture(url);

    private static IResult? TryServeFixture(string url)

    {
        // Fakes-mode local fixture (no upstream provider to fetch from) — same ticket
        // mechanism as a real provider URL, just served from disk instead of proxied over HTTP.
        // "local:" is the same serving path (legacy tickets); jobs no longer issue trimmed copies.
        var isLocal = url.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
        if (!isLocal && !url.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase))
            return null;
        var fixturePath = isLocal ? url["local:".Length..] : url["fixture:".Length..];
        if (!File.Exists(fixturePath))
            return Results.NotFound(new { ok = false, error = "Fixture file not found" });
        var fixtureCtype = Path.GetExtension(fixturePath).ToLowerInvariant() switch
        {
            ".wav" => SpecializedMimeType.AudioWav.ToMimeTypeString(),
            ".mp3" => SpecializedMimeType.AudioMpeg.ToMimeTypeString(),
            ".mp4" => SpecializedMimeType.VideoMp4.ToMimeTypeString(),
            _ => SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString(),
        };
        var fixtureStream = File.OpenRead(fixturePath);
        return Results.Stream(fixtureStream, contentType: fixtureCtype, fileDownloadName: Path.GetFileName(fixturePath));
    }

    /// <summary>Set on a streamed provider copy that still carries the previous clip at its head (seconds).</summary>
    public const string LeadInHeader = "X-PTM-Lead-In-Seconds";

    internal static async Task<IResult> ProxyUpstreamMediaAsync(
        string url, IHttpClientFactory httpFactory, HttpContext httpContext, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("media-proxy");
        HttpResponseMessage? resp = null;
        try
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                return Results.Json(new { ok = false, error = $"Upstream HTTP {code}" }, statusCode: code);
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? SpecializedMimeType.VideoMp4.ToMimeTypeString();
            // Results.Stream has no completion callback — RegisterForDisposeAsync guarantees resp is
            // disposed once the response body finishes writing, on every exit path (success or client abort).
            httpContext.Response.RegisterForDispose(resp);
            resp = null;
            return Results.Stream(stream, contentType: ctype, fileDownloadName: "clip.mp4");
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
        finally
        {
            resp?.Dispose();
        }
    }
}
