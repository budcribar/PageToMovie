using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ReviewTabQueryTests
{
    [Theory]
    [InlineData("review", ReviewTab.Review)]
    [InlineData("approve", ReviewTab.Review)]
    [InlineData("play", ReviewTab.Finish)]
    [InlineData("share", ReviewTab.Share)]
    [InlineData("finish", ReviewTab.Finish)]
    [InlineData("Review", ReviewTab.Review)]
    [InlineData("FINISH", ReviewTab.Finish)]
    public void TryParseTab_accepts_known_values(string raw, ReviewTab expected)
    {
        Assert.True(Review.ReviewListState.TryParseTab(raw, out var tab));
        Assert.Equal(expected, tab);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("cut")]
    [InlineData("editor")]
    [InlineData("unknown")]
    [InlineData("0")]
    [InlineData("1")]
    public void TryParseTab_ignores_unknown_values(string? raw)
    {
        Assert.False(Review.ReviewListState.TryParseTab(raw, out _));
    }

    [Fact]
    public void Review_header_is_finish_then_review_then_share()
    {
        var razor = File.ReadAllText(ReviewPagePath("Review.razor"));
        Assert.DoesNotContain("review-tab-play", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("review-open-editor", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("review-dub-my-voice", razor, StringComparison.Ordinal);

        var finish = razor.IndexOf("review-tab-finish", StringComparison.Ordinal);
        var review = razor.IndexOf("review-tab-review", StringComparison.Ordinal);
        var share = razor.IndexOf("review-tab-share", StringComparison.Ordinal);
        Assert.True(finish >= 0 && review > finish && share > review);

        var list = File.ReadAllText(ReviewPagePath("ReviewListState.cs"));
        Assert.Contains("internal ReviewTab? _activeTab = ReviewTab.Finish;", list, StringComparison.Ordinal);
    }

    private static string ReviewPagePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Review page file '{fileName}' not found.");
    }
}
