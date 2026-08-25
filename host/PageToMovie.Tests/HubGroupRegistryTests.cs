using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A broadcast to a SignalR group with no members succeeds and delivers nothing, so an
/// undeliverable job is indistinguishable from a healthy one unless someone counts. These pin the
/// counting, which is what turns "no progress bar" into a server-side fact.
/// </summary>
public class HubGroupRegistryTests
{
    [Fact]
    public void An_unknown_user_has_no_connections()
    {
        var reg = new HubGroupRegistry();
        Assert.Equal(0, reg.Count("budcribar"));
        Assert.Equal(0, reg.Count(null));
        Assert.Equal("<none>", reg.Describe());
    }

    [Fact]
    public void Connections_accumulate_and_drain()
    {
        var reg = new HubGroupRegistry();
        reg.Add("u1");
        reg.Add("u1");
        Assert.Equal(2, reg.Count("u1"));
        reg.Remove("u1");
        Assert.Equal(1, reg.Count("u1"));
        reg.Remove("u1");
        Assert.Equal(0, reg.Count("u1"));
    }

    /// <summary>A double-disconnect must not push the count below zero, or the group looks live
    /// again after the next connect and the warning stops firing.</summary>
    [Fact]
    public void Removing_more_than_were_added_floors_at_zero()
    {
        var reg = new HubGroupRegistry();
        reg.Add("u1");
        reg.Remove("u1");
        reg.Remove("u1");
        Assert.Equal(0, reg.Count("u1"));
        reg.Add("u1");
        Assert.Equal(1, reg.Count("u1"));
    }

    /// <summary>
    /// SignalR matches group names ordinally, so "Budcribar" and "budcribar" are two groups and a
    /// publish to one reaches neither's subscribers. Describe() must keep them apart, since seeing
    /// the near-miss spelled out is the whole point of the diagnostic.
    /// </summary>
    [Fact]
    public void Case_differences_are_distinct_groups()
    {
        var reg = new HubGroupRegistry();
        reg.Add("budcribar");
        Assert.Equal(0, reg.Count("Budcribar"));
        Assert.Contains("budcribar(1)", reg.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_omits_groups_that_have_fully_disconnected()
    {
        var reg = new HubGroupRegistry();
        reg.Add("gone");
        reg.Add("here");
        reg.Remove("gone");
        var desc = reg.Describe();
        Assert.Contains("here(1)", desc, StringComparison.Ordinal);
        Assert.DoesNotContain("gone", desc, StringComparison.Ordinal);
    }
}
