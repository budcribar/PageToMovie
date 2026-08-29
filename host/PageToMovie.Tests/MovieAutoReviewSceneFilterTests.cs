using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
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
    public void MovieReport_filters_groups_when_a_scene_is_selected()
    {
        var report = new MovieAutoReviewReport
        {
            OverallScore = 5,
            GroupFeedback =
            {
                new MovieSceneGroupFeedback { SceneRange = "Scenes 1-2", SceneNumbers = { 1, 2 } },
                new MovieSceneGroupFeedback { SceneRange = "Scenes 3-4", SceneNumbers = { 3, 4 } }
            }
        };

        var filtered = new Review_MovieReport { Report = report, FilterSceneNumber = 4 };
        Assert.True(filtered.ShowCard);
        Assert.False(filtered.ShowMovieOverview);
        Assert.True(filtered.ShowBody);
        Assert.Single(filtered.VisibleGroups);
        Assert.Equal("Scenes 3-4", filtered.VisibleGroups[0].SceneRange);

        var overall = new Review_MovieReport { Report = report, FilterSceneNumber = null, Collapsed = true };
        Assert.True(overall.ShowMovieOverview);
        Assert.False(overall.ShowBody);
        Assert.Equal(2, overall.VisibleGroups.Count);
    }
}
