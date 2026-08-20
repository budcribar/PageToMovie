using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Api;

public static partial class SceneClipEndpoints
{
    public sealed record SidecarServices(
        ProjectStore Store,
        IUserContext User,
        IOptions<PageToMovieOptions> Opts);

    public sealed record ClipUploadServices(
        HttpContext HttpContext,
        ProjectStore Store,
        IServiceProvider Services);

    public sealed record ClipVideoServices(
        ProjectStore Store,
        IUserContext User,
        IOptions<PageToMovieOptions> Opts,
        IHttpClientFactory HttpFactory,
        XaiResponsesClient Xai);

    public sealed record ClipReorderServices(
        ProjectStore Store,
        MediaRegistryService Registry,
        IUserContext User,
        IOptions<PageToMovieOptions> Opts);

    private static IResult? ValidateSidecarBody(string body, int scene, int clip)
    {
        if (body.Length > 256 * 1024)
            return Results.BadRequest(new { ok = false, error = "sidecar too large" });
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { return Results.BadRequest(new { ok = false, error = "sidecar must be JSON" }); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !SidecarHasProviderPointer(r))
                return Results.BadRequest(new { ok = false, error = "sidecar carries no provider pointer (source_url / source_file_id)" });
            if (SidecarSceneClipMismatch(r, scene, clip))
                return Results.BadRequest(new { ok = false, error = "sidecar scene/clip does not match the route" });
        }
        return null;
    }

    private static bool SidecarHasProviderPointer(JsonElement r) =>
        (r.TryGetProperty("source_url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
        || (r.TryGetProperty("source_file_id", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()));

    private static bool SidecarSceneClipMismatch(JsonElement r, int scene, int clip) =>
        r.TryGetProperty("scene", out var sc) && sc.TryGetInt32(out var scN) && scN != scene
        || r.TryGetProperty("clip", out var cl) && cl.TryGetInt32(out var clN) && clN != clip;

    private static async Task<IResult> RestoreSidecarAsync(
        string id, int scene, int clip, string body, ProjectStore store, CancellationToken ct)
    {
        var projectDir = await store.GetProjectDirAsync(id, ct);
        var videoDir = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder);
        Directory.CreateDirectory(videoDir);
        // Never overwrite a sidecar that still has a provider pointer — the server copy is newer-or-equal.
        if (ClipProviderSource.ReadForClip(videoDir, scene, clip) is { HasProviderCopy: true })
            return Results.Ok(new { ok = true, restored = false, reason = "server already has a sidecar" });
        var dest = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}_take_01.clip.json");
        await File.WriteAllTextAsync(dest, body, ct);
        DeleteStrayClientSidecarMarkers(videoDir, scene, clip);
        store.InvalidateSceneListCache(id);
        await Console.Error.WriteLineAsync($"[sidecar] restored {id} S{scene:D2}C{clip:D2} from the browser's media folder");
        return Results.Ok(new { ok = true, restored = true });
    }

    private static void DeleteStrayClientSidecarMarkers(string videoDir, int scene, int clip)
    {
        // Stray markers the old register left for synced sidecars (…clip.json.client.json) — drop them.
        foreach (var stray in Directory.EnumerateFiles(videoDir, $"scene_{scene:D2}_clip_{clip:D2}*.clip.json.client.json"))
        {
            try { File.Delete(stray); } catch { /* best effort */ }
        }
    }

    private static async Task<IResult> ServeClipVideoAsync(
        string id, int sceneNumber, int clipNumber, HttpRequest req, ClipVideoServices svc, CancellationToken ct)
    {
        var store = svc.Store;
        var (path, parentId) = await ResolveClipVideoPathWithParentAsync(
            store, id, sceneNumber, clipNumber, ct);
        var fileId = store.TryReadClipSourceFileId(id, sceneNumber, clipNumber)
            ?? (string.IsNullOrWhiteSpace(parentId) ? null : store.TryReadClipSourceFileId(parentId, sceneNumber, clipNumber));
        var exists = path is not null || !string.IsNullOrWhiteSpace(fileId);
        if (HttpMethods.IsHead(req.Method))
            return exists ? Results.Ok() : Results.NotFound();

        if (path is not null)
            return Results.File(path, SpecializedMimeType.VideoMp4.ToMimeTypeString(), enableRangeProcessing: true);

        if (await TryServeProviderClipAsync(id, sceneNumber, clipNumber, req, svc, ct) is { } providerResult)
            return providerResult;

        return await ServeXaiClipOrNotFoundAsync(id, sceneNumber, clipNumber, parentId, fileId, svc, ct);
    }

    private static async Task<IResult?> TryServeProviderClipAsync(
        string id, int sceneNumber, int clipNumber, HttpRequest req, ClipVideoServices svc, CancellationToken ct)
    {
        // No server file: stream the provider copy (sidecar source_url, then source_file_id
        // when the public link 404s) the same way /media/file and /media/proxy do.
        // A video-extend clip's provider copy is the combined video — say how much head is the
        // previous clip so the browser slices it (the fakes' fixture: copies are served from disk).
        var projectDir = await svc.Store.GetProjectDirAsync(id, ct);
        var providerSrc = ClipProviderSource.ReadForClip(
            Path.Combine(projectDir, "assets", "video"), sceneNumber, clipNumber);
        if (providerSrc is null || !providerSrc.HasProviderCopy)
            return null;
        if (providerSrc.IsCombined)
            req.HttpContext.Response.Headers[MediaEndpoints.LeadInHeader] =
                providerSrc.LeadInSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return await MediaEndpoints.StreamProviderCopyAsync(
            providerSrc.SourceUrl, providerSrc.SourceFileId, svc.HttpFactory, svc.Xai, req.HttpContext, ct,
            recoverAfterProvider: (_, _, _) => Task.FromResult(
                MediaEndpoints.TryRecoverHostedCopy(projectDir, sceneNumber, clipNumber)));
    }

    private static async Task<IResult> ServeXaiClipOrNotFoundAsync(
        string id, int sceneNumber, int clipNumber, string? parentId, string? fileId,
        ClipVideoServices svc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            await MarkForkFallbackNeededAsync(svc.Store, id, parentId, sceneNumber, clipNumber, ct);
            return Results.NotFound(new { ok = false, error = "clip video not found" });
        }
        try
        {
            var stream = await svc.Xai.OpenFileContentStreamAsync(fileId, ct);
            return Results.Stream(stream, SpecializedMimeType.VideoMp4.ToMimeTypeString());
        }
        catch (Exception ex)
        {
            // Imagine file_ids are generate-only. Surface the xAI error; Railway is the fallback.
            if (MediaEndpoints.TryRecoverHostedCopy(
                    await svc.Store.GetProjectDirAsync(id, ct), sceneNumber, clipNumber) is { } hosted)
                return hosted;
            await MarkForkFallbackNeededAsync(svc.Store, id, parentId, sceneNumber, clipNumber, ct);
            return Results.Json(
                new { ok = false, error = "Provider file download failed: " + TrimForPlaybackError(ex.Message) },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static string TrimForPlaybackError(string s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
