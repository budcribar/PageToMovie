using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

public sealed record MeasuredTimingEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("estimatedDurationSec")] double EstimatedDurationSec,
    [property: JsonPropertyName("actualDurationSec")] double ActualDurationSec,
    [property: JsonPropertyName("deltaSec")] double DeltaSec);

public sealed record CompositeTimingEntry(
    string CameraId,
    string ActionId,
    string ConcurrencyMode,
    double BaseOverheadSec,
    double OverlapRatioGamma);

/// <summary>
/// Empirical ledger of action and camera duration overheads measured from ground-truth video benchmarks.
/// Supports composite dual-key lookups keyed by (Camera ID, Action ID, Concurrency Mode) and the Concurrency Overlap Factor (gamma).
/// Calculates Effective Speech Window = Total Clip Duration - CamOverhead - ((1 - gamma) * ActOverhead).
/// Predictively splits shots when speech capacity is exceeded.
/// </summary>
public sealed class ActionCameraOverheadLedger
{
    private const string CamPushIn = "cam_push_in";
    private const string CamWhipPan = "cam_whip_pan";
    private const string CamTrackingDolly = "cam_tracking_dolly";
    private const string Serial = "serial";
    private const string Concurrent = "concurrent";

    private static readonly Dictionary<string, double> SingleKeyOverheads = new(StringComparer.OrdinalIgnoreCase)
    {
        // Camera Movements
        [CamPushIn] = 1.6,
        [CamWhipPan] = 0.8,
        [CamTrackingDolly] = 2.4,
        ["cam_crane_canopy"] = 2.7,

        // Reactions
        ["react_gasp_shock"] = 1.3,
        ["react_confused_stare"] = 1.7,
        ["react_heart_pounding"] = 1.7,
        ["react_creature_roar"] = 2.1,

        // Physical Actions & Aggression
        ["act_heavy_carry"] = 3.1,
        ["act_weightlifting"] = 2.8,
        ["act_knife_pull"] = 2.0,
        ["act_choke_wall"] = 2.2,
        ["act_stabbing"] = 3.1,
        ["act_running_panic"] = 2.8,
        ["act_pills_sorting"] = 2.3,

        // Psychological Horror (Tell-Tale Heart)
        ["act_creeping_step"] = 2.8,
        ["act_lantern_unshutter"] = 1.9,
        ["act_sudden_shriek"] = 1.4,
        ["act_floorboard_dismantle"] = 2.8,

        // Creature / Adventure (Jungle Book)
        ["act_creature_pounce"] = 2.4,
        ["act_creature_stalk"] = 2.7,
        ["act_vine_swing"] = 3.2,

        // Vehicles & Environment
        ["car_broadside_crash"] = 2.0,
        ["car_muscle_drive"] = 2.3,
        ["car_ferry_ride"] = 3.3,
        ["scene_visitation_room"] = 2.4,

        // Mindfulness & Dreams
        ["act_yoga_pose"] = 2.4,
        ["dream_viking_battle"] = 3.6,
        ["dream_lake_goddess"] = 3.6,

        // Composite Action-Dialogue Beats (Nick & Me)
        ["combo_pills_and_snivel"] = 2.3,
        ["combo_weights_and_taunt"] = 2.8,
        ["combo_knife_and_threat"] = 2.0,
        ["combo_drive_and_talk"] = 2.3,
        ["combo_bar_and_confront"] = 2.4,
        ["combo_yoga_and_explain"] = 2.4,
    };

    private static readonly Dictionary<(string CameraId, string ActionId, string Mode), CompositeTimingEntry> CompositeLedger = new()
    {
        // Documented Examples (§2)
        [(CamPushIn, "act_knife_pull", Serial)] = new CompositeTimingEntry(CamPushIn, "act_knife_pull", Serial, 2.0, 0.0),
        [(CamPushIn, "act_pills_sorting", Concurrent)] = new CompositeTimingEntry(CamPushIn, "act_pills_sorting", Concurrent, 2.3, 0.85),
        
        // Extended Composite Dual-Key Entries (*Nick and Me*, *Tell-Tale Heart*, *Jungle Book*)
        [(CamWhipPan, "act_stabbing", Serial)] = new CompositeTimingEntry(CamWhipPan, "act_stabbing", Serial, 2.8, 0.0),
        [(CamTrackingDolly, "act_running_panic", Concurrent)] = new CompositeTimingEntry(CamTrackingDolly, "act_running_panic", Concurrent, 2.5, 0.80),
        [("cam_crane_canopy", "act_vine_swing", Concurrent)] = new CompositeTimingEntry("cam_crane_canopy", "act_vine_swing", Concurrent, 2.7, 0.75),
        [(CamPushIn, "act_creeping_step", Serial)] = new CompositeTimingEntry(CamPushIn, "act_creeping_step", Serial, 2.4, 0.0),
        [(CamTrackingDolly, "car_muscle_drive", Concurrent)] = new CompositeTimingEntry(CamTrackingDolly, "car_muscle_drive", Concurrent, 2.2, 0.85),
        [(CamPushIn, "act_heavy_carry", Serial)] = new CompositeTimingEntry(CamPushIn, "act_heavy_carry", Serial, 3.0, 0.0),
        [(CamWhipPan, "react_gasp_shock", Serial)] = new CompositeTimingEntry(CamWhipPan, "react_gasp_shock", Serial, 1.2, 0.0),
    };

    private readonly ILogger<ActionCameraOverheadLedger>? _log;

    public ActionCameraOverheadLedger(ILogger<ActionCameraOverheadLedger>? log = null)
    {
        _log = log;
    }

    public double GetOverheadSec(string categoryId, double fallbackSec = 1.8)
    {
        if (SingleKeyOverheads.TryGetValue(categoryId, out var overhead))
            return overhead;
        return fallbackSec;
    }

    /// <summary>
    /// Composite Dual-Key Lookup keyed by (Camera ID, Action ID, Concurrency Mode).
    /// Returns exact calibrated CompositeTimingEntry or falls back to single-key interpolation.
    /// </summary>
    public CompositeTimingEntry GetCompositeEntry(string? cameraId, string? actionId, string? concurrencyMode)
    {
        var cam = string.IsNullOrWhiteSpace(cameraId) ? CamPushIn : cameraId.Trim().ToLowerInvariant();
        var act = string.IsNullOrWhiteSpace(actionId) ? "act_generic_action" : actionId.Trim().ToLowerInvariant();
        var mode = string.IsNullOrWhiteSpace(concurrencyMode) ? Serial : concurrencyMode.Trim().ToLowerInvariant();

        var key = (cam, act, mode);
        if (CompositeLedger.TryGetValue(key, out var entry))
        {
            _log?.LogDebug("[TimingLedger] Composite dual-key HIT for ({Cam}, {Act}, {Mode}) -> Overhead={Overhead}s, Gamma={Gamma}",
                cam, act, mode, entry.BaseOverheadSec, entry.OverlapRatioGamma);
            return entry;
        }

        // Interpolated fallback from single-key empirical overhead dictionary
        double baseOverhead = GetOverheadSec(act, GetOverheadSec(cam, 2.0));
        double gamma = string.Equals(mode, Concurrent, StringComparison.OrdinalIgnoreCase) ? 0.85 : 0.0;

        _log?.LogDebug("[TimingLedger] Composite dual-key MISS for ({Cam}, {Act}, {Mode}) -> Fallback Overhead={Overhead}s, Gamma={Gamma}",
            cam, act, mode, baseOverhead, gamma);

        return new CompositeTimingEntry(cam, act, mode, baseOverhead, gamma);
    }

    /// <summary>
    /// Calculates remaining seconds for speech in a clip taking into account the Concurrency Overlap Factor (gamma).
    /// Effective Speech Window = Total Clip Duration - Camera Overhead - ((1 - gamma) * Action Overhead).
    /// </summary>
    public double CalculateEffectiveSpeechWindowSec(
        double totalClipDurationSec,
        string? cameraCategoryId = null,
        string? actionCategoryId = null,
        double concurrencyFactorGamma = 0.0)
    {
        var mode = concurrencyFactorGamma > 0.0 ? Concurrent : Serial;
        var entry = GetCompositeEntry(cameraCategoryId, actionCategoryId, mode);

        double camOverhead = GetOverheadSec(entry.CameraId, 1.6);
        double actOverhead = entry.BaseOverheadSec;
        double gamma = Math.Max(concurrencyFactorGamma, entry.OverlapRatioGamma);

        double netActionOverhead = (1.0 - gamma) * actOverhead;
        double remaining = totalClipDurationSec - camOverhead - netActionOverhead;
        return Math.Max(0.0, remaining);
    }

    /// <summary>
    /// Returns max allowed words for a clip based on Effective Speech Window and words-per-second rate.
    /// </summary>
    public int CalculateMaxSpeechWords(
        double totalClipDurationSec,
        string? cameraCategoryId = null,
        string? actionCategoryId = null,
        double concurrencyFactorGamma = 0.0,
        double wordsPerSecond = ClipDurationEstimator.DialogueWordsPerSecond)
    {
        double speechWindow = CalculateEffectiveSpeechWindowSec(totalClipDurationSec, cameraCategoryId, actionCategoryId, concurrencyFactorGamma);
        return (int)Math.Floor(speechWindow * wordsPerSecond);
    }

    /// <summary>
    /// True if beat needs auto-splitting into Action Shot + Dialogue Shot.
    /// </summary>
    public bool ExceedsSpeechCapacity(
        int dialogueWordCount,
        double totalClipDurationSec,
        string? cameraCategoryId = null,
        string? actionCategoryId = null,
        double concurrencyFactorGamma = 0.0,
        double wordsPerSecond = ClipDurationEstimator.DialogueWordsPerSecond)
    {
        int maxWords = CalculateMaxSpeechWords(totalClipDurationSec, cameraCategoryId, actionCategoryId, concurrencyFactorGamma, wordsPerSecond);
        return dialogueWordCount > maxWords;
    }
}
