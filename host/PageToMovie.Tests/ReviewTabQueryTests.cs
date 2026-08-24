using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ReviewTabQueryTests
{
    [Theory]
    [InlineData("review", ReviewTab.Review)]
    [InlineData("play", ReviewTab.Play)]
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
}
