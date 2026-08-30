using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ClipForkFallbackTests
{
    [Fact]
    public void MarkNeeded_lists_and_clears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm_fork_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ClipForkFallback.MarkNeeded(dir, 3, 2);
            ClipForkFallback.MarkNeeded(dir, 3, 2);
            var need = ClipForkFallback.ListNeeded(dir);
            Assert.Single(need);
            Assert.Equal((3, 2), need[0]);
            ClipForkFallback.ClearNeeded(dir, 3, 2);
            Assert.Empty(ClipForkFallback.ListNeeded(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void WriteProtectedMp4_is_skipped_by_prune_guard()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm_fork_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bytes = new byte[2048];
            ClipForkFallback.WriteProtectedMp4(dir, 1, 1, bytes);
            // A pushed fork copy is the clip's first take, like any other rendition.
            var mp4 = Path.Combine(
                dir, "assets", "video", ClipTakeNaming.TakeMp4FileName(1, 1, 1));
            Assert.True(File.Exists(mp4));
            Assert.True(ClipForkFallback.IsProtectedFromPrune(mp4));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void WriteSidecarFileId_drops_expiry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm_fork_fb_" + Guid.NewGuid().ToString("N"));
        var video = Path.Combine(dir, "assets", "video");
        Directory.CreateDirectory(video);
        File.WriteAllText(Path.Combine(video, "scene_01_clip_01.clip.json"),
            """{"source_file_id":"old","source_file_expires_at":1}""");
        ClipForkFallback.WriteSidecarFileId(dir, 1, 1, "file_new");
        var json = File.ReadAllText(Path.Combine(video, "scene_01_clip_01.clip.json"));
        Assert.Contains("file_new", json);
        Assert.DoesNotContain("source_file_expires_at", json);
    }

    [Fact]
    public void ProjectDirFromVideoDir_walks_assets_video()
    {
        var project = Path.Combine(Path.GetTempPath(), "ptm_fork_proj_" + Guid.NewGuid().ToString("N"));
        var video = Path.Combine(project, "assets", "video");
        Assert.Equal(Path.GetFullPath(project), ClipForkFallback.ProjectDirFromVideoDir(video));
    }

    [Fact]
    public void TryProtectedMp4Path_requires_prune_guard()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm_fork_fb_" + Guid.NewGuid().ToString("N"));
        try
        {
            var video = Path.Combine(dir, "assets", "video");
            Directory.CreateDirectory(video);
            File.WriteAllBytes(Path.Combine(video, "scene_01_clip_01.mp4"), new byte[2048]);
            Assert.Null(ClipForkFallback.TryProtectedMp4Path(dir, 1, 1));

            ClipForkFallback.WriteProtectedMp4(dir, 2, 3, new byte[2048]);
            var hosted = ClipForkFallback.TryProtectedMp4Path(dir, 2, 3);
            Assert.NotNull(hosted);
            Assert.True(File.Exists(hosted));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryMarkNeeded_is_safe_and_lists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm_fork_fb_" + Guid.NewGuid().ToString("N"));
        try
        {
            ClipForkFallback.TryMarkNeeded(null, 1, 1);
            ClipForkFallback.TryMarkNeeded(dir, 0, 1);
            ClipForkFallback.TryMarkNeeded(dir, 4, 5);
            Assert.Equal((4, 5), Assert.Single(ClipForkFallback.ListNeeded(dir)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
