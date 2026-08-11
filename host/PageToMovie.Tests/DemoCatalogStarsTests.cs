using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class DemoCatalogStarsTests
{
    [Fact]
    public void TotalStars_CombinesLocalUpvotesAndYouTubeLikes()
    {
        // Arrange
        var item = new DemoListItem
        {
            UpvoteCount = 5,
            YoutubeLikeCount = 12
        };

        // Act & Assert
        Assert.Equal(17uL, item.TotalStars);
    }

    [Fact]
    public void TotalStars_HandlesNullOrZeroYouTubeLikes()
    {
        // Arrange
        var itemNull = new DemoListItem { UpvoteCount = 8, YoutubeLikeCount = null };
        var itemZero = new DemoListItem { UpvoteCount = 3, YoutubeLikeCount = 0 };

        // Act & Assert
        Assert.Equal(8uL, itemNull.TotalStars);
        Assert.Equal(3uL, itemZero.TotalStars);
    }

    [Fact]
    public async Task SetYouTubeStats_UpdatesCatalogEntryLikesAndViews()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_stats_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
            var projects = new ProjectStore(opts);
            var demos = new DemoCatalogService(projects, NullLogger<DemoCatalogService>.Instance);

            // Add a demo entry with valid MP4 header bytes
            var mp4Bytes = System.Text.Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
            var entry = await demos.PublishFromStreamAsync(
                new MemoryStream(mp4Bytes),
                "Test Film",
                "Desc",
                "test-proj",
                "admin",
                acceptedGuidelines: true);

            // Act: Update stats with YouTube likes and views
            await demos.SetYouTubeStatsAsync(entry.Id, likeCount: 42, viewCount: 150);
            var updated = await demos.TryGetAsync(entry.Id);

            // Assert
            Assert.NotNull(updated);
            Assert.Equal(42uL, updated.YoutubeLikeCount);
            Assert.Equal(150uL, updated.YoutubeViewCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
