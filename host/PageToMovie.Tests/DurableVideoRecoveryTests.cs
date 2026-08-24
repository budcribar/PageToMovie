using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PageToMovie.Api;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Durable clip recovery: request <c>storage_options.public_url</c>, persist that URL as
/// sidecar <c>source_url</c>, and fall back to the Railway fork copy when the provider
/// file_id cannot be downloaded. Hop-walk / combined-extend slicing is unchanged.
/// </summary>
public sealed class DurableVideoRecoveryTests
{
    [Fact]
    public void RecoveredViaSourceUrlStatus_uses_http_code_from_file_id_error()
    {
        Assert.Equal(
            "recovered via source_url after file content HTTP 500",
            MediaProxyHeaders.RecoveredViaSourceUrlStatus(
                "xAI file content HTTP 500: {\"error\":\"Failed to retrieve file\"}"));
        Assert.Equal(500, MediaProxyHeaders.TryHttpStatus("xAI file content HTTP 500: Failed to retrieve file"));
        Assert.Equal(4, (int)ClipProviderSource.PublicUrlTimeoutWhenFileIdPresent.TotalSeconds);
    }

    [Fact]
    public void Media_js_reads_the_same_FileIdError_header()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? jsDir = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "PageToMovie.Web", "wwwroot", "js");
            if (Directory.Exists(candidate))
            {
                jsDir = candidate;
                break;
            }
            candidate = Path.Combine(dir.FullName, "host", "PageToMovie.Web", "wwwroot", "js");
            if (Directory.Exists(candidate))
            {
                jsDir = candidate;
                break;
            }
            dir = dir.Parent;
        }
        Assert.False(string.IsNullOrWhiteSpace(jsDir));
        var media = File.ReadAllText(Path.Combine(jsDir!, "pagetomovie-media.js"));
        var ffmpeg = File.ReadAllText(Path.Combine(jsDir!, "pagetomovie-ffmpeg.js"));
        Assert.Contains(MediaProxyHeaders.FileIdError, media, StringComparison.Ordinal);
        Assert.Contains("fileIdErrorFrom", ffmpeg, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentVideoStorageOptions_requests_public_url_without_expiry()
    {
        var opts = GrokVideoClient.PermanentVideoStorageOptions();
        Assert.True(opts.ContainsKey("filename"));
        Assert.False(string.IsNullOrWhiteSpace(opts["filename"] as string));
        // S2701: do not Assert.Equal(true, object). `is true` still requires boxed bool true.
        Assert.True(opts["public_url"] is true);
        Assert.False(opts.ContainsKey("expires_after"));
    }

    [Fact]
    public void ParseFileOutput_caches_public_url_and_file_id()
    {
        using var doc = JsonDocument.Parse(
            """{"file_output":{"file_id":"file_abc","public_url":"https://files.x.ai/p/abc.mp4","expires_at":1999999999}}""");
        var stored = GrokVideoClient.ParseFileOutput(doc.RootElement);
        Assert.Equal("file_abc", stored.FileId);
        Assert.Equal("https://files.x.ai/p/abc.mp4", stored.PublicUrl);
        Assert.Equal(1999999999, stored.ExpiresAtUnixSeconds);
        Assert.Equal("https://files.x.ai/p/abc.mp4", stored.DurableSourceUrl("https://vidgen.x.ai/expired.mp4"));
    }

    [Fact]
    public void DurableSourceUrl_falls_back_to_poll_url_when_public_url_missing()
    {
        using var doc = JsonDocument.Parse("""{"file_output":{"file_id":"file_only"}}""");
        var stored = GrokVideoClient.ParseFileOutput(doc.RootElement);
        Assert.True(stored.HasFileId);
        Assert.False(stored.HasPublicUrl);
        Assert.Equal("https://vidgen.x.ai/poll.mp4", stored.DurableSourceUrl("https://vidgen.x.ai/poll.mp4"));
    }

    [Fact]
    public void ParseFileOutput_empty_without_file_output()
    {
        using var doc = JsonDocument.Parse("""{"url":"https://vidgen.x.ai/x.mp4"}""");
        var stored = GrokVideoClient.ParseFileOutput(doc.RootElement);
        Assert.False(stored.HasFileId);
        Assert.False(stored.HasPublicUrl);
        Assert.Null(stored.DurableSourceUrl(null));
    }

    [Fact]
    public async Task StreamProviderCopy_uses_railway_copy_when_imagine_file_id_cannot_download()
    {
        var project = Path.Combine(Path.GetTempPath(), "ptm-dur-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ClipForkFallback.WriteProtectedMp4(project, 1, 1, new byte[] { 9, 8, 7, 6, 5 });
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://vidgen.example/expired.mp4",
                "file_1ed4c54f-2edd-485b-8d35-5f31c854132a",
                (_, _) => Task.FromResult<IResult?>(null),
                (_, _) => throw new InvalidOperationException("xAI file content HTTP 404: not found"),
                CancellationToken.None,
                new MediaEndpoints.StreamProviderCopyOptions(
                    RecoverAfterProvider: (_, _, _) => Task.FromResult(
                        MediaEndpoints.TryRecoverHostedCopy(project, 1, 1))));

            Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
            Assert.DoesNotContain("File not found", ErrorOf(result), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task StreamProviderCopy_marks_need_fork_after_file_id_500_and_url_miss()
    {
        var project = Path.Combine(Path.GetTempPath(), "ptm-dur-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var urlHits = 0;
            var result = await MediaEndpoints.StreamProviderCopyAsync(
                "https://vidgen.example/expired.mp4",
                "file_dead",
                (_, _) =>
                {
                    urlHits++;
                    return Task.FromResult<IResult?>(null);
                },
                (_, _) => throw new InvalidOperationException(
                    "xAI file content HTTP 500: {\"error\":\"Failed to retrieve file\"}"),
                CancellationToken.None,
                new MediaEndpoints.StreamProviderCopyOptions(
                    RecoverAfterProvider: (_, _, _) => Task.FromResult(
                        MediaEndpoints.TryRecoverHostedCopy(project, 1, 2))));

            Assert.Equal(1, urlHits);
            Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(result));
            var err = ErrorOf(result);
            Assert.Contains("Provider file download failed", err, StringComparison.Ordinal);
            Assert.Contains("500", err, StringComparison.Ordinal);
            Assert.Contains("Failed to retrieve file", err, StringComparison.Ordinal);
            Assert.Contains(ClipProviderSource.SourceUrlAlsoFailedPrefix, err, StringComparison.Ordinal);
            Assert.DoesNotContain("\"File not found\"", err, StringComparison.Ordinal);
            Assert.Equal((1, 2), Assert.Single(ClipForkFallback.ListNeeded(project)));
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void CollectRecovery_tickets_project_scene_clip_for_railway_fallback()
    {
        var project = Path.Combine(Path.GetTempPath(), "ptm-dur-" + Guid.NewGuid().ToString("N")[..8]);
        var video = Path.Combine(project, "assets", "video");
        Directory.CreateDirectory(video);
        File.WriteAllText(
            Path.Combine(video, "scene_01_clip_01.clip.json"),
            """{"scene":1,"clip":1,"source_url":"https://vidgen.example/expired.mp4","source_file_id":"file_imagine"}""");
        try
        {
            string? seenDir = null;
            var seenScene = 0;
            var seenClip = 0;
            var entries = MediaEndpoints.CollectProviderRecoveryEntries(
                video,
                (url, fileId, projectDir, scene, clip) =>
                {
                    seenDir = projectDir;
                    seenScene = scene;
                    seenClip = clip;
                    Assert.Equal("https://vidgen.example/expired.mp4", url);
                    Assert.Equal("file_imagine", fileId);
                    return "tok-rail";
                });

            Assert.Single(entries);
            Assert.Equal(Path.GetFullPath(project), seenDir);
            Assert.Equal(1, seenScene);
            Assert.Equal(1, seenClip);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Ticket_store_keeps_project_scene_clip()
    {
        var store = new MediaProxyTicketStore();
        var token = store.Issue(
            "https://vidgen.example/expired.mp4",
            TimeSpan.FromMinutes(5),
            "file_imagine",
            keyUserId: "owner",
            projectDir: "/data/projects/Mary19",
            scene: 1,
            clip: 1);
        Assert.True(store.TryTake(token, out var url, out var fileId, out var keyUser, out var dir, out var scene, out var clip));
        Assert.Equal("https://vidgen.example/expired.mp4", url);
        Assert.Equal("file_imagine", fileId);
        Assert.Equal("owner", keyUser);
        Assert.Equal("/data/projects/Mary19", dir);
        Assert.Equal(1, scene);
        Assert.Equal(1, clip);
    }

    [Fact]
    public void ClipFileNaming_parses_scene_clip()
    {
        Assert.True(ClipFileNaming.TryParseSceneClip("scene_01_clip_01.mp4", out var s, out var c));
        Assert.Equal(1, s);
        Assert.Equal(1, c);
        Assert.True(ClipFileNaming.TryParseSceneClip("scene_02_clip_03_take_02.mp4", out s, out c));
        Assert.Equal(2, s);
        Assert.Equal(3, c);
        Assert.False(ClipFileNaming.TryParseSceneClip("scene_01.mp4", out _, out _));
    }

    private static int? StatusOf(IResult result) =>
        result is IStatusCodeHttpResult s ? s.StatusCode : 200;

    private static string ErrorOf(IResult result)
    {
        if (result is IValueHttpResult { Value: { } v })
            return JsonSerializer.Serialize(v);
        return result.GetType().Name;
    }
}
