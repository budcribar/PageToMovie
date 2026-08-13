namespace PageToMovie.Engine.Deterministic;

/// <summary>Local, no-network fallback for action timing classification.</summary>
public sealed class ActionOverheadHeuristic
{
    public ActionClassifierEstimation Classify(string actionDescription, string? parenthetical = null)
    {
        var text = $"{actionDescription} {parenthetical}".Trim().ToLowerInvariant();

        if (ContainsAny(text, "pills", "medicine", "sorting")) return Result("act_pills_sorting", 2.3, .92, "Matched elderly-care or sorting action.");
        if (ContainsAny(text, "knife", "blade", "weapon")) return Result("act_knife_pull", 2.0, .90, "Matched weapon-draw action.");
        if (ContainsAny(text, "stab", "attack")) return Result("act_stabbing", 3.1, .95, "Matched physical attack action.");
        if (ContainsAny(text, "crash", "collision")) return Result("car_broadside_crash", 2.0, .94, "Matched vehicle collision.");
        if (ContainsAny(text, "car", "drive", "vehicle", "trans am")) return Result("car_muscle_drive", 2.3, .88, "Matched vehicle movement.");
        if (ContainsAny(text, "yoga", "mat", "meditation", "corpse pose")) return Result("act_yoga_pose", 2.4, .91, "Matched deliberate held pose.");
        if (ContainsAny(text, "weights", "barbell", "curl")) return Result("act_weightlifting", 2.8, .89, "Matched weightlifting action.");
        if (ContainsAny(text, "unshutter")) return Result("act_lantern_unshutter", 1.9, .92, "Matched lantern manipulation.");
        if (ContainsAny(text, "creeping", "darkness", "lantern", "stealth")) return Result("act_creeping_step", 2.8, .93, "Matched slow stealth action.");
        if (ContainsAny(text, "shriek", "scream")) return Result("act_sudden_shriek", 1.4, .95, "Matched sudden vocal reaction.");
        if (ContainsAny(text, "floorboard", "dismantle")) return Result("act_floorboard_dismantle", 2.8, .94, "Matched structural dismantling.");
        if (ContainsAny(text, "stalk", "stalking")) return Result("act_creature_stalk", 2.7, .91, "Matched stalking movement.");
        if (ContainsAny(text, "vine", "swing")) return Result("act_vine_swing", 3.2, .93, "Matched swinging movement.");
        if (ContainsAny(text, "tiger", "panther", "creature", "beast", "pounce")) return Result("act_creature_pounce", 2.4, .90, "Matched creature attack.");
        return Result("act_generic_action", 2.2, .75, "Default calibrated action category.");
    }

    private ActionClassifierEstimation Result(string category, double fallback, double confidence, string explanation) =>
        new(category, ActionCameraOverheadLedger.GetOverheadSec(category, fallback), confidence, explanation);

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
