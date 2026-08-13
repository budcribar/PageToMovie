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

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Open a local folder on disk in Windows File Explorer (or OS file manager).</summary>
        app.MapPost("/api/system/open-folder", PostSystemOpenFolder);
        // <summary>Open a scene composite or full cut in the user's preferred external video editor.</summary>
        app.MapPost("/api/system/open-editor", PostSystemOpenEditor);
        return app;
    }

    private static async Task<IResult> PostSystemOpenFolder(OpenFolderRequest body, ProjectStore store, CancellationToken ct)
    {
    var path = body?.Path;
    if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(body?.ProjectId))
    {
        path = await store.GetProjectDirAsync(body.ProjectId, ct);
    }
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { ok = false, error = "Path is required." });
    }

    try
    {
        var targetPath = path.Trim();
        if (OperatingSystem.IsWindows())
        {
            targetPath = targetPath.Replace('/', '\\');
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ApiEndpointHelpers.WindowsExplorerPath(),
                    Arguments = $"\"{targetPath}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new { ok = true, opened = targetPath });
            }
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ApiEndpointHelpers.WindowsExplorerPath(),
                    Arguments = $"\"{parent}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new { ok = true, opened = parent });
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start(ApiEndpointHelpers.UnixOpenPath(), $"\"{targetPath}\"");
            return Results.Ok(new { ok = true, opened = targetPath });
        }
        else if (OperatingSystem.IsLinux())
        {
            System.Diagnostics.Process.Start(ApiEndpointHelpers.UnixOpenPath(), $"\"{targetPath}\"");
            return Results.Ok(new { ok = true, opened = targetPath });
        }

        return Results.BadRequest(new { ok = false, error = $"Path '{targetPath}' does not exist on server disk." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostSystemOpenEditor(OpenEditorRequest body, ProjectStore store, CancellationToken ct)
    {
    if (string.IsNullOrWhiteSpace(body?.ProjectId))
        return Results.BadRequest(new { ok = false, error = "ProjectId is required." });

    var projectDir = await store.GetProjectDirAsync(body.ProjectId, ct);
    var editorName = string.IsNullOrWhiteSpace(body.EditorName) ? "ClipChamp" : body.EditorName.Trim();
    var videoPath = ResolveEditorVideoPath(projectDir, body.SceneNumber, body.ClipNumber);

    try
    {
        var targetPath = videoPath.Trim();
        var relativeVideoUrl = BuildEditorVideoUrl(body.ProjectId, body.SceneNumber, body.ClipNumber);
        if (OperatingSystem.IsWindows())
            return OpenEditorOnWindows(targetPath, editorName, relativeVideoUrl);
        if (OperatingSystem.IsMacOS())
            return OpenEditorOnMac(targetPath, editorName, relativeVideoUrl);

        // Linux / Cloud container (e.g. Railway)
        return Results.Ok(new OpenEditorResponse
        {
            Ok = false,
            IsRemote = true,
            VideoUrl = relativeVideoUrl,
            Error = $"Server is running in cloud. Streaming video file to open in {editorName} on your device."
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new OpenEditorResponse { Ok = false, Error = ex.Message });
    }
}

    private static string ResolveEditorVideoPath(string projectDir, int? sceneNumber, int? clipNumber)
    {
        var fromScene = TryResolveSceneVideoPath(projectDir, sceneNumber, clipNumber);
        if (fromScene is not null)
            return fromScene;

        var wipMovie = Path.Combine(projectDir, "movie.mp4");
        if (File.Exists(wipMovie))
            return wipMovie;

        var altWip = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder, "wip_movie.mp4");
        if (File.Exists(altWip))
            return altWip;

        var videoDir = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder);
        if (Directory.Exists(videoDir))
            return videoDir;
        return projectDir;
    }

    private static string? TryResolveSceneVideoPath(string projectDir, int? sceneNumber, int? clipNumber)
    {
        if (sceneNumber is not int sn || sn <= 0)
            return null;
        if (clipNumber is int cn && cn > 0)
        {
            var cPath = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder, $"scene_{sn:D3}_clip_{cn:D2}.mp4");
            if (File.Exists(cPath))
                return cPath;
        }
        var compPath = Path.Combine(projectDir, ApiText.AssetsFolder, ApiText.VideoFolder, $"scene_{sn:D3}_composite.mp4");
        if (File.Exists(compPath))
            return compPath;
        return null;
    }

    private static string BuildEditorVideoUrl(string projectId, int? sceneNumber, int? clipNumber)
    {
        if (sceneNumber is int targetSn && targetSn > 0)
        {
            if (clipNumber is int targetCn && targetCn > 0)
                return $"/api/projects/{projectId}/scenes/{targetSn}/clips/{targetCn}/video";
            return $"/api/projects/{projectId}/scenes/{targetSn}/composite";
        }
        return $"/api/projects/{projectId}/movie";
    }

    private static IResult OpenEditorOnWindows(string targetPath, string editorName, string? relativeVideoUrl)
    {
        targetPath = targetPath.Replace('/', '\\');

        if ((string.Equals(editorName, "ClipChamp", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(editorName, "Clipchamp", StringComparison.OrdinalIgnoreCase)) &&
            TryOpenClipchamp(targetPath, relativeVideoUrl) is { } clipchampResult)
        {
            return clipchampResult;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editorName,
                Arguments = $"\"{targetPath}\"",
                UseShellExecute = true
            });
            return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = editorName, VideoUrl = relativeVideoUrl });
        }
        catch
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true
            });
            return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = "Default OS Editor", VideoUrl = relativeVideoUrl });
        }
    }

    private static IResult? TryOpenClipchamp(string targetPath, string? relativeVideoUrl)
    {
        try
        {
            // Launch Microsoft Clipchamp via registered Windows protocol ms-clipchamp:
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-clipchamp:",
                UseShellExecute = true
            });

            // Reveal/select the target video file in Explorer so user can easily drag into Clipchamp
            if (File.Exists(targetPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(ApiEndpointHelpers.WindowsExplorerPath(), $"/select,\"{targetPath}\"");
                }
                catch { /* best-effort explorer reveal */ }
            }

            return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = "Clipchamp", VideoUrl = relativeVideoUrl });
        }
        catch { /* fallback to default */ }
        return null;
    }

    private static IResult OpenEditorOnMac(string targetPath, string editorName, string? relativeVideoUrl)
    {
        try
        {
            System.Diagnostics.Process.Start(ApiEndpointHelpers.UnixOpenPath(), $"\"{targetPath}\"");
            return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = editorName, VideoUrl = relativeVideoUrl });
        }
        catch (Exception)
        {
            return Results.Ok(new OpenEditorResponse { Ok = false, IsRemote = true, VideoUrl = relativeVideoUrl, Error = $"Remote server cannot open desktop app. Stream video to open in {editorName}." });
        }
    }
}
