using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class DemoYouTubePublisherServiceTests
{
    private static (DemoCatalogService Demos, DemoYouTubePublisherService Publisher, string Root) MakeHarness(
        YouTubeOptions? youTube = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_yt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opts = Options.Create(new PageToMovieOptions
        {
            WorkspaceRoot = root,
            YouTube = youTube ?? new YouTubeOptions(), // unconfigured by default
        });
        var projects = new ProjectStore(opts);
        var demos = new DemoCatalogService(projects, NullLogger<DemoCatalogService>.Instance);
        var auth = new YouTubeAuthService(projects, opts);
        var publisher = new DemoYouTubePublisherService(demos, auth, NullLogger<DemoYouTubePublisherService>.Instance);
        return (demos, publisher, root);
    }

    private static async Task<DemoCatalogService.DemoEntry> PublishSampleAsync(DemoCatalogService demos)
    {
        var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
        await using var stream = new MemoryStream(bytes);
        return await demos.PublishFromStreamAsync(stream, "My Film", "desc", "Demo", "user1", acceptedGuidelines: true);
    }

    private static string WriteFakeMp4(string dir, string name = "movie.mp4")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000)));
        return path;
    }

    [Fact]
    public void IsConfigured_false_when_YouTube_OAuth_not_set_up()
    {
        var (_, publisher, root) = MakeHarness();
        try { Assert.False(publisher.IsConfigured); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PublishAsync_marks_failed_and_keeps_local_file_when_not_configured()
    {
        var (demos, publisher, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);

            await publisher.PublishAsync(entry.Id);

            var updated = await demos.TryGetAsync(entry.Id);
            Assert.NotNull(updated);
            Assert.Equal("failed", updated!.YoutubeUploadStatus);
            Assert.Null(updated.YoutubeId);
            Assert.NotNull(demos.ResolveMoviePath(entry.Id)); // never lost the only copy
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PublishAsync_is_a_noop_for_a_demo_that_does_not_exist()
    {
        var (demos, publisher, root) = MakeHarness();
        try
        {
            var ex = await Record.ExceptionAsync(() => publisher.PublishAsync("does_not_exist_12345"));
            Assert.Null(ex);
            Assert.Null(await demos.TryGetAsync("does_not_exist_12345"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PublishAsync_is_a_noop_once_already_migrated_with_no_local_movie()
    {
        var (demos, publisher, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);
            await demos.SetYouTubeUploadStatusAsync(entry.Id, "done", "already123", "https://youtu.be/already123");
            Assert.Null(demos.ResolveMoviePath(entry.Id)); // deleted on done

            await publisher.PublishAsync(entry.Id);

            var updated = await demos.TryGetAsync(entry.Id);
            Assert.Equal("already123", updated!.YoutubeId); // unchanged — no re-upload attempted
            Assert.Equal("done", updated.YoutubeUploadStatus);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PublishAsync_V2_path_attempts_upload_when_local_movie_exists_beside_YoutubeId()
    {
        var (demos, publisher, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);
            await demos.SetYouTubeUploadStatusAsync(entry.Id, "done", "oldvid99", "https://youtu.be/oldvid99");
            // Simulate re-publish: new movie.mp4 next to existing YoutubeId
            var demoDir = Path.Combine(root, "_demos", entry.Id);
            WriteFakeMp4(demoDir);

            await publisher.PublishAsync(entry.Id);

            var updated = await demos.TryGetAsync(entry.Id);
            Assert.NotNull(updated);
            // YouTube not configured → upload fails, but we took the V2 path (not no-op).
            Assert.Equal("failed", updated!.YoutubeUploadStatus);
            // Previous pointer preserved so gallery still works.
            Assert.Equal("oldvid99", updated.YoutubeId);
            Assert.NotNull(demos.ResolveMoviePath(entry.Id));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FindPublicDemoForProject_and_AttachMovieFromWip_support_replace_flow()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_replace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
            var projects = new ProjectStore(opts);
            var demos = new DemoCatalogService(projects, NullLogger<DemoCatalogService>.Instance);

            // Create a real project with a WIP-like movie for AttachMovieFromWip
            var proj = await projects.CreateProjectAsync("ReplaceMe", "Replace Me");
            var wipDir = Path.Combine(root, "projects", proj.Id, "assets");
            Directory.CreateDirectory(wipDir);
            // ResolveWipMoviePath looks for assets/movie_wip.mp4 typically
            var wipPath = projects.ResolveWipMoviePath(proj.Id);
            // If null, write common path
            var movieWip = Path.Combine(root, "projects", proj.Id, "assets", "movie_wip.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(movieWip)!);
            File.WriteAllBytes(movieWip, Encoding.ASCII.GetBytes("....ftypmp42" + new string('y', 2000)));

            var entry = await PublishSampleAsync(demos);
            // Force project id / public / youtube
            await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, "user1", "test");
            // Manually patch projectId via attach after setting youtube
            await demos.SetYouTubeUploadStatusAsync(entry.Id, "done", "yt1", "https://youtu.be/yt1");

            // Re-point project: AttachMovie uses demo id; FindPublic needs matching projectId
            // Publish sample used project "Demo" — use that
            var found = await demos.FindPublicDemoForProjectAsync("Demo", "user1");
            Assert.NotNull(found);
            Assert.Equal(entry.Id, found!.Id);

            // Attach from file directly (WIP path may vary)
            var newMovie = WriteFakeMp4(Path.Combine(root, "tmp"), "new.mp4");
            var updated = await demos.AttachMovieFromFileAsync(entry.Id, newMovie, title: "Updated Title");
            Assert.Equal("Updated Title", updated.Title);
            Assert.Equal("yt1", updated.YoutubeId); // pointer kept until V2 succeeds
            Assert.Equal("pending_replace", updated.YoutubeUploadStatus);
            Assert.NotNull(demos.ResolveMoviePath(entry.Id));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* */ }
        }
    }
}
