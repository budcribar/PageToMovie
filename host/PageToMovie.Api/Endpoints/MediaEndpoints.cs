using System.Diagnostics;
using System.Text.Json;
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
        // Media that may have arrived via full project import (video, music, audio, history).
        var list = new List<object>();
        var assetsRoot = Path.Combine(projectDir, ApiText.AssetsFolder);
        var mediaExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mov", ".mkv", ".m4v",
            ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac", ".opus",
            ".png", ".jpg", ApiText.JpegExtension, ".webp", ".gif",
        };

        if (Directory.Exists(assetsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name is "Thumbs.db" or ".DS_Store") continue;
                var ext = Path.GetExtension(file);
                var isClipJson = name.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);
                if (!isClipJson && !mediaExts.Contains(ext))
                    continue;

                var relPath = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                var fi = new FileInfo(file);
                var isMp4 = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

                string? sha256 = null;
                try
                {
                    if (fi.Length <= 64L * 1024 * 1024)
                    {
                        using var fs = File.OpenRead(file);
                        var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                        sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                }
                catch { /* best-effort sha256 */ }

                var ticketToken = tickets.Issue($"{id}:{relPath}", TimeSpan.FromHours(2));
                var streamUrl = $"/api/projects/{Uri.EscapeDataString(id)}/media/file?path={Uri.EscapeDataString(relPath)}&ticket={ticketToken}";

                list.Add(new
                {
                    relativePath = relPath,
                    fileName = name,
                    sizeBytes = fi.Length,
                    sha256,
                    isMp4,
                    streamUrl,
                });
            }
        }

        return Results.Ok(new { ok = true, projectId = id, files = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdMediaFile(string id,
    string path,
    string? ticket,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    var ticketValid = false;
    if (!string.IsNullOrWhiteSpace(ticket))
    {
        var target = tickets.TryTakeUrl(ticket);
        if (target is not null && string.Equals(target, $"{id}:{path}", StringComparison.OrdinalIgnoreCase))
        {
            ticketValid = true;
        }
    }

    if (!ticketValid && AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { ok = false, error = "path parameter required" });

        var projectDir = await store.GetProjectDirAsync(id, ct);
        var cleanRelPath = path.TrimStart('/', '\\').Replace('\\', '/');

        var fullPath = Path.GetFullPath(Path.Combine(projectDir, cleanRelPath.Replace('/', Path.DirectorySeparatorChar)));
        var fullProjDir = Path.GetFullPath(projectDir);
        if (!fullPath.StartsWith(fullProjDir, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, error = "Invalid media path" });

        if (!File.Exists(fullPath))
            return Results.NotFound(new { ok = false, error = "File not found" });

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
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

        return Results.File(fullPath, contentType, Path.GetFileName(fullPath), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
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

            // Curated/forkable source projects opt out of offload (project.json "keep_media_on_server":
            // true) so their clips stay server-side and remain available to forks + the voice-dub input.
            // A stopgap for clips generated before source_url capture; rebuilt movies re-fetch by URL.
            var keepMediaOnServer = false;
            try
            {
                var pjPath = Path.Combine(dir, "project.json");
                if (File.Exists(pjPath))
                {
                    using var pjDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(pjPath, ct));
                    keepMediaOnServer = pjDoc.RootElement.TryGetProperty("keep_media_on_server", out var kEl)
                        && kEl.ValueKind == System.Text.Json.JsonValueKind.True;
                }
            }
            catch { /* default: offload as usual */ }

            if (!isCharacterImage && !keepMediaOnServer)
            {
                var marker = full + ".client.json";
                await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(new
                {
                    storage = "client",
                    sha256 = dto.Sha256,
                    sizeBytes = dto.SizeBytes,
                    registeredAt = dto.CreatedAt,
                    userId = user.UserId,
                }) + "\n", ct);

                // Reclaim server volume storage: if server MP4 exists and matches verified client registration size, delete server copy.
                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    if (dto.SizeBytes <= 0 || fi.Length == dto.SizeBytes)
                    {
                        File.Delete(full);
                    }
                }
            }

            store.InvalidateSceneListCache(id);
        }
        catch { /* non-fatal */ }

        return Results.Ok(new { ok = true, media = dto });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
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

    // Inline provider bytes (e.g. ElevenLabs Music streams audio back rather than hosting a URL):
    // decode the self-contained data: URL and serve it, so no media is persisted on the API host.
    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var comma = url.IndexOf(',');
        var meta = comma > 0 ? url[5..comma] : "";
        if (comma < 0 || !meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, error = "Unsupported data URL" });
        var dataCtype = meta.Split(';')[0];
        if (string.IsNullOrWhiteSpace(dataCtype)) dataCtype = SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString();
        byte[] dataBytes;
        try { dataBytes = Convert.FromBase64String(url[(comma + 1)..]); }
        catch { return Results.BadRequest(new { ok = false, error = "Malformed data URL" }); }
        var ext = dataCtype.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? ".mp3"
            : dataCtype.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".bin";
        return Results.Bytes(dataBytes, contentType: dataCtype, fileDownloadName: "track" + ext);
    }

    // Fakes-mode local fixture (no upstream provider to fetch from) — same ticket
    // mechanism as a real provider URL, just served from disk instead of proxied over HTTP.
    if (url.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase))
    {
        var fixturePath = url["fixture:".Length..];
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
