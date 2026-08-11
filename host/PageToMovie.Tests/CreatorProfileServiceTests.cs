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

public class CreatorProfileServiceTests
{
    private static (CreatorProfileService ProfileService, DemoCatalogService Demos, DemoUpvoteService Upvotes, UserDatabaseService Users, ProjectStore Projects, string Root) MakeHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_creator_profile_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
        var projects = new ProjectStore(opts);
        var demos = new DemoCatalogService(projects, NullLogger<DemoCatalogService>.Instance);
        var upvotes = new DemoUpvoteService(opts, NullLogger<DemoUpvoteService>.Instance);
        var users = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);
        var profileService = new CreatorProfileService(users, demos, upvotes, projects, NullLogger<CreatorProfileService>.Instance);
        return (profileService, demos, upvotes, users, projects, root);
    }

    [Fact]
    public async Task Computes_Creator_Stats_And_Badges_Correctly()
    {
        var (profileService, demos, upvotes, users, projects, root) = MakeHarness();
        try
        {
            // Register user
            await users.AcceptTermsAsync("filmmaker_alice");

            // Publish a sample demo by filmmaker_alice
            var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
            await using var stream = new MemoryStream(bytes);
            var entry = await demos.PublishFromStreamAsync(
                stream, "Alice Movie", "Description", "proj1", "filmmaker_alice", acceptedGuidelines: true);

            await demos.SetStatusAsync(entry.Id, DemoCatalogService.DemoStatuses.Public, "admin", "Approved");
            // CreatorProfileService counts demos via DemoCatalogService.ListPublic, which requires
            // a YoutubeId (YouTube is the gallery source of truth) — simulate a completed upload
            // so this demo is actually counted as "published".
            await demos.SetYouTubeUploadStatusAsync(entry.Id, "done", youtubeId: "yt_" + Guid.NewGuid().ToString("N")[..8]);

            // Add an upvote
            upvotes.TryAdd(entry.Id, "user_bob");

            // Fetch profile
            var profile = await profileService.GetProfileAsync("filmmaker_alice");

            Assert.NotNull(profile);
            Assert.Equal("filmmaker_alice", profile!.Username);
            Assert.Equal(1, profile.MoviesPublished);
            Assert.Equal(1, profile.TotalUpvotes);
            Assert.Contains(profile.Badges, b => b.Id == "debut_director");
            Assert.Contains(profile.Badges, b => b.Id == "featured_filmmaker");
        }
        finally
        {
            if (Directory.Exists(root))
                try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Returns_Null_For_Empty_Handle()
    {
        var (profileService, _, _, _, _, root) = MakeHarness();
        try
        {
            var profile = await profileService.GetProfileAsync("");
            Assert.Null(profile);
        }
        finally
        {
            if (Directory.Exists(root))
                try { Directory.Delete(root, true); } catch { }
        }
    }
}
