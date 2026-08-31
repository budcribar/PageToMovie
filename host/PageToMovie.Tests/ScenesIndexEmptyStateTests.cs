using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Film scene-index first paint: a null list is "still loading", not "no scenes yet".
/// After load, an empty list is the real empty shot-plan card.
/// </summary>
public class ScenesIndexEmptyStateTests
{
    [Fact]
    public void Null_list_is_pending_not_empty()
    {
        Assert.True(Scenes.ScenesListState.IsSceneListPending(null));
        Assert.False(Scenes.ScenesListState.IsEmptyShotPlan(null));
    }

    [Fact]
    public void Empty_loaded_list_is_the_real_empty_shot_plan()
    {
        var empty = new List<SceneSummary>();
        Assert.False(Scenes.ScenesListState.IsSceneListPending(empty));
        Assert.True(Scenes.ScenesListState.IsEmptyShotPlan(empty));
    }

    [Fact]
    public void Populated_list_is_neither_pending_nor_empty()
    {
        var scenes = new List<SceneSummary> { new() { SceneNumber = 1 } };
        Assert.False(Scenes.ScenesListState.IsSceneListPending(scenes));
        Assert.False(Scenes.ScenesListState.IsEmptyShotPlan(scenes));
    }

    [Fact]
    public void Index_markup_shows_loading_for_pending_list_not_empty_copy()
    {
        var razor = ReadPage("ScenesSceneIndex.razor");

        Assert.Contains("IsSceneListPending(list._scenes)", razor, StringComparison.Ordinal);
        Assert.Contains("Loading scenes…", razor, StringComparison.Ordinal);
        Assert.Contains("TestId=\"scenes-loading\"", razor, StringComparison.Ordinal);
        Assert.Contains("No shot plan yet", razor, StringComparison.Ordinal);
        Assert.Contains("scenes-empty-shotplan", razor, StringComparison.Ordinal);

        // First-paint used to hit this copy when _scenes was null and _busy was still false.
        Assert.DoesNotContain("No scene list yet", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMissingSceneList", razor, StringComparison.Ordinal);

        var pendingAt = razor.IndexOf("IsSceneListPending(list._scenes)", StringComparison.Ordinal);
        var emptyBranchAt = razor.IndexOf("IsEmptyShotPlan(list._scenes)", StringComparison.Ordinal);
        var loadingAt = razor.IndexOf("Loading scenes…", StringComparison.Ordinal);
        var emptyCopyAt = razor.IndexOf("No shot plan yet", StringComparison.Ordinal);
        Assert.True(pendingAt >= 0 && loadingAt > pendingAt, "pending branch must show Loading scenes…");
        Assert.True(emptyBranchAt > pendingAt && emptyCopyAt > emptyBranchAt,
            "real empty card stays after the pending/loading branch");

        var pendingBlock = razor[pendingAt..emptyBranchAt];
        Assert.Contains("Loading scenes…", pendingBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Host._busy", pendingBlock);
        Assert.DoesNotContain("No shot plan yet", pendingBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("No scenes", pendingBlock, StringComparison.Ordinal);
    }

    private static string ReadPage(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
