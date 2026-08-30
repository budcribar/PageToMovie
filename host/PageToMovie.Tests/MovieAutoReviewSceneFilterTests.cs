using PageToMovie.Core.Models;
using PageToMovie.Engine;
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

    [Fact]
    public void A_passing_group_flags_only_the_scenes_its_evidence_cites()
    {
        var cited = new MovieSceneGroupFeedback
        {
            SceneRange = "Scenes 1-4",
            SceneNumbers = { 1, 2, 3, 4 },
            Score = 8,
            VisualConsistencyNotes = "background art jumps to 3D render",
            Evidence = { new MovieReviewEvidence { Ref = "S03C01", Claim = "2D watercolor to 3D render" } },
        };

        Assert.Equal(new[] { 3 }, MovieAutoReviewService.CollectFlaggedScenes([cited]));
    }

    [Fact]
    public void Praise_that_names_the_medium_does_not_flag_a_passing_group()
    {
        // The keyword scan cannot tell "stays photoreal throughout" from a complaint, so a group
        // with no cite has nothing to send anyone back to.
        var praise = new MovieSceneGroupFeedback
        {
            SceneRange = "Scenes 1-4",
            SceneNumbers = { 1, 2, 3, 4 },
            Score = 9,
            VisualConsistencyNotes = "Character look stays photoreal throughout, no drift",
        };

        Assert.Empty(MovieAutoReviewService.CollectFlaggedScenes([praise]));
    }

    [Fact]
    public void A_failing_group_still_flags_every_scene_it_covers()
    {
        var failing = new MovieSceneGroupFeedback
        {
            SceneRange = "Scenes 5-6", SceneNumbers = { 5, 6 }, Score = 4,
        };

        Assert.Equal(new[] { 5, 6 }, MovieAutoReviewService.CollectFlaggedScenes([failing]));
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
