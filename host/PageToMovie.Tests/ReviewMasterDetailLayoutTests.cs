using Xunit;

namespace PageToMovie.Tests;

public sealed class ReviewMasterDetailLayoutTests
{
    [Fact]
    public void Review_tab_is_side_by_side_master_detail()
    {
        var tab = File.ReadAllText(ReviewPagePath("ReviewReviewTab.razor"));
        var masterAt = tab.IndexOf("review-master-pane", StringComparison.Ordinal);
        var detailAt = tab.IndexOf("review-detail-pane", StringComparison.Ordinal);
        var listAt = tab.IndexOf("Review_SceneList", StringComparison.Ordinal);
        var reportAt = tab.IndexOf("Review_MovieReport", StringComparison.Ordinal);

        Assert.Contains("review-master-detail", tab, StringComparison.Ordinal);
        Assert.Contains("class=\"review-md\"", tab, StringComparison.Ordinal);
        Assert.True(masterAt >= 0 && detailAt > masterAt, "master pane must precede detail pane");
        Assert.True(listAt >= 0 && listAt < detailAt, "scene list lives in the master pane");
        Assert.True(reportAt > detailAt, "executive review lives in the detail pane");
        Assert.Contains("Review_ClipReview", tab, StringComparison.Ordinal);
    }

    [Fact]
    public void Scene_list_has_full_movie_row_and_play_approve_clips()
    {
        var list = File.ReadAllText(ReviewPagePath("Review.SceneList.razor"));
        var fullAt = list.IndexOf("review-full-movie-row", StringComparison.Ordinal);
        var sceneAt = list.IndexOf("review-scene-row", StringComparison.Ordinal);

        Assert.True(fullAt >= 0 && sceneAt > fullAt, "Full Movie row sits above scene rows");
        Assert.Contains("list.SelectOverall", list, StringComparison.Ordinal);
        Assert.Contains("review-play-scene-{sn}", list, StringComparison.Ordinal);
        Assert.Contains("review-approve-{sn}", list, StringComparison.Ordinal);
        Assert.Contains("review-clips-{sn}", list, StringComparison.Ordinal);
        Assert.Contains("L[\"Review.FullMovie\"]", list, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_css_is_two_columns_and_stacks_on_narrow()
    {
        var css = File.ReadAllText(CssPath());
        var blockAt = css.IndexOf(".review-md {", StringComparison.Ordinal);
        Assert.True(blockAt >= 0, "review-md grid missing");
        var block = css[blockAt..];
        Assert.Contains("grid-template-columns: minmax(22rem, 42%) minmax(0, 1fr)", block, StringComparison.Ordinal);

        var narrowAt = css.IndexOf("@media (max-width: 991.98px)", StringComparison.Ordinal);
        Assert.True(narrowAt > blockAt, "narrow breakpoint must follow the desktop grid");
        var narrow = css[narrowAt..];
        Assert.Contains(".review-md {", narrow, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 1fr", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_razor_keeps_top_actions_and_checklist()
    {
        var page = File.ReadAllText(ReviewPagePath("Review.razor"));
        Assert.Contains("review-checklist", page, StringComparison.Ordinal);
        Assert.Contains("review-tab-review", page, StringComparison.Ordinal);
        Assert.Contains("review-tab-share", page, StringComparison.Ordinal);
        Assert.Contains("review-auto-all", page, StringComparison.Ordinal);
        Assert.Contains("ReviewPage.ActivityHistory", page, StringComparison.Ordinal);
    }

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

    private static string CssPath()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(d.FullName, "host", "PageToMovie.Web", "wwwroot", "app.css");
            if (File.Exists(candidate))
                return candidate;
            d = d.Parent;
        }

        throw new FileNotFoundException("app.css");
    }
}
