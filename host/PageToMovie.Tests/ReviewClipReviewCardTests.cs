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
        Assert.Contains("playback.CanPlayScene(sn)", razor, StringComparison.Ordinal);
        Assert.Contains("playback.PlayClipAsync(sel, cn)", razor, StringComparison.Ordinal);
        Assert.Contains("list.ClipIsPlayable(sel, cn)", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewReviewTab_clip_play_does_not_use_scene_completeness()
    {
        var razor = File.ReadAllText(ReviewReviewTabPath());
        var playAt = razor.IndexOf("playback.PlayClipAsync(sel, cn)", StringComparison.Ordinal);
        Assert.True(playAt >= 0);
        var window = razor[Math.Max(0, playAt - 500)..Math.Min(razor.Length, playAt + 40)];
        Assert.DoesNotContain("CanPlayScene", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ClipsOnDisk >= s.ClipCount", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewReviewTab_clip_play_button_is_success_styled_and_testable()
    {
        var razor = File.ReadAllText(ReviewReviewTabPath());
        var playAt = razor.IndexOf("playback.PlayClipAsync(sel, cn)", StringComparison.Ordinal);
        Assert.True(playAt >= 0, "per-clip Play handler missing");
        var window = razor[Math.Max(0, playAt - 500)..Math.Min(razor.Length, playAt + 40)];

        Assert.Contains("btn btn-sm btn-success", window, StringComparison.Ordinal);
        Assert.Contains("review-play-clip-{sel}-{cn}", window, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@(Host._busy || !playable)\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("btn-outline-dark", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewReviewTab_shows_in_card_clip_play_error()
    {
        var razor = File.ReadAllText(ReviewReviewTabPath());
        var cardAt = razor.IndexOf("review-clip-review-card", StringComparison.Ordinal);
        Assert.True(cardAt >= 0);
        var card = razor[cardAt..];
        Assert.Contains("review-clip-play-error", card, StringComparison.Ordinal);
        Assert.Contains("playback._clipPlayError", card, StringComparison.Ordinal);
        Assert.Contains("playback._clientClipUrl", card, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewPlayTab_clip_player_uses_resolved_url_not_server_fallback()
    {
        var razor = File.ReadAllText(ReviewPlayTabPath());
        Assert.Contains("Playback._clientClipUrl", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("ClipServerSrc", razor, StringComparison.Ordinal);
        Assert.Contains("review-clip-play-error", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void DecideClipPlay_prefers_local_blob_and_clears_error()
    {
        var (src, error) = Review.ReviewPlayback.DecideClipPlay(
            new[] { "blob:http://localhost/clip-1" },
            collectError: null,
            scene: 1,
            clip: 2,
            mediaFolderConnected: true);
        Assert.Equal("blob:http://localhost/clip-1", src);
        Assert.Null(error);
    }

    [Fact]
    public void DecideClipPlay_uses_reachable_server_url_when_that_is_what_collect_returned()
    {
        var server = "http://localhost/api/projects/p/scenes/1/clips/2/video";
        var (src, error) = Review.ReviewPlayback.DecideClipPlay(
            new[] { server }, null, 1, 2, mediaFolderConnected: false);
        Assert.Equal(server, src);
        Assert.Null(error);
    }

    [Fact]
    public void DecideClipPlay_missing_clip_has_friendly_alert_and_no_src()
    {
        var collect = ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S01 C02" }, false);
        var (src, error) = Review.ReviewPlayback.DecideClipPlay(
            Array.Empty<string>(), collect, 1, 2, mediaFolderConnected: false);
        Assert.Null(src);
        Assert.Contains("S01 C02", error, StringComparison.Ordinal);
        Assert.Contains("local media folder", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("404", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HTTP 404")]
    [InlineData("clip video not found")]
    public void DecideClipPlay_rewrites_http_missing_and_does_not_return_src(string raw)
    {
        var (src, error) = Review.ReviewPlayback.DecideClipPlay(
            Array.Empty<string>(), raw, 2, 3, mediaFolderConnected: false);
        Assert.Null(src);
        Assert.DoesNotContain("404", error, StringComparison.Ordinal);
        Assert.Contains("S02 C03", error, StringComparison.Ordinal);
        Assert.Contains("local media folder", error, StringComparison.OrdinalIgnoreCase);
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
            2, ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S02 C01" }, false), false);
        Assert.Contains("S02 C01", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("404", msg, StringComparison.Ordinal);
    }

    private static string ReviewReviewTabPath() => ReviewPagePath("ReviewReviewTab.razor");

    private static string ReviewPlayTabPath() => ReviewPagePath("ReviewPlayTab.razor");

    private static string ReviewPagePath(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return candidate;
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
