using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

public sealed class MovieAutoReviewSceneFilterTests
{
    [Fact]
    public void GroupsForScene_uses_explicit_scene_numbers()
    {
        var report = new MovieAutoReviewReport
        {
            GroupFeedback =
            {
                new MovieSceneGroupFeedback { SceneRange = "Scenes 1-2", SceneNumbers = { 1, 2 }, Score = 6 },
                new MovieSceneGroupFeedback { SceneRange = "Scenes 3-4", SceneNumbers = { 3, 4 }, Score = 8 }
            }
        };

        Assert.Single(report.GroupsForScene(3));
        Assert.Equal("Scenes 3-4", report.GroupsForScene(3)[0].SceneRange);
        Assert.Empty(report.GroupsForScene(9));
    }

    [Fact]
    public void GroupsForScene_falls_back_to_range_text_when_numbers_missing()
    {
        var report = new MovieAutoReviewReport
        {
            GroupFeedback =
            {
                new MovieSceneGroupFeedback { SceneRange = "Scenes 1-4", Score = 5 }
            }
        };

        Assert.True(report.GroupFeedback[0].IncludesScene(2));
        Assert.False(report.GroupFeedback[0].IncludesScene(5));
        Assert.Single(report.GroupsForScene(4));
    }

    [Theory]
    [InlineData("Scenes 1-4", 1, true)]
    [InlineData("Scenes 1-4", 4, true)]
    [InlineData("Scenes 1-4", 5, false)]
    [InlineData("Scene 3", 3, true)]
    [InlineData("Scene 3", 2, false)]
    [InlineData("", 1, false)]
    public void RangeTextIncludesScene_covers_labeled_bands(string range, int scene, bool expected)
    {
        Assert.Equal(expected, MovieSceneGroupFeedback.RangeTextIncludesScene(range, scene));
    }

    [Fact]
    public void MovieReport_razor_filters_groups_for_the_selected_scene()
    {
        var razor = File.ReadAllText(ReviewPagePath("Review.MovieReport.razor"));
        var cs = File.ReadAllText(ReviewPagePath("Review.MovieReport.razor.cs"));
        Assert.Contains("FilterSceneNumber", cs, StringComparison.Ordinal);
        Assert.Contains("GroupsForScene", cs, StringComparison.Ordinal);
        Assert.Contains("ShowMovieOverview", razor, StringComparison.Ordinal);
        Assert.Contains("VisibleGroups", razor, StringComparison.Ordinal);
        Assert.Contains("DialogueNotes", razor, StringComparison.Ordinal);
        Assert.Contains("group.Evidence", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Audio &amp; Dialogue Alignment", razor, StringComparison.Ordinal);
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
}
