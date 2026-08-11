using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class FilmBuildAndStageCommitTests
{
    [Fact]
    public void Stage_commit_messages_are_greppable()
    {
        Assert.StartsWith("ptm:stage=", ProjectStageCommits.ScreenplayCreated);
        Assert.StartsWith("ptm:stage=", ProjectStageCommits.CastBuilt);
        Assert.Contains("film_id=", ProjectStageCommits.FilmStitched("film_x_1"));
        Assert.Equal(ProjectStageCommits.ScreenplayCreated, ProjectStageCommits.FromJobKind("stage1"));
        Assert.Equal(ProjectStageCommits.FilmJobFinished, ProjectStageCommits.FromJobKind("film"));
    }

    [Fact]
    public async Task FilmBuild_create_and_roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_film_build_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var segs = new List<FilmBuildSegment>
            {
                new() { Index = 0, Scene = 1, Clip = 1, TStart = 0, TEnd = 4.2, Src = "assets/video/scene_01_clip_01.mp4" },
                new() { Index = 1, Scene = 1, Clip = 2, TStart = 4.2, TEnd = 8.0, Src = "assets/video/scene_01_clip_02.mp4" },
            };
            var doc = FilmBuildService.Create("admin/Mary", "abc123def4567890abc123def4567890abc123def4567890abc123def4567890", 8.0, segs, byteLength: 1024);
            Assert.StartsWith("film_", doc.FilmId);
            Assert.Equal(2, doc.Timeline.Segments.Count);
            Assert.Equal(8.0, doc.Timeline.TotalSeconds);
            await FilmBuildService.WriteAsync(root, doc);
            var path = FilmBuildService.GetPath(root);
            Assert.True(File.Exists(path));
            var loaded = await FilmBuildService.TryReadAsync(root);
            Assert.NotNull(loaded);
            Assert.Equal(doc.FilmId, loaded!.FilmId);
            Assert.Equal(doc.Studio.Sha256, loaded.Studio.Sha256);
            Assert.Equal(2, loaded.Timeline.Segments.Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void HashBytes_is_stable()
    {
        var a = FilmBuildService.HashBytes(new byte[] { 1, 2, 3 });
        var b = FilmBuildService.HashBytes(new byte[] { 1, 2, 3 });
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }
}
