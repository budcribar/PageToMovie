using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// plan_looks SkipAlreadyLocked filters — same rules the Cast/Locations buttons use
/// to hide "Generate looks for plan" when the job would be a no-op.
/// </summary>
public class PlanLooksWorkTests
{
    [Fact]
    public void Used_unlocked_face_needs_a_look()
    {
        Assert.True(PlanLooksWork.NeedsLook(Face(used: true, locked: false)));
        Assert.False(PlanLooksWork.NeedsLook(Face(used: true, locked: true)));
        Assert.False(PlanLooksWork.NeedsLook(Face(used: false, locked: false)));
    }

    [Fact]
    public void Group_and_voice_only_faces_are_not_plan_look_subjects()
    {
        Assert.False(PlanLooksWork.NeedsLook(new CharacterSummary { UsedInPlan = true, IsGroup = true }));
        Assert.False(PlanLooksWork.NeedsLook(new CharacterSummary { UsedInPlan = true, VoiceOnly = true }));
    }

    [Fact]
    public void Used_unlocked_place_needs_a_look()
    {
        Assert.True(PlanLooksWork.NeedsLook(Place(used: true, locked: false)));
        Assert.False(PlanLooksWork.NeedsLook(Place(used: true, locked: true)));
        Assert.False(PlanLooksWork.NeedsLook(Place(used: false, locked: false)));
    }

    [Fact]
    public void All_used_looks_locked_when_every_used_face_and_place_is_locked()
    {
        var cast = new[]
        {
            Face(used: true, locked: true),
            Face(used: false, locked: false),
            new CharacterSummary { UsedInPlan = true, IsGroup = true, Locked = false },
        };
        var locs = new[]
        {
            Place(used: true, locked: true),
            Place(used: false, locked: false),
        };
        Assert.True(PlanLooksWork.AllUsedLooksLocked(cast, locs));
    }

    [Fact]
    public void All_used_looks_locked_is_false_when_a_used_face_or_place_is_unlocked()
    {
        var lockedCast = new[] { Face(used: true, locked: true) };
        var lockedLocs = new[] { Place(used: true, locked: true) };
        Assert.False(PlanLooksWork.AllUsedLooksLocked(
            new[] { Face(used: true, locked: false) }, lockedLocs));
        Assert.False(PlanLooksWork.AllUsedLooksLocked(
            lockedCast, new[] { Place(used: true, locked: false) }));
    }

    [Fact]
    public void Empty_or_none_in_plan_is_already_done_noop()
    {
        Assert.True(PlanLooksWork.AllUsedLooksLocked(null, null));
        Assert.True(PlanLooksWork.AllUsedLooksLocked(Array.Empty<CharacterSummary>(), Array.Empty<LocationSummary>()));
        Assert.True(PlanLooksWork.AllUsedLooksLocked(
            new[] { Face(used: false, locked: false) },
            new[] { Place(used: false, locked: false) }));
    }

    [Fact]
    public void SkipAlreadyLocked_false_still_targets_locked_subjects()
    {
        Assert.True(PlanLooksWork.NeedsLook(Face(used: true, locked: true), skipAlreadyLocked: false));
        Assert.True(PlanLooksWork.NeedsLook(Place(used: true, locked: true), skipAlreadyLocked: false));
    }

    private static CharacterSummary Face(bool used, bool locked) =>
        new() { UsedInPlan = used, Locked = locked, Key = used ? "hero" : "extra" };

    private static LocationSummary Place(bool used, bool locked) =>
        new() { UsedInPlan = used, Locked = locked, Key = used ? "kitchen" : "attic" };
}
