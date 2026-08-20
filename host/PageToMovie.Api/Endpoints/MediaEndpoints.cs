using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
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
                     (url, fileId, projectDirHint, scene, clip, modelId, providerId) => tickets.Issue(
                         url ?? "", TimeSpan.FromHours(2), fileId,
                         projectDir: projectDirHint, scene: scene, clip: clip,
                         modelId: modelId, providerId: providerId)))
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
    /// newest sidecar still points at the provider copy (<c>source_url</c> and/or
    /// <c>source_file_id</c>): offered through the same proxy-ticket stream so a client sync
    /// can self-heal a missed live save. Durable playback is <c>file_output.public_url</c>
    /// (persisted as <c>source_url</c>); vidgen poll links expire; Files <c>file_id</c> is
    /// downloaded through <see cref="IVideoClient"/> for the clip's catalog video model.
    /// Combined video-extend copies carry their sidecar's lead-in so the client can slice
    /// the new tail out as this clip and recover a missing previous clip from the head.
    /// <paramref name="issueTicket"/> maps (url, file_id, projectDir, scene, clip, model, provider) to a proxy token.
    /// </summary>
    public delegate string IssueRecoveryTicket(
        string? url, string? fileId, string? projectDir, int scene, int clip,
        string? modelId, string? providerId);

    public static List<ProviderRecoverySyncEntry> CollectProviderRecoveryEntries(
        string videoDir, Func<string?, string?, string> issueTicket) =>
        CollectProviderRecoveryEntries(
            videoDir, (url, fileId, _, _, _, _, _) => issueTicket(url, fileId));

    public static List<ProviderRecoverySyncEntry> CollectProviderRecoveryEntries(
        string videoDir, Func<string?, string?, string?, int, int, string> issueTicket) =>
        CollectProviderRecoveryEntries(
            videoDir, (url, fileId, projectDir, scene, clip, _, _) =>
                issueTicket(url, fileId, projectDir, scene, clip));

    public static List<ProviderRecoverySyncEntry> CollectProviderRecoveryEntries(
        string videoDir, IssueRecoveryTicket issueTicket)
    {
        var entries = new List<ProviderRecoverySyncEntry>();
        if (!Directory.Exists(videoDir))
            return entries;

        var projectDir = ClipForkFallback.ProjectDirFromVideoDir(videoDir);
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
            if (src is null || !src.HasProviderCopy)
                continue;

            var modelId = CatalogApiKey.ResolveVideoModel(
                src.Model, CatalogApiKey.TryReadProjectVideoModel(projectDir));
            var providerId = CatalogApiKey.ProviderIdForVideo(modelId, src.SourceProvider);

            var fileName = $"scene_{scene:D2}_clip_{clip:D2}.mp4";
            entries.Add(new ProviderRecoverySyncEntry(
                RelativePath: $"{ApiText.AssetsFolder}/{ApiText.VideoFolder}/{fileName}",
                FileName: fileName,
                SizeBytes: 0,
                Sha256: null,
                IsMp4: true,
                StreamUrl: $"/api/media/proxy/{issueTicket(src.SourceUrl, src.SourceFileId, projectDir, scene, clip, modelId, providerId)}",
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
    IVideoClient video,
    IUserApiKeyProvider keys,
    ILoggerFactory logFactory,
    HttpContext httpContext,
    CancellationToken ct)
    {
    var ticketValid = TryValidateMediaTicket(id, path, ticket, tickets, out var ticketKeyUserId);
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
        {
            var src = ClipProviderSource.ReadForMp4(fullPath);
            var cfg = await store.GetConfigAsync(id, ct).ConfigureAwait(false);
            var modelId = CatalogApiKey.ResolveVideoModel(src?.Model, ProjectModelSelection.TryVideo(cfg));
            var providerId = CatalogApiKey.ProviderIdForVideo(modelId, src?.SourceProvider);
            var key = await ResolveTicketVideoKeyAsync(keys, ticketKeyUserId, modelId, providerId, ct)
                .ConfigureAwait(false);
            using (CatalogApiKey.PushKey(providerId, key))
            using (UserApiCallScope.Push(ticketKeyUserId))
                return await ServeMissingMediaAsync(fullPath, httpFactory, video, modelId, httpContext, logFactory, ct);
        }

        return Results.File(fullPath, ContentTypeForMediaExtension(fullPath), Path.GetFileName(fullPath), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static bool TryValidateMediaTicket(
        string id, string path, string? ticket, MediaProxyTicketStore tickets, out string? keyUserId)
    {
        keyUserId = null;
        if (string.IsNullOrWhiteSpace(ticket))
            return false;
        if (!tickets.TryTake(ticket, out var target, out _, out keyUserId))
            return false;
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
        string fullPath, IHttpClientFactory httpFactory, IVideoClient video, string? model,
        HttpContext httpContext, ILoggerFactory logFactory, CancellationToken ct)
    {
        // Clips do not live on the server: a generated clip is provider-hosted (sidecar
        // source_url / source_file_id) until the browser saves it locally. Stream it through, never store it.
        if (fullPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var served = await TryServeMissingMp4Async(fullPath, httpFactory, video, model, httpContext, logFactory, ct);
            if (served is not null)
                return served;
        }
        return Results.NotFound(new { ok = false, error = "File not found" });
    }

    private static async Task<IResult?> TryServeMissingMp4Async(
        string fullPath, IHttpClientFactory httpFactory, IVideoClient video, string? model,
        HttpContext httpContext, ILoggerFactory logFactory, CancellationToken ct)
    {
        var src = ClipProviderSource.ReadForMp4(fullPath);
        if (src is null || !src.HasProviderCopy)
            return null;

        // Video-extend clip: the provider copy is the combined video. Stream it and advertise
        // the lead-in so the browser (ClipSummary.ProviderLeadInSeconds / ffmpeg.wasm) slices
        // the head. The API host never downloads to trim with native ffmpeg.
        if (src.IsCombined)
        {
            httpContext.Response.Headers[LeadInHeader] = src.LeadInSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        var videoDir = Path.GetDirectoryName(fullPath);
        ClipFileNaming.TryParseSceneClip(Path.GetFileName(fullPath), out var scene, out var clip);
        return await StreamProviderCopyAsync(
            src.SourceUrl, src.SourceFileId, httpFactory, httpContext, ct,
            new StreamProviderCopyOptions(
                Video: video,
                Model: model,
                LogFactory: logFactory,
                RecoverAfterProvider: (_, _, _) => Task.FromResult(
                    TryRecoverHostedCopy(ClipForkFallback.ProjectDirFromVideoDir(videoDir), scene, clip))));
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
        // Character and location look plates stay on the server (small; Cast/Locations readiness
        // + thumbnails + wipe-resync depend on the bytes). Client-storage offload is for video.
        var keepLookOnServer = ProjectAssetNaming.IsServerRetainedLookPath(dto.RelativePath);

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
            if (!keepLookOnServer && isMediaBytes)
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

    private sealed record MediaProxyTokenServices(
        MediaProxyTicketStore Tickets,
        IHttpClientFactory HttpFactory,
        IVideoClient Video,
        IUserApiKeyProvider Keys,
        ILoggerFactory LogFactory,
        HttpContext HttpContext);

    private static async Task<IResult> GetMediaProxyToken(
        string token,
        [AsParameters] MediaProxyTokenServices svc,
        CancellationToken ct)
    {
    if (!svc.Tickets.TryTake(token, out var url, out var fileId, out var keyUserId,
            out var projectDir, out var scene, out var clip, out var ticketModel, out var ticketProvider)
        || (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(fileId)))
        return Results.NotFound(new { ok = false, error = "Media ticket expired or invalid" });

    if (!string.IsNullOrWhiteSpace(url) && TryServeDataUrl(url) is { } dataResult)
        return dataResult;

    // Resolve first, then Push on this caller's ExecutionContext. Pushing inside an
    // async helper does not stick — ApiKeyScope is AsyncLocal and is restored after await.
    var modelId = CatalogApiKey.ResolveVideoModel(
        ticketModel, CatalogApiKey.TryReadProjectVideoModel(projectDir));
    var providerId = CatalogApiKey.ProviderIdForVideo(modelId, ticketProvider);
    var key = await ResolveTicketVideoKeyAsync(svc.Keys, keyUserId, modelId, providerId, ct)
        .ConfigureAwait(false);
    using (CatalogApiKey.PushKey(providerId, key))
    using (UserApiCallScope.Push(keyUserId))
        return await StreamProviderCopyAsync(
            url, fileId, svc.HttpFactory, svc.HttpContext, ct,
            new StreamProviderCopyOptions(
                Video: svc.Video,
                Model: modelId,
                LogFactory: svc.LogFactory,
                RecoverAfterProvider: (_, _, _) => Task.FromResult(
                    TryRecoverHostedCopy(projectDir, scene, clip))));
}

    /// <summary>
    /// Key for the user who issued the ticket (media-sync JS fetch has no JWT), using the
    /// same catalog provider id video generation uses for the clip's video model.
    /// Never <c>GetKeyAsync(null, …)</c> and never a hardcoded provider name.
    /// </summary>
    internal static Task<string?> ResolveTicketVideoKeyAsync(
        IUserApiKeyProvider? keys,
        string? keyUserId,
        string? modelId,
        string? providerId,
        CancellationToken ct)
    {
        var provider = CatalogApiKey.ProviderIdForVideo(modelId, providerId);
        return CatalogApiKey.GetKeyAsync(keys, keyUserId, provider, ct);
    }

    /// <summary>
    /// Catalog-routed stored-file client plus optional logger and Railway
    /// hosted-copy recovery for the DI wrapper.
    /// </summary>
    internal sealed record StreamProviderCopyOptions(
        IVideoClient Video,
        string? Model = null,
        ILoggerFactory? LogFactory = null,
        Func<string?, Exception?, CancellationToken, Task<IResult?>>? RecoverAfterProvider = null);

    /// <summary>
    /// Stream the provider copy: public URL first, then the catalog-routed
    /// <see cref="IVideoClient"/> stored-file path. xAI models reuse
    /// <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/> (the only Files
    /// content GET — do not add another). Combined-extend slicing/hop-walk is
    /// unchanged — the same bytes (combined file) are streamed either way.
    /// </summary>
    internal static Task<IResult> StreamProviderCopyAsync(
        string? url,
        string? fileId,
        IHttpClientFactory httpFactory,
        HttpContext httpContext,
        CancellationToken ct,
        StreamProviderCopyOptions options) =>
        StreamProviderCopyAsync(
            url,
            fileId,
            (u, token) => TryOpenHttpOrFixtureAsync(u, httpFactory, httpContext, token),
            (id, token) => TryOpenStoredFileAsync(options.Video, options.Model, id, httpContext, token),
            ct,
            options.LogFactory?.CreateLogger("MediaProxy"),
            options.RecoverAfterProvider);

    /// <summary>
    /// Positional <paramref name="video"/> / <paramref name="model"/> form used by tests.
    /// </summary>
    internal static Task<IResult> StreamProviderCopyAsync(
        string? url,
        string? fileId,
        IHttpClientFactory httpFactory,
        IVideoClient video,
        string? model,
        HttpContext httpContext,
        CancellationToken ct) =>
        StreamProviderCopyAsync(
            url, fileId, httpFactory, httpContext, ct,
            new StreamProviderCopyOptions(Video: video, Model: model));

    /// <summary>Test hook: URL then file_id openers. A file_id failure is a visible error,
    /// not a silent <c>File not found</c>. <paramref name="recoverAfterProvider"/> is the
    /// Railway hosted-copy / <c>.need-fork</c> path when the provider file cannot be downloaded.</summary>
    internal static async Task<IResult> StreamProviderCopyAsync(
        string? url,
        string? fileId,
        Func<string, CancellationToken, Task<IResult?>> openUrl,
        Func<string, CancellationToken, Task<IResult?>> openFileId,
        CancellationToken ct,
        ILogger? log = null,
        Func<string?, Exception?, CancellationToken, Task<IResult?>>? recoverAfterProvider = null)
    {
        try
        {
            var streamed = await ClipProviderSource.TryOpenAsync(
                url, fileId, openUrl, openFileId, ct).ConfigureAwait(false);
            if (streamed is not null)
                return streamed;
            if (recoverAfterProvider is not null
                && await recoverAfterProvider(fileId, null, ct).ConfigureAwait(false) is { } hosted)
                return hosted;
            if (!string.IsNullOrWhiteSpace(fileId))
                return Results.Json(
                    new { ok = false, error = "Provider file could not be opened" },
                    statusCode: StatusCodes.Status404NotFound);
            return Results.NotFound(new { ok = false, error = "File not found" });
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(fileId))
        {
            var detail = TrimForError(ex.Message, 400);
            log?.LogWarning(ex, "Provider file content failed for {FileId}: {Error}", fileId, detail);
            if (recoverAfterProvider is not null
                && await recoverAfterProvider(fileId, ex, ct).ConfigureAwait(false) is { } hosted)
                return hosted;
            return Results.Json(
                new { ok = false, error = "Provider file download failed: " + detail },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Serve a Railway-protected fork copy, or mark <c>.need-fork</c> so the owner can push one.</summary>
    internal static IResult? TryRecoverHostedCopy(string? projectDir, int scene, int clip)
    {
        var hosted = ClipForkFallback.TryProtectedMp4Path(projectDir, scene, clip);
        if (hosted is not null)
            return Results.File(hosted, SpecializedMimeType.VideoMp4.ToMimeTypeString(), Path.GetFileName(hosted), enableRangeProcessing: true);
        ClipForkFallback.TryMarkNeeded(projectDir, scene, clip);
        return null;
    }

    private static Task<IResult?> TryOpenHttpOrFixtureAsync(
        string url, IHttpClientFactory httpFactory, HttpContext httpContext, CancellationToken ct)
    {
        // Missing fixture → null so source_file_id can still recover the clip.
        if (TryParseFixturePath(url, out var fixturePath) && File.Exists(fixturePath)
            && TryServeFixture(url) is { } fixture)
            return Task.FromResult<IResult?>(fixture);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var up)
            || (up.Scheme != Uri.UriSchemeHttps && up.Scheme != Uri.UriSchemeHttp))
            return Task.FromResult<IResult?>(null);
        return TryProxyUpstreamMediaAsync(url, httpFactory, httpContext, ct);
    }

    private const string LocalUrlPrefix = "local:";
    private const string FixtureUrlPrefix = "fixture:";

    private static bool TryParseFixturePath(string url, out string path)
    {
        path = "";
        if (url.StartsWith(LocalUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[LocalUrlPrefix.Length..];
            return path.Length > 0;
        }
        if (url.StartsWith(FixtureUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[FixtureUrlPrefix.Length..];
            return path.Length > 0;
        }
        return false;
    }

    /// <summary>
    /// Catalog-routed stored-file open. xAI adapters call
    /// <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/> (the only Files content GET).
    /// Failures throw so the proxy can log the real provider status instead of <c>File not found</c>.
    /// </summary>
    private static async Task<IResult?> TryOpenStoredFileAsync(
        IVideoClient video, string? model, string fileId, HttpContext httpContext, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return null;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Video: model is required to open a stored provider file. Open Settings and choose a Video generation model.");
        var stream = await video.OpenStoredFileStreamAsync(fileId, model, ct).ConfigureAwait(false);
        if (stream is null)
            return null;
        httpContext.Response.RegisterForDispose(stream);
        return Results.Stream(
            stream,
            contentType: SpecializedMimeType.VideoMp4.ToMimeTypeString(),
            fileDownloadName: "clip.mp4");
    }

    private static string TrimForError(string s, int n)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Length <= n)
            return s;
        return s[..n];
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
        // local: is the same serving path (legacy tickets); jobs no longer issue trimmed copies.
        if (!TryParseFixturePath(url, out var fixturePath))
            return null;
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
        var ok = await TryProxyUpstreamMediaAsync(url, httpFactory, httpContext, ct).ConfigureAwait(false);
        return ok ?? Results.Json(new { ok = false, error = "Upstream HTTP 404" }, statusCode: 404);
    }

    /// <summary>Proxy the public provider URL. Returns null on 404 / non-success so callers
    /// can fall back to <c>source_file_id</c>.</summary>
    internal static async Task<IResult?> TryProxyUpstreamMediaAsync(
        string url, IHttpClientFactory httpFactory, HttpContext httpContext, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("media-proxy");
        HttpResponseMessage? resp = null;
        try
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? SpecializedMimeType.VideoMp4.ToMimeTypeString();
            // Results.Stream has no completion callback — RegisterForDisposeAsync guarantees resp is
            // disposed once the response body finishes writing, on every exit path (success or client abort).
            httpContext.Response.RegisterForDispose(resp);
            resp = null;
            return Results.Stream(stream, contentType: ctype, fileDownloadName: "clip.mp4");
        }
        catch
        {
            return null;
        }
        finally
        {
            resp?.Dispose();
        }
    }
}
