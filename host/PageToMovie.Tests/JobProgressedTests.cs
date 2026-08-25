using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The lost-job poll runs on its own timer whether or not the hub is healthy, and
/// JobLostOnRestart.ApplyServerView hands back a fresh object every tick — so ReferenceEquals is
/// true on a poll that learned nothing. Gating the "socket delivered nothing" warning and the
/// republish on object identity meant both fired once per tick on a perfectly healthy job
/// (2026-08-25: every update arrived over the socket and the page still cried wolf about it).
/// </summary>
public class JobProgressedTests
{
    private static JobSnapshot Snap(
        string status = "running", int index = 1, int total = 4, string? media = null) =>
        new()
        {
            JobId = "job1", Kind = "batch", Status = status,
            Index = index, Total = total, ClientMediaUrl = media,
        };

    [Fact]
    public void A_re_read_of_an_unchanged_job_is_not_progress()
    {
        Assert.False(Scenes.ScenesGeneration.JobProgressed(Snap(), Snap()));
    }

    [Fact]
    public void A_status_change_is_progress()
    {
        Assert.True(Scenes.ScenesGeneration.JobProgressed(Snap("running"), Snap("done")));
    }

    [Fact]
    public void Advancing_a_step_is_progress()
    {
        Assert.True(Scenes.ScenesGeneration.JobProgressed(Snap(index: 1), Snap(index: 2)));
    }

    /// <summary>A batch learns its size after it starts; the bar cannot render until it does.</summary>
    [Fact]
    public void Learning_the_total_is_progress()
    {
        Assert.True(Scenes.ScenesGeneration.JobProgressed(Snap(total: 0), Snap(total: 4)));
    }

    /// <summary>
    /// The one that must never be missed: ClientMediaUrl appearing is the only signal that a clip
    /// is ready to save, and the API host drops its copy right after publishing it.
    /// </summary>
    [Fact]
    public void A_new_clip_to_save_is_progress()
    {
        Assert.True(Scenes.ScenesGeneration.JobProgressed(
            Snap(media: null), Snap(media: "/api/media/proxy/abc")));
    }

    /// <summary>Status casing varies between the store and the wire; that is not a change.</summary>
    [Fact]
    public void Status_casing_alone_is_not_progress()
    {
        Assert.False(Scenes.ScenesGeneration.JobProgressed(Snap("Running"), Snap("running")));
    }
}
