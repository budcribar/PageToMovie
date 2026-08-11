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

public class DemoCatalogServiceTests
{
    private static (DemoCatalogService Demos, string Root) MakeHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_catalog_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
        var projects = new ProjectStore(opts);
        var demos = new DemoCatalogService(projects, NullLogger<DemoCatalogService>.Instance);
        return (demos, root);
    }

    private static async Task<DemoCatalogService.DemoEntry> PublishSampleAsync(DemoCatalogService demos)
    {
        var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
        await using var stream = new MemoryStream(bytes);
        return await demos.PublishFromStreamAsync(
            stream, "My Film", "desc", "Demo", "user1", acceptedGuidelines: true,
            madeForKids: true, isAiSyntheticContent: false, privacyStatus: "unlisted",
            tags: new() { "a", "b" });
    }

    [Fact]
    public async Task Records_YouTube_metadata_declared_at_submit_time()
    {
        var (demos, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);

            Assert.True(entry.MadeForKids);
            Assert.False(entry.IsAiSyntheticContent);
            Assert.Equal("unlisted", entry.PrivacyStatus);
            Assert.Equal(new[] { "a", "b" }, entry.Tags);
            Assert.Equal("none", entry.YoutubeUploadStatus);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SetYouTubeUploadStatus_done_deletes_local_file_but_entry_stays_valid()
    {
        var (demos, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);
            var moviePath = demos.ResolveMoviePath(entry.Id);
            Assert.NotNull(moviePath);
            Assert.True(File.Exists(moviePath));

            var updated = await demos.SetYouTubeUploadStatusAsync(entry.Id, "done", "yt123", "https://youtu.be/yt123");

            Assert.NotNull(updated);
            Assert.Equal("yt123", updated!.YoutubeId);
            Assert.Equal("https://youtu.be/yt123", updated.YoutubeUrl);
            Assert.Equal("done", updated.YoutubeUploadStatus);
            Assert.False(File.Exists(moviePath!)); // local copy removed — server footprint goal

            // Entry must still resolve (it now lives on YouTube, not on disk).
            var reread = await demos.TryGetAsync(entry.Id);
            Assert.NotNull(reread);
            Assert.Equal("yt123", reread!.YoutubeId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SetYouTubeUploadStatus_failed_keeps_local_file_as_fallback()
    {
        var (demos, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);

            var updated = await demos.SetYouTubeUploadStatusAsync(entry.Id, "failed", error: "quota exceeded");

            Assert.NotNull(updated);
            Assert.Equal("failed", updated!.YoutubeUploadStatus);
            Assert.Equal("quota exceeded", updated.YoutubeUploadError);
            Assert.Null(updated.YoutubeId);
            Assert.NotNull(demos.ResolveMoviePath(entry.Id)); // still server-hosted
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MigrateEmailCreatedBy_rewrites_email_to_resolved_id_leaves_handles_and_is_idempotent()
    {
        var (demos, root) = MakeHarness();
        try
        {
            var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
            DemoCatalogService.DemoEntry legacy, modern;
            await using (var s = new MemoryStream(bytes))
                legacy = await demos.PublishFromStreamAsync(s, "Legacy", "d", "Demo", "budcribar@msn.com", acceptedGuidelines: true);
            await using (var s2 = new MemoryStream(bytes))
                modern = await demos.PublishFromStreamAsync(s2, "Modern", "d", "Demo", "somehandle", acceptedGuidelines: true);

            Func<string, string?> resolver = email =>
                string.Equals(email, "budcribar@msn.com", StringComparison.OrdinalIgnoreCase) ? "budcribar" : null;

            // First pass rewrites exactly the email record.
            Assert.Equal(1, await demos.MigrateEmailCreatedByAsync(resolver));
            Assert.Equal("budcribar", (await demos.TryGetAsync(legacy.Id))!.CreatedBy);
            Assert.Equal("somehandle", (await demos.TryGetAsync(modern.Id))!.CreatedBy); // non-email handle untouched

            // Idempotent: nothing left to change on a second pass.
            Assert.Equal(0, await demos.MigrateEmailCreatedByAsync(resolver));

            // Unresolvable email is left as-is rather than blanked.
            var (demos2, root2) = MakeHarness();
            try
            {
                await using var s3 = new MemoryStream(bytes);
                var orphan = await demos2.PublishFromStreamAsync(s3, "Orphan", "d", "Demo", "nobody@example.com", acceptedGuidelines: true);
                Assert.Equal(0, await demos2.MigrateEmailCreatedByAsync(_ => null));
                Assert.Equal("nobody@example.com", (await demos2.TryGetAsync(orphan.Id))!.CreatedBy);
            }
            finally { Directory.Delete(root2, true); }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Entry_with_no_local_file_and_no_YoutubeId_is_treated_as_missing()
    {
        var (demos, root) = MakeHarness();
        try
        {
            var entry = await PublishSampleAsync(demos);
            var moviePath = demos.ResolveMoviePath(entry.Id)!;
            File.Delete(moviePath); // simulate corruption/partial write without a YouTube migration

            Assert.Null(await demos.TryGetAsync(entry.Id));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
