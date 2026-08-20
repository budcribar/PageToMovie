namespace PageToMovie.Core.Models;

/// <summary>
/// Subjects <c>plan_looks</c> would generate when <see cref="StartPlanLooksRequest.SkipAlreadyLocked"/>
/// is true. Same filters as <c>FilmJobService.CollectPlanLookTargets</c> /
/// auto-queue after Stage 2 — used-in-plan faces (not group / voice-only) and used-in-plan places
/// that do not already have a locked plate.
/// </summary>
public static class PlanLooksWork
{
    public static bool IsCastFaceSubject(CharacterSummary c) =>
        c.UsedInPlan && !c.IsGroup && !c.VoiceOnly;

    public static bool IsLocationSubject(LocationSummary l) =>
        l.UsedInPlan;

    public static bool NeedsLook(CharacterSummary c, bool skipAlreadyLocked = true) =>
        IsCastFaceSubject(c) && (!skipAlreadyLocked || !c.Locked);

    public static bool NeedsLook(LocationSummary l, bool skipAlreadyLocked = true) =>
        IsLocationSubject(l) && (!skipAlreadyLocked || !l.Locked);

    /// <summary>
    /// True when a SkipAlreadyLocked plan_looks job would be a no-op: every used face and place
    /// already has a lock, or there are none in the plan.
    /// </summary>
    public static bool AllUsedLooksLocked(
        IEnumerable<CharacterSummary>? cast,
        IEnumerable<LocationSummary>? locations)
    {
        var castNeed = cast?.Any(c => NeedsLook(c)) == true;
        var locNeed = locations?.Any(l => NeedsLook(l)) == true;
        return !castNeed && !locNeed;
    }
}
