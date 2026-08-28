using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A succeeded job used to keep its progress card on the page for good — most visibly a batch that
/// finished with "No clips to generate", which held a fixed slice of every Review screen for news
/// the scene table already carried.
/// </summary>
public class JobProgressCardVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Work_in_flight_always_keeps_its_card()
    {
        Assert.True(Snap("running", startedAt: Now.AddHours(-3)).DeservesProgressCard(Now));
        Assert.True(Snap("queued", startedAt: null).DeservesProgressCard(Now));
    }

    [Fact]
    public void Failures_keep_their_card_however_old()
    {
        // The card is the only place the reason shows, so age must not retire it.
        Assert.True(Snap("error", finishedAt: Now.AddDays(-2)).DeservesProgressCard(Now));
        Assert.True(Snap("cancelled", finishedAt: Now.AddDays(-2)).DeservesProgressCard(Now));
    }

    [Fact]
    public void A_success_keeps_its_card_briefly_then_stops_holding_space()
    {
        Assert.True(Snap("done", finishedAt: Now.AddSeconds(-5)).DeservesProgressCard(Now));
        Assert.True(Snap("partial", finishedAt: Now.AddSeconds(-5)).DeservesProgressCard(Now));

        Assert.False(Snap("done", finishedAt: Now - JobSnapshot.ProgressCardLinger).DeservesProgressCard(Now));
        Assert.False(Snap("done", finishedAt: Now.AddHours(-6)).DeservesProgressCard(Now));
    }

    [Fact]
    public void A_success_with_no_timestamps_is_kept_rather_than_hidden_unexplained()
    {
        Assert.True(Snap("done").DeservesProgressCard(Now));

        // StartedAt stands in when the finish stamp is missing.
        Assert.False(Snap("done", startedAt: Now.AddHours(-6)).DeservesProgressCard(Now));
    }

    private static JobSnapshot Snap(
        string status,
        DateTimeOffset? finishedAt = null,
        DateTimeOffset? startedAt = null) => new()
        {
            Status = status,
            FinishedAt = finishedAt,
            StartedAt = startedAt,
        };
}
