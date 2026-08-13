using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
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

    [Fact]
    public async Task RegisterFromWipFile_default_path_hashes_wip_under_owner_slug_project()
    {
        await using var fx = await WipFixture.CreateAsync();
        var doc = await FilmBuildService.RegisterFromWipFileAsync(fx.Store, fx.ProjectId);
        Assert.NotNull(doc);
        Assert.Equal(FilmBuildService.HashBytes(WipFixture.WipBytes), doc!.Studio.Sha256);
        Assert.Equal(WipFixture.WipBytes.Length, doc.Studio.ByteLength);
    }

    [Fact]
    public async Task RegisterFromWipFile_nested_relative_path_stays_under_project()
    {
        await using var fx = await WipFixture.CreateAsync();
        var nestedDir = Path.Combine(fx.ProjectDir, "assets", "video");
        Directory.CreateDirectory(nestedDir);
        var nested = Path.Combine(nestedDir, "cut.mp4");
        var payload = new byte[] { 7, 7, 7, 7 };
        await File.WriteAllBytesAsync(nested, payload);

        var doc = await FilmBuildService.RegisterFromWipFileAsync(
            fx.Store, fx.ProjectId, "assets/video/cut.mp4");
        Assert.NotNull(doc);
        Assert.Equal(FilmBuildService.HashBytes(payload), doc!.Studio.Sha256);
    }

    [Fact]
    public async Task RegisterFromWipFile_accepts_percent2F_encoded_composite_project_id()
    {
        await using var fx = await WipFixture.CreateAsync();
        Assert.Contains('/', fx.ProjectId, StringComparison.Ordinal);
        var encoded = fx.ProjectId.Replace("/", "%2F", StringComparison.Ordinal);

        var doc = await FilmBuildService.RegisterFromWipFileAsync(fx.Store, encoded);
        Assert.NotNull(doc);
        Assert.Equal(FilmBuildService.HashBytes(WipFixture.WipBytes), doc!.Studio.Sha256);
    }

    [Theory]
    [InlineData("../secret.bin")]
    [InlineData("..\\secret.bin")]
    [InlineData("../../secret.bin")]
    [InlineData("assets/../../secret.bin")]
    [InlineData("%2e%2e/secret.bin")]
    public async Task RegisterFromWipFile_rejects_path_traversal(string evilRel)
    {
        await using var fx = await WipFixture.CreateAsync();
        var sentinel = Path.Combine(fx.Root, "secret.bin");
        await File.WriteAllBytesAsync(sentinel, new byte[] { 9, 9, 9, 9 });

        var doc = await FilmBuildService.RegisterFromWipFileAsync(fx.Store, fx.ProjectId, evilRel);
        Assert.Null(doc);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(sentinel));
    }

    [Fact]
    public async Task RegisterFromWipFile_rejects_absolute_path_outside_project()
    {
        await using var fx = await WipFixture.CreateAsync();
        var sentinel = Path.Combine(fx.Root, "outside.mp4");
        await File.WriteAllBytesAsync(sentinel, new byte[] { 8, 8, 8, 8 });

        var doc = await FilmBuildService.RegisterFromWipFileAsync(fx.Store, fx.ProjectId, sentinel);
        Assert.Null(doc);
        Assert.False(File.Exists(Path.Combine(fx.ProjectDir, "assets", "movie_wip.film.json")));
    }

    private sealed class WipFixture : IAsyncDisposable
    {
        public static readonly byte[] WipBytes = { 1, 2, 3, 4, 5 };

        public string Root { get; }
        public ProjectStore Store { get; }
        public string ProjectId { get; }
        public string ProjectDir { get; }

        private WipFixture(string root, ProjectStore store, string projectId, string projectDir)
        {
            Root = root;
            Store = store;
            ProjectId = projectId;
            ProjectDir = projectDir;
        }

        public static async Task<WipFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ptm_film_wip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "projects"));
            var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = root }));
            var project = await store.CreateProjectAsync("WipBook", ownerUserId: "alice");
            var projectDir = await store.GetProjectDirAsync(project.Id);
            var assets = Path.Combine(projectDir, "assets");
            Directory.CreateDirectory(assets);
            await File.WriteAllBytesAsync(Path.Combine(assets, "movie_wip.mp4"), WipBytes);
            return new WipFixture(root, store, project.Id, projectDir);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            try { Directory.Delete(Root, recursive: true); } catch { /* ignore */ }
        }
    }
}
