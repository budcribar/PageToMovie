using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The finish editor builds the movie from whatever video files are in this browser's media folder
/// and knows nothing of the shot plan. A seventeen-scene film with two files on hand therefore
/// produced a nine-second cut that looked finished, because the timeline was showing everything it
/// had. The page already holds all three counts; it just never compared them.
/// </summary>
public class FinishCutMissingClipsTests
{
    [Fact]
    public void A_cut_holding_every_planned_clip_says_nothing()
    {
        Assert.Null(ReviewFinishTab.Reconcile(planned: 29, onServer: 29, local: 29));
        // More local files than the plan (extra takes, older scenes) is not a shortfall either.
        Assert.Null(ReviewFinishTab.Reconcile(planned: 29, onServer: 29, local: 31));
    }

    [Fact]
    public void Clips_that_exist_but_are_not_in_this_folder_read_as_a_folder_problem()
    {
        var gap = ReviewFinishTab.Reconcile(planned: 29, onServer: 29, local: 2);

        Assert.NotNull(gap);
        Assert.Equal(27, gap!.NotHere);
        Assert.Equal(0, gap.NotMade);
    }

    [Fact]
    public void Clips_that_exist_nowhere_read_as_unfinished_work()
    {
        var gap = ReviewFinishTab.Reconcile(planned: 29, onServer: 2, local: 2);

        Assert.NotNull(gap);
        Assert.Equal(0, gap!.NotHere);
        Assert.Equal(27, gap.NotMade);
    }

    [Fact]
    public void A_mixed_shortfall_names_both_causes()
    {
        var gap = ReviewFinishTab.Reconcile(planned: 29, onServer: 20, local: 2);

        Assert.NotNull(gap);
        Assert.Equal(18, gap!.NotHere);
        Assert.Equal(9, gap.NotMade);
        Assert.Equal(29, gap.NotHere + gap.NotMade + gap.Local);
    }

    [Fact]
    public void A_server_that_pruned_synced_clips_is_not_read_as_clips_going_missing()
    {
        // The server deletes clip bytes once a browser has taken them, so its count runs behind the
        // folder's. That is evidence something exists, never evidence something is absent — reading
        // it the other way would accuse the operator of losing files they are holding.
        var gap = ReviewFinishTab.Reconcile(planned: 29, onServer: 0, local: 20);

        Assert.NotNull(gap);
        Assert.Equal(0, gap!.NotHere);
        Assert.Equal(9, gap.NotMade);
    }
}
