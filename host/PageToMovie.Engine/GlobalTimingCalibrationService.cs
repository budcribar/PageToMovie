using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Connects ClipTimingTelemetryRepository SQLite storage with ActionCameraOverheadLedger.
/// Continuously recalibrates in-memory overheads as new scene cuts are processed and verified.
/// </summary>
public sealed class GlobalTimingCalibrationService
{
    private readonly ClipTimingTelemetryRepository _repo;
    private readonly ILogger<GlobalTimingCalibrationService>? _log;

    public GlobalTimingCalibrationService(
        ClipTimingTelemetryRepository repo,
        ActionCameraOverheadLedger ledger,
        ILogger<GlobalTimingCalibrationService>? log = null)
    {
        _repo = repo;
        _log = log;
    }

    public async Task<TimingCacheStats> GetStatsAsync()
    {
        return await _repo.GetCacheTelemetryStatsAsync().ConfigureAwait(false);
    }

    public async Task<List<TimingTrendPoint>> GetTrendAsync(int maxPoints = 30)
    {
        return await _repo.GetTrendHistoryAsync(maxPoints).ConfigureAwait(false);
    }

    public async Task RecordCutTelemetryAsync(
        string projectId,
        int sceneNumber,
        string videoModelId,
        string videoModelVersion,
        string evaluatorModelId,
        string evaluatorModelVersion,
        string? cameraCategory,
        string? actionCategory,
        int wordCount,
        double estimatedDurationSec,
        double clipDurationSec,
        double measuredCamOverheadSec,
        double measuredActionOverheadSec,
        bool dialogueTruncated)
    {
        var record = new TimingTelemetryRecord(
            Id: Guid.NewGuid().ToString("N"),
            ProjectId: projectId,
            SceneNumber: sceneNumber,
            VideoModelId: videoModelId,
            VideoModelVersion: videoModelVersion,
            EvaluatorModelId: evaluatorModelId,
            EvaluatorModelVersion: evaluatorModelVersion,
            CameraCategory: cameraCategory ?? "",
            ActionCategory: actionCategory ?? "",
            WordCount: wordCount,
            EstimatedDurationSec: estimatedDurationSec > 0 ? estimatedDurationSec : Math.Round(measuredCamOverheadSec + measuredActionOverheadSec + (wordCount / ClipDurationEstimator.DialogueWordsPerSecond), 2),
            ClipDurationSec: clipDurationSec,
            MeasuredCamOverheadSec: measuredCamOverheadSec,
            MeasuredActionOverheadSec: measuredActionOverheadSec,
            DialogueTruncated: dialogueTruncated,
            CreatedAt: DateTime.UtcNow.ToString("o"));

        await _repo.RecordTelemetryAsync(record).ConfigureAwait(false);
        _log?.LogInformation("[GlobalTimingCalibration] Logged timing telemetry for scene {Scene} ({VideoModel} / {EvaluatorModel})", sceneNumber, videoModelId, evaluatorModelId);
    }

    public async Task<int> SeedDefaultBenchmarksAsync()
    {
        var defaultBenchmarks = new List<(string Id, string Category, string Prompt, double EstimatedSec, string Mode, double Gamma)>
        {
            ("cam_push_in", "Camera Movement", "Cinematic slow push-in zoom", 1.8, "serial", 0.0),
            ("react_gasp_shock", "Facial Reaction", "Character gasps in shock", 1.5, "serial", 0.0),
            ("act_heavy_carry", "Physical Action", "Strong man carrying heavy grocery bags", 3.0, "serial", 0.0),
            ("act_knife_pull", "Aggression", "Character pulls out a switchblade", 2.1, "serial", 0.0),
            ("car_broadside_crash", "Vehicle", "Broadside T-bone car crash", 1.8, "serial", 0.0),
            ("act_weightlifting", "Physical Action", "Man doing preacher curls with a 50lb barbell", 2.5, "serial", 0.0),
            ("act_choke_wall", "Aggression", "Grabs by neck, pinning against wall", 2.4, "serial", 0.0),
            ("act_stabbing", "Aggression", "Dramatic stabbing motion in hallway", 3.0, "serial", 0.0),
            ("act_running_panic", "Locomotion", "Character sprinting down city street", 3.0, "serial", 0.0),
            ("act_pills_sorting", "Elderly Care", "Elderly woman sorting pill bottles on counter", 2.6, "serial", 0.0),
            ("car_muscle_drive", "Vehicle", "1980 Pontiac Trans Am revving engine", 2.5, "serial", 0.0),
            ("act_yoga_pose", "Mindfulness", "Person lying on purple mat doing Savasana", 2.5, "serial", 0.0),
            ("act_creeping_step", "Psychological Horror", "Creeping forward step-by-step in pitch darkness", 2.8, "serial", 0.0),
            ("act_creature_pounce", "Creature / Adventure", "Large wild tiger leaping down from tree", 2.4, "serial", 0.0),
            ("combo_pills_and_snivel", "Composite Action + Dialogue", "Sorting pill bottles while sniveling & speaking", 2.6, "concurrent", 0.85),
            ("combo_weights_and_taunt", "Composite Action + Dialogue", "Dropping 50lb barbell then speaking taunt", 2.5, "serial", 0.0),
            ("combo_knife_and_threat", "Composite Action + Dialogue", "Pulling switchblade open then delivering threat", 2.1, "serial", 0.10),
            ("combo_drive_and_talk", "Composite Action + Dialogue", "Driving 1980 Trans Am while shouting dialogue", 2.5, "concurrent", 0.90),
            ("combo_bar_and_confront", "Composite Action + Dialogue", "Pinning someone to wall then yelling in face", 2.4, "serial", 0.0),
            ("combo_yoga_and_explain", "Composite Action + Dialogue", "Lying in corpse pose while explaining Savasana", 2.5, "concurrent", 0.80)
        };

        return await _repo.SeedInitialBenchmarksAsync(defaultBenchmarks).ConfigureAwait(false);
    }
}
