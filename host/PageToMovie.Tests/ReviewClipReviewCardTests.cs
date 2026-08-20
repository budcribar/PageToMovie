using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class ReviewClipReviewCardTests
{
    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(2, 1, 1)]
    public void ToggleSelectedScene_opens_the_clicked_scene(int? current, int clicked, int expected)
    {
        Assert.Equal(expected, Review.ReviewListState.ToggleSelectedScene(current, clicked));
    }

    [Fact]
    public void ToggleSelectedScene_clears_when_the_same_scene_is_clicked_again()
    {
        Assert.Null(Review.ReviewListState.ToggleSelectedScene(3, 3));
    }

    [Fact]
    public void ReviewReviewTab_clip_review_header_has_dismiss_not_play_scene()
    {
        var razor = File.ReadAllText(ReviewReviewTabPath());
        var cardAt = razor.IndexOf("review-clip-review-card", StringComparison.Ordinal);
        Assert.True(cardAt >= 0, "clip-review card test id missing");

        var card = razor[cardAt..];
        var bodyAt = card.IndexOf("card-body", StringComparison.Ordinal);
        Assert.True(bodyAt > 0, "clip-review card body missing");
        var header = card[..bodyAt];

        Assert.Contains("review-dismiss-clip-review", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Play scene", header, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaySceneAsync", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewReviewTab_keeps_scene_row_play_and_per_clip_play()
    {
        var razor = File.ReadAllText(ReviewReviewTabPath());
        Assert.Contains("review-play-scene-{sn}", razor, StringComparison.Ordinal);
        Assert.Contains("playback.PlayClip(sel, cn)", razor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HTTP 404")]
    [InlineData("clip video not found")]
    [InlineData("Failed to fetch")]
    [InlineData("Not Found")]
    public void FriendlyStitchError_rewrites_raw_missing_media_errors(string raw)
    {
        Assert.True(Review.ReviewPlayback.LooksLikeHttpMissing(raw));
        var msg = Review.ReviewPlayback.FriendlyStitchError(1, raw);
        Assert.DoesNotContain("404", msg, StringComparison.Ordinal);
        Assert.Contains("S01", msg, StringComparison.Ordinal);
        Assert.Contains("local media folder", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendlyMissingClipsError_prefers_collect_error()
    {
        var msg = Review.ReviewPlayback.FriendlyMissingClipsError(
            2, ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S02C01" }, false), false);
        Assert.Contains("S02C01", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("404", msg, StringComparison.Ordinal);
    }

    private static string ReviewReviewTabPath()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", "ReviewReviewTab.razor");
            if (File.Exists(candidate))
                return candidate;
            d = d.Parent;
        }

        throw new FileNotFoundException("ReviewReviewTab.razor");
    }
}
