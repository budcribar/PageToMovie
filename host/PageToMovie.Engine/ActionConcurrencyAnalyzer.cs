using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

public sealed record ActionConcurrencyResult(
    string CameraId,
    string ActionId,
    string Mode,
    double OverlapRatioGamma,
    string Reason);

/// <summary>
/// Analyzes Fountain beat action descriptions, camera movements, and parentheticals.
/// Extracts Camera ID, Action ID, Concurrency Mode, and Overlap Ratio Gamma (γ).
/// </summary>
public static class ActionConcurrencyAnalyzer
{
    private static readonly Regex WhipPanRegex = new(@"\b(whip|pan|fast pan)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TrackingDollyRegex = new(@"\b(tracking|dolly|follow|pan alongside|chase)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CraneCanopyRegex = new(@"\b(crane|canopy|overhead|high angle|top down)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex KnifePullRegex = new(@"\b(knife|blade|switchblade|pulls out|unsheathes|clicks open)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PillsSortingRegex = new(@"\b(pills|bottle|sorting|medicine|prescription|counter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex StabbingRegex = new(@"\b(stab|stabbing|lunge|spear|thrusts|plunges)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ChokeWallRegex = new(@"\b(choke|strangle|pin against wall|grabs throat)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex RunningPanicRegex = new(@"\b(running|run|panic|flee|sprints|dashes)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CreepingStepRegex = new(@"\b(creeping|creep|stealth|tiptoe|slow step|sneaks)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex HeavyCarryRegex = new(@"\b(carry|heavy|lifting|weights|deadlift|hoists)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex VineSwingRegex = new(@"\b(vine|swing|canopy swing|jungle)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex MuscleDriveRegex = new(@"\b(drive|driving|car|steering|vehicle|muscle car)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex ConcurrentKeywordsRegex = new(@"\b(while|as he|as she|as they|simultaneously|during|pacing|driving|riding|holding|sorting|walking|eating|drinking)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex SerialKeywordsRegex = new(@"\b(pauses|stops|then|after|first|clicks open|drops|pulls out|launches|slaps|hits|stabs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    public static ActionConcurrencyResult AnalyzeBeat(string? actionDescription, string? parenthetical)
    {
        var text = $"{actionDescription} {parenthetical}".Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ActionConcurrencyResult("cam_push_in", "act_generic_action", "serial", 0.0, "Empty action description; default to serial.");
        }

        // 1. Extract Camera ID
        string cameraId = "cam_push_in";
        if (WhipPanRegex.IsMatch(text)) cameraId = "cam_whip_pan";
        else if (TrackingDollyRegex.IsMatch(text)) cameraId = "cam_tracking_dolly";
        else if (CraneCanopyRegex.IsMatch(text)) cameraId = "cam_crane_canopy";

        // 2. Extract Action ID
        string actionId = "act_generic_action";
        if (KnifePullRegex.IsMatch(text)) actionId = "act_knife_pull";
        else if (PillsSortingRegex.IsMatch(text)) actionId = "act_pills_sorting";
        else if (StabbingRegex.IsMatch(text)) actionId = "act_stabbing";
        else if (ChokeWallRegex.IsMatch(text)) actionId = "act_choke_wall";
        else if (RunningPanicRegex.IsMatch(text)) actionId = "act_running_panic";
        else if (CreepingStepRegex.IsMatch(text)) actionId = "act_creeping_step";
        else if (HeavyCarryRegex.IsMatch(text)) actionId = "act_heavy_carry";
        else if (VineSwingRegex.IsMatch(text)) actionId = "act_vine_swing";
        else if (MuscleDriveRegex.IsMatch(text)) actionId = "car_muscle_drive";

        // 3. Determine Concurrency Mode & Overlap Ratio Gamma
        if (SerialKeywordsRegex.IsMatch(text))
        {
            return new ActionConcurrencyResult(
                CameraId: cameraId,
                ActionId: actionId,
                Mode: "serial",
                OverlapRatioGamma: 0.0,
                Reason: "Detected serial action marker (e.g. 'pauses', 'then', 'clicks open').");
        }

        if (ConcurrentKeywordsRegex.IsMatch(text))
        {
            return new ActionConcurrencyResult(
                CameraId: cameraId,
                ActionId: actionId,
                Mode: "concurrent",
                OverlapRatioGamma: 0.85,
                Reason: "Detected concurrent verb/action marker (e.g. 'while', 'as he', 'pacing').");
        }

        if (!string.IsNullOrWhiteSpace(parenthetical) && parenthetical.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
        {
            return new ActionConcurrencyResult(
                CameraId: cameraId,
                ActionId: actionId,
                Mode: "concurrent",
                OverlapRatioGamma: 0.80,
                Reason: "Short parenthetical beat; default to concurrent overlap.");
        }

        return new ActionConcurrencyResult(
            CameraId: cameraId,
            ActionId: actionId,
            Mode: "serial",
            OverlapRatioGamma: 0.0,
            Reason: "Standard action beat; default to serial execution.");
    }
}
