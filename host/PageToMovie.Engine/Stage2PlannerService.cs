using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Fountain;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Engine.Deterministic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Approved Fountain screenplay → Stage 2 clip blueprint.
/// Reads <c>source/screenplay.fountain</c> directly (in-memory beat model).
/// Covers silent beat duration classes (optional chat), plan_scene, visual prompt packing,
/// wardrobe continuity, duration allocation.
/// </summary>
public sealed class Stage2PlannerService
{
    /// <summary>
    /// Provider-agnostic global negatives — applied at gen time by
    /// <see cref="ClipVideoPromptBuilder"/>, not baked into every blueprint row.
    /// </summary>
    public const string GlobalNegativeDefault =
        "no legible text, no watermarks, no logos, no extra limbs, " +
        "blur/obscure environmental signage or screens, no name tags, no name badges, " +
        "no embroidered names, no lower thirds, no personal names on clothing or props";

    // Duration floors/caps live in ClipDurationEstimator (dialogue-aware, cost-sensitive)
    private const int GrokMinClip = ClipDurationEstimator.MinSeconds;
    private const int GrokMaxClip = ClipDurationEstimator.MaxSeconds;
    private const int GrokAbsMax = ClipDurationEstimator.AbsMaxSeconds;
    private const int GrokDefault = 6;
    private const int GrokSceneMin = 6;
    // No design-time length budget — send full visual prompts.
    // If the video API rejects for length, GrokVideoClient shortens and retries.

    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectStore _projects;
    private readonly ILogger<Stage2PlannerService> _log;
    private readonly SilentBeatActionClassifier? _silentBeatClassifier;
    private readonly AmbientSfxClassifier? _ambientSfxClassifier;
    private readonly OnScreenCastClassifier? _onScreenCastClassifier;
    private readonly ExtendCutClassifier? _extendCutClassifier;
    private readonly SpeciesKindClassifier? _speciesKindClassifier;
    private readonly ShotPlanRefiningClassifier? _shotPlanRefiner;
    private readonly BeatPacingClassifier? _beatPacingClassifier;
    private readonly CinematicLightingClassifier? _lightingClassifier;
    private readonly CameraDirectorClassifier? _cameraClassifier;
    private readonly NegativePromptClassifier? _negativeClassifier;
    private readonly WardrobeContinuityClassifier? _wardrobeClassifier;
    private readonly CharacterEmotionArcClassifier? _emotionClassifier;
    private readonly SoundDesignComposerClassifier? _soundComposerClassifier;
    private readonly DepthOfFieldClassifier? _dofClassifier;
    private readonly ColorPaletteGradingClassifier? _colorGradingClassifier;
    private readonly GenerationErrorLogger? _errorLog;

    public Stage2PlannerService(
        ProjectStore projects,
        ILogger<Stage2PlannerService> log,
        SilentBeatActionClassifier? silentBeatClassifier = null,
        AmbientSfxClassifier? ambientSfxClassifier = null,
        OnScreenCastClassifier? onScreenCastClassifier = null,
        ExtendCutClassifier? extendCutClassifier = null,
        SpeciesKindClassifier? speciesKindClassifier = null,
        ShotPlanRefiningClassifier? shotPlanRefiner = null,
        BeatPacingClassifier? beatPacingClassifier = null,
        CinematicLightingClassifier? lightingClassifier = null,
        CameraDirectorClassifier? cameraClassifier = null,
        NegativePromptClassifier? negativeClassifier = null,
        WardrobeContinuityClassifier? wardrobeClassifier = null,
        CharacterEmotionArcClassifier? emotionClassifier = null,
        SoundDesignComposerClassifier? soundComposerClassifier = null,
        DepthOfFieldClassifier? dofClassifier = null,
        ColorPaletteGradingClassifier? colorGradingClassifier = null,
        GenerationErrorLogger? errorLog = null)
    {
        _projects = projects;
        _log = log;
        _silentBeatClassifier = silentBeatClassifier;
        _ambientSfxClassifier = ambientSfxClassifier;
        _onScreenCastClassifier = onScreenCastClassifier;
        _extendCutClassifier = extendCutClassifier;
        _speciesKindClassifier = speciesKindClassifier;
        _shotPlanRefiner = shotPlanRefiner;
        _beatPacingClassifier = beatPacingClassifier;
        _lightingClassifier = lightingClassifier;
        _cameraClassifier = cameraClassifier;
        _negativeClassifier = negativeClassifier;
        _wardrobeClassifier = wardrobeClassifier;
        _emotionClassifier = emotionClassifier;
        _soundComposerClassifier = soundComposerClassifier;
        _dofClassifier = dofClassifier;
        _colorGradingClassifier = colorGradingClassifier;
        _errorLog = errorLog;
    }

    /// <summary>
    /// Project video model from Settings (<c>model_name</c>) — required for duration bounds.
    /// </summary>
    private async Task<string> ResolveVideoModelIdAsync(string projectId, CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return ProjectModelSelection.RequireVideo(cfg, "Shot plan / Stage 2");
    }

    private async Task<string> ResolvePlanningModelIdAsync(string projectId, CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return ProjectModelSelection.RequirePlanning(cfg, "Shot plan classifiers");
    }

    public async Task<Stage2PlanResult> PlanAsync(
        string projectId,
        string resolution = "720p",
        string scenes = "all",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        using var operationTrace = ModelOperationTraceScope.Begin();
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        // Clip-duration bounds for whichever video model this project is actually configured to
        // generate with — planning must not assume Grok's limits when a different model is selected.
        var videoModelId = await ResolveVideoModelIdAsync(projectId, ct).ConfigureAwait(false);
        var planningModel = await ResolvePlanningModelIdAsync(projectId, ct).ConfigureAwait(false);
        var (durMinSeconds, durMaxSeconds, durAbsMaxSeconds) = ClipDurationEstimator.ResolveBoundsForModel(videoModelId);
        // Tighter cap for clips that will extend from the previous one (some providers, e.g. Grok,
        // allow a longer fresh clip than the "new portion" of a reference/continue call) — used so
        // beat-coalescing plans against the RIGHT ceiling for whichever mode a clip ends up in,
        // instead of always using the looser fresh-generation max.
        var durExtensionMaxSeconds = ClipDurationEstimator.ResolveExtensionMaxForModel(videoModelId, durMaxSeconds);
        // How many characters this video model can render speaking in one clip (catalog-driven).
        // 1 → keep one speaker per clip (shot-reverse-shot); >=2 → allow two-hander coalescing.
        var maxSpeakersPerClip = ResolveMaxSpeakersPerClip(videoModelId);

        // Fountain is the only screenplay source of truth.
        ScreenplayService.EnsureCanonicalDraft(_projects, projectId);
        var fountainPath = ScreenplayService.GetDraftPath(_projects, projectId);
        if (!File.Exists(fountainPath))
            throw new InvalidOperationException(
                "No screenplay draft. Create and approve a Fountain screenplay first.");

        var screenplay = ScreenplayService.Get(_projects, projectId);
        if (!screenplay.Status.Signed && screenplay.Status.DraftExists)
            throw new InvalidOperationException(
                "Approve the screenplay before building a shot plan (draft has unapproved changes).");

        onProgress?.Invoke($"Loading screenplay: {Path.GetFileName(fountainPath)}");
        var stage1 = ScreenplayService.BuildModelFromFountainText(screenplay.Text, durMinSeconds, durMaxSeconds, durAbsMaxSeconds);
        var sourceLabel = Path.GetFileName(fountainPath);

        // Overlay plate/voice edits from cast_seeds.json when present
        MergeCastSeedsOverlay(_projects, projectId, stage1);

        // AI enrichments (each: chat preferred → retry → heuristic fallback).
        // All use the Settings Script & planning model — no host option invent defaults.
        SilentBeatClassifyResult? classifyMeta = null;
        var enrichMeta = new Dictionary<string, object?>();
        if (_silentBeatClassifier is not null)
        {
            classifyMeta = await _silentBeatClassifier
                .ClassifyStage1Async(stage1, onProgress, ct, overrideModel: planningModel)
                .ConfigureAwait(false);
            enrichMeta["silent_beat"] = classifyMeta.ToMetaDict();
        }
        if (_ambientSfxClassifier is not null)
        {
            var amb = await _ambientSfxClassifier.ClassifyStage1Async(stage1, onProgress, ct, overrideModel: planningModel)
                .ConfigureAwait(false);
            enrichMeta["ambient_sfx"] = amb.ToMetaDict();
        }
        if (_speciesKindClassifier is not null)
        {
            var sp = await _speciesKindClassifier.ClassifyStage1Async(stage1, onProgress, ct, model: planningModel)
                .ConfigureAwait(false);
            enrichMeta["species_kind"] = sp.ToMetaDict();
        }
        if (_onScreenCastClassifier is not null)
        {
            var osc = await _onScreenCastClassifier.ClassifyStage1Async(stage1, onProgress, ct, model: planningModel)
                .ConfigureAwait(false);
            enrichMeta["onscreen_cast"] = osc.ToMetaDict();
        }
        if (_extendCutClassifier is not null)
        {
            var ext = await _extendCutClassifier.ClassifyStage1Async(stage1, onProgress, ct, model: planningModel)
                .ConfigureAwait(false);
            enrichMeta["extend_hardcut"] = ext.ToMetaDict();
        }

        var gpv = GetDict(stage1, "global_production_variables");
        var locSeeds = GetDict(gpv, "location_seed_tokens");
        var charSeeds = GetDict(gpv, "character_seed_tokens");
        NormalizeCharPlaceholders(charSeeds);

        var want = ParseSceneRange(scenes);
        var scenesIn = GetScenes(stage1)
            .Where(s =>
            {
                if (want is null) return true;
                var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
                return want.Contains(n);
            })
            .ToList();

        if (scenesIn.Count == 0)
            throw new InvalidOperationException("Screenplay has no scenes to plan.");

        onProgress?.Invoke($"Planning {scenesIn.Count} scene(s) @ {resolution}…");
        var styleLock = CoerceString(gpv.TryGetValue("render_style_lock", out var rsl) ? rsl : null);
        var planned = new List<Dictionary<string, object?>>();
        foreach (var s in scenesIn)
        {
            ct.ThrowIfCancellationRequested();
            var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
            onProgress?.Invoke($"  Scene {sn}…");
            // All 9 read only from `s` / their own independently-built sceneBeats clone and
            // return a fresh result — none mutate shared state — so they run concurrently
            // instead of one round-trip at a time. Each classifier's underlying IChatClient
            // sets auth per-request now (not on shared HttpClient.DefaultRequestHeaders), so
            // this fan-out is safe there too.
            var pacingTask = _beatPacingClassifier is not null
                ? _beatPacingClassifier.ClassifyScenePacingAsync(s, BuildSceneBeats(s, durMinSeconds, durMaxSeconds, durAbsMaxSeconds), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, int>?>(null);
            var lightingTask = _lightingClassifier is not null
                ? _lightingClassifier.ClassifySceneLightingAsync(s, onProgress, ct, model: planningModel)
                : Task.FromResult<string?>(null);
            var cameraTask = _cameraClassifier is not null
                ? _cameraClassifier.ClassifySceneCameraAsync(s, BuildSceneBeats(s, durMinSeconds, durMaxSeconds, durAbsMaxSeconds), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, CameraDirective>?>(null);
            var negativeTask = _negativeClassifier is not null
                ? _negativeClassifier.ClassifySceneNegativeAsync(s, onProgress, ct, model: planningModel)
                : Task.FromResult<string?>(null);
            var wardrobeTask = _wardrobeClassifier is not null
                ? _wardrobeClassifier.ClassifySceneWardrobeAsync(s, UnionCharactersOnScreen(s), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, string>?>(null);
            var emotionTask = _emotionClassifier is not null
                ? _emotionClassifier.ClassifySceneEmotionAsync(s, BuildSceneBeats(s, durMinSeconds, durMaxSeconds, durAbsMaxSeconds), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, EmotionDirective>?>(null);
            var soundTask = _soundComposerClassifier is not null
                ? _soundComposerClassifier.ClassifySceneSoundDesignAsync(s, BuildSceneBeats(s, durMinSeconds, durMaxSeconds, durAbsMaxSeconds), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, SoundDesignDirective>?>(null);
            var dofTask = _dofClassifier is not null
                ? _dofClassifier.ClassifySceneDepthOfFieldAsync(s, BuildSceneBeats(s, durMinSeconds, durMaxSeconds, durAbsMaxSeconds), onProgress, ct, model: planningModel)
                : Task.FromResult<Dictionary<string, DepthOfFieldDirective>?>(null);
            var colorTask = _colorGradingClassifier is not null
                ? _colorGradingClassifier.ClassifySceneColorGradingAsync(s, onProgress, ct, model: planningModel)
                : Task.FromResult<ColorGradingDirective?>(null);

            await Task.WhenAll(
                pacingTask, lightingTask, cameraTask, negativeTask, wardrobeTask,
                emotionTask, soundTask, dofTask, colorTask).ConfigureAwait(false);

            var aiPacing = pacingTask.Result;
            var aiLighting = lightingTask.Result;
            var aiCamera = cameraTask.Result;
            var aiNegative = negativeTask.Result;
            var aiWardrobe = wardrobeTask.Result;
            var aiEmotion = emotionTask.Result;
            var aiSound = soundTask.Result;
            var aiDof = dofTask.Result;
            var aiColor = colorTask.Result;
            var plannedScene = PlanScene(s, resolution, locSeeds, charSeeds, styleLock, aiPacing, aiLighting, aiCamera, aiNegative, aiWardrobe, aiEmotion, aiSound, aiDof, aiColor, durMinSeconds, durMaxSeconds, durAbsMaxSeconds, durExtensionMaxSeconds, maxSpeakersPerClip);
            // Skip transition-only phantoms (e.g. FADE IN before first heading)
            if (plannedScene is null)
            {
                onProgress?.Invoke($"  Scene {sn}: skipped (no filmable content)");
                continue;
            }
            if (_shotPlanRefiner is not null)
            {
                await _shotPlanRefiner.RefinePlannedSceneAsync(plannedScene, onProgress, ct, model: planningModel).ConfigureAwait(false);
            }
            planned.Add(plannedScene);
        }

        if (planned.Count == 0)
            throw new InvalidOperationException("Screenplay has no filmable scenes to plan.");

        var outPath = await _projects.FindBlueprintPathAsync(projectId, ct).ConfigureAwait(false)
            ?? Path.Combine(projectDir, "blueprint.clips.grok.json");
        if (File.Exists(outPath))
        {
            var bak = outPath + $".bak_pre_stage2_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(outPath, bak, overwrite: true);
            onProgress?.Invoke($"Backed up blueprint → {Path.GetFileName(bak)}");
        }

        // Single source of truth for the auto-inserted credits scene's content (title + author + creator site).
        var creditsVisualPrompt = _projects.BuildCreditsVisualPrompt(projectId);

        Dictionary<string, object?> plan;
        if (want is not null && File.Exists(outPath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false);
                var existing = GrokChatClient.ParseJsonObject(existingText);
                plan = MergePlannedScenes(existing, planned, stage1, gpv, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
                onProgress?.Invoke("Merged planned scenes into existing blueprint");
            }
            catch
            {
                plan = BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
            }
        }
        else
        {
            plan = BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
        }

        // Heal: a character on-screen in a clip is by definition on-screen in the scene. Union each
        // clip's cast into its scene cast so a model that under-listed the scene cast (e.g. omitting
        // the lead) does not hard-fail the clip⊆scene validation below.
        HealSceneCastFromClips(plan);

        // Middle-layer guard: every spoken line in the approved screenplay must survive planning into
        // some clip's audio_payload. Record coverage in stage2_meta (always), surface any drop as a
        // plan issue, and log it to generation_errors so we can trace which transform silenced it.
        var coverage = Stage2DialogueCoverage.Verify(stage1, plan);
        GetDict(plan, "stage2_meta")["dialogue_coverage"] = coverage.Meta;
        if (coverage.HasGaps)
        {
            onProgress?.Invoke(
                $"Dialogue coverage: {coverage.CoveredLines}/{coverage.ExpectedLines} screenplay lines reach a clip " +
                $"({coverage.Gaps.Count} not spoken in the shot plan).");
            await LogDialogueCoverageGapsAsync(coverage, videoModelId, planningModel, ct).ConfigureAwait(false);
        }

        var planIssues = StructuredOperationArtifacts.RequireJsonProperties(plan, "stage2_meta", "scenes")
            .Concat(Stage2AggregateValidator.Validate(plan))
            .Concat(coverage.Issues)
            .ToArray();
        var classifierProvenance = Stage2AggregateValidator.BuildClassifierProvenance(enrichMeta);
        await StructuredOperationArtifacts.WriteAsync(
            _projects.GetProjectDir(projectId), "stage2_shot_plan", videoModelId,
            new { projectId, sourceLabel, resolution, scenes }, plan, planIssues, ct).ConfigureAwait(false);
        await Stage2AggregateValidator.WriteManifestAsync(
            _projects.GetProjectDir(projectId), classifierProvenance, operationTrace.Snapshot(), planIssues, ct).ConfigureAwait(false);
        if (planIssues.Any(i => i.Severity == ModelValidationSeverity.Error))
            throw new InvalidOperationException(string.Join(" ", planIssues.Select(i => i.Message)));

        // Fail loud at the source if the plan has duplicate clip numbers in a scene — that doubles the
        // scene downstream (the stitch concatenates one file per veo_clips entry). Throwing here catches
        // the bug during generation, before any video spend, and never touches an already-saved movie.
        var planJson = JsonSerializer.Serialize(plan, JsonWrite);
        using (var planDoc = System.Text.Json.JsonDocument.Parse(planJson))
        {
            if (BlueprintClipValidation.DescribeDuplicates(planDoc.RootElement) is { } dupDesc)
                throw new InvalidOperationException(
                    "Shot plan has duplicate clip numbers (each duplicate would double its scene): " + dupDesc);
        }

        await File.WriteAllTextAsync(outPath, planJson + "\n", ct).ConfigureAwait(false);
        var meta = GetDict(plan, "stage2_meta");
        var totalClips = ToInt(meta.TryGetValue("total_clips", out var tc) ? tc : 0);
        var sceneCount = GetList(plan, "scenes").Count;
        var totalDur = ToInt(meta.TryGetValue("total_duration_seconds", out var td) ? td : 0);
        onProgress?.Invoke(
            $"Wrote {Path.GetFileName(outPath)} · {sceneCount} scenes · {totalClips} clips");

        _projects.TriggerAutoGitCommit(projectId, "Stage: Stage 2 blueprint written");

        return new Stage2PlanResult
        {
            Ok = true,
            OutPath = outPath,
            SceneCount = sceneCount,
            ClipCount = totalClips,
            DurationSeconds = totalDur,
        };
    }

    /// <summary>
    /// Learning-loop signal for the dialogue-coverage gate: one <c>structural_gate_failure</c> row per
    /// plan carrying every screenplay line that never reached a clip (as <c>s{scene}:{beat}</c> ids),
    /// so the offending Stage-2 transform can be traced. Never throws — same swallow contract as the
    /// classifiers' coverage logging.
    /// </summary>
    private async Task LogDialogueCoverageGapsAsync(
        Stage2DialogueCoverage.Report coverage, string videoModelId, string planningModel, CancellationToken ct)
    {
        if (_errorLog is null || !coverage.HasGaps) return;
        var missingIds = coverage.Gaps
            .Select(g => $"s{g.Scene}:{(string.IsNullOrWhiteSpace(g.BeatId) ? "?" : g.BeatId)}")
            .Take(50)
            .ToList();
        var preview = string.Join(" | ", coverage.Gaps.Take(5)
            .Select(g => $"scene {g.Scene} [{g.Diagnosis}] \"{g.Dialogue}\""));
        await _errorLog.LogAsync(new GenerationErrorRecord
        {
            Stage = "stage2_dialogue_coverage",
            // Coverage is shaped by the planning-model classifiers (silent-beat, coalesce, etc.).
            Model = planningModel,
            ErrorType = "structural_gate_failure",
            ErrorMessage =
                $"{coverage.Gaps.Count} screenplay line(s) not spoken in the shot plan " +
                $"({coverage.CoveredLines}/{coverage.ExpectedLines} covered); video_model={videoModelId}; {preview}",
            RequestedCount = coverage.ExpectedLines,
            ReturnedCount = coverage.CoveredLines,
            MissingIds = missingIds,
            Resolved = false,
        }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> BuildFullPlan(
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        List<Dictionary<string, object?>> planned,
        string sourceLabel,
        string resolution,
        string scenesFilter,
        SilentBeatClassifyResult? classifyMeta,
        Dictionary<string, object?>? enrichMeta,
        string? creditsVisualPrompt = null)
    {
        EnsureEndCreditsScene(planned, creditsVisualPrompt);
        return new()
        {
            ["schema_version"] = "stage2.v1",
            ["movie_title"] = stage1.TryGetValue("movie_title", out var mt) ? mt : null,
            ["source_book_title"] = stage1.TryGetValue("source_book_title", out var sbt) ? sbt : null,
            ["video_provider_profile"] = ResolveVideoProviderProfile(stage1),
            ["global_production_variables"] = gpv,
            ["scenes"] = planned.Cast<object?>().ToList(),
            ["stage2_meta"] = MakeMeta(stage1, planned, sourceLabel, resolution, scenesFilter, classifyMeta, enrichMeta),
        };
    }

    private static Dictionary<string, object?> MergePlannedScenes(
        Dictionary<string, object?> existing,
        List<Dictionary<string, object?>> planned,
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        string sourceLabel,
        string resolution,
        string scenesFilter,
        SilentBeatClassifyResult? classifyMeta,
        Dictionary<string, object?>? enrichMeta,
        string? creditsVisualPrompt = null)
    {
        var byN = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var s in GetList(existing, "scenes").OfType<Dictionary<string, object?>>())
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        foreach (var s in planned)
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        var all = byN.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        EnsureEndCreditsScene(all, creditsVisualPrompt);
        existing["schema_version"] = "stage2.v1";
        existing["movie_title"] = stage1.TryGetValue("movie_title", out var mt) ? mt
            : existing.TryGetValue("movie_title", out var emt) ? emt : null;
        existing["source_book_title"] = stage1.TryGetValue("source_book_title", out var sbt) ? sbt
            : existing.TryGetValue("source_book_title", out var esbt) ? esbt : null;
        existing["video_provider_profile"] = ResolveVideoProviderProfile(stage1);
        existing["global_production_variables"] = gpv;
        existing["scenes"] = all.Cast<object?>().ToList();
        existing["stage2_meta"] = MakeMeta(stage1, all, sourceLabel, resolution, scenesFilter, classifyMeta, enrichMeta);
        return existing;
    }

    public static void EnsureEndCreditsScene(List<Dictionary<string, object?>> scenes, string? creditsVisualPrompt = null)
    {
        if (scenes == null || scenes.Count == 0) return;

        // Dedupe via the single credits predicate (ProjectStore.IsCreditsScene) so a re-plan never
        // appends a second credits card — including when the existing credits scene is only marked by
        // its clip-level is_credits flag (older auto-inserts) rather than a heading/setting.
        if (scenes.Any(ProjectStore.IsCreditsScene))
            return;

        var maxSn = scenes.Select(s => ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0)).DefaultIfEmpty(0).Max();
        var creditsSceneNumber = maxSn + 1;

        // Content comes from the single credits-card builder (ProjectStore.BuildCreditsVisualPrompt) so this
        // auto-inserted scene matches a manual re-add; a plain fallback keeps pure-planning callers/tests working.
        var visualPrompt = string.IsNullOrWhiteSpace(creditsVisualPrompt)
            ? "Elegant cinematic end-credits title card, deep matte-black background with fine film grain, "
              + "centered high-contrast typography, tasteful theatrical end-title look, soft fade to black."
            : creditsVisualPrompt.Trim();

        // Use the same clip shape as a normal (editable) clip — clip_number + audio_payload — so the Scenes
        // editor can open and edit the credits card. (It previously used clip_index with no audio_payload,
        // which the editor keys off clip_number couldn't load: the scene showed "no details / can't edit".)
        var creditsClip = new Dictionary<string, object?>
        {
            ["clip_number"] = 1,
            ["clip_index"] = 1,
            ["timestamp"] = "",
            ["veo_continuation_source"] = "none",
            ["primary_subject"] = "End Credits Title Card",
            ["characters_on_screen"] = new List<object?>(),
            ["focus_keys"] = new List<object?>(),
            ["action_summary"] = "Scrolling film end credits and attribution title card.",
            ["duration_seconds"] = 6,
            ["is_credits"] = true,
            ["visual_prompt"] = visualPrompt,
            ["audio_payload"] = new Dictionary<string, object?>
            {
                ["delivery"] = "none",
                ["speaker"] = "",
                ["dialogue"] = "",
            },
        };

        var creditsScene = new Dictionary<string, object?>
        {
            ["scene_number"] = creditsSceneNumber,
            ["scene_heading"] = "FADE OUT. END CREDITS",
            ["is_credits"] = true,
            ["total_estimated_duration_seconds"] = 6,
            ["veo_clips"] = new List<object?> { creditsClip },
        };

        scenes.Add(creditsScene);
    }

    private static Dictionary<string, object?> MakeMeta(
        Dictionary<string, object?> stage1,
        List<Dictionary<string, object?>> planned,
        string sourceLabel,
        string resolution,
        string scenesFilter,
        SilentBeatClassifyResult? classifyMeta,
        Dictionary<string, object?>? enrichMeta)
    {
        var meta = new Dictionary<string, object?>
        {
            ["source_screenplay"] = sourceLabel,
            ["source_stage1"] = sourceLabel,
            ["resolution"] = resolution,
            ["scene_filter"] = scenesFilter,
            ["planner"] = "Stage2PlannerService (C# Fountain)",
            ["prompt_truncates"] = false,
            ["prompt_length_policy"] = "full_then_api_retry_shorten",
            ["screenplay_fingerprint"] = Stage1Fingerprint(stage1),
            ["stage1_fingerprint"] = Stage1Fingerprint(stage1),
            ["planned_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["total_duration_seconds"] = planned.Sum(s =>
                ToInt(s.TryGetValue("total_estimated_duration_seconds", out var d) ? d : 0)),
            ["total_clips"] = planned.Sum(s => GetList(s, "veo_clips").Count),
        };
        if (classifyMeta is not null)
            meta["silent_beat_classify"] = classifyMeta.ToMetaDict();
        if (enrichMeta is { Count: > 0 })
            meta["ai_enrichments"] = enrichMeta;
        return meta;
    }

    /// <summary>
    /// Overlay design_reference_images / voice fields from source/cast_seeds.json onto the
    /// in-memory model derived from Fountain.
    /// </summary>
    private static void MergeCastSeedsOverlay(
        ProjectStore projects,
        string projectId,
        Dictionary<string, object?> stage1)
    {
        var path = ScreenplayService.GetCastSeedsPath(projects, projectId);
        if (!File.Exists(path))
            return;
        try
        {
            var overlay = GrokChatClient.ParseJsonObject(File.ReadAllText(path));
            // Shapes: { character_seed_tokens } or { global_production_variables.character_seed_tokens }
            var overlaySeeds = GetDict(overlay, "character_seed_tokens");
            if (overlaySeeds.Count == 0)
                overlaySeeds = GetDict(GetDict(overlay, "global_production_variables"), "character_seed_tokens");
            if (overlaySeeds.Count == 0)
                return;

            var gpv = GetDict(stage1, "global_production_variables");
            var seeds = GetDict(gpv, "character_seed_tokens");
            foreach (var (key, val) in overlaySeeds)
            {
                if (val is not Dictionary<string, object?> ov)
                    continue;
                var norm = NormalizeCharacterKey(key);
                var matchKey = seeds.Keys.FirstOrDefault(k => NormalizeCharacterKey(k) == norm) ?? key;

                if (!seeds.TryGetValue(matchKey, out var existing) || existing is not Dictionary<string, object?> cur)
                {
                    seeds[matchKey] = ov;
                }
                else
                {
                    foreach (var (fk, fv) in ov)
                        cur[fk] = fv;
                    seeds[matchKey] = cur;
                }
                seeds[key] = seeds[matchKey];
            }
            gpv["character_seed_tokens"] = seeds;
            stage1["global_production_variables"] = gpv;
        }
        catch
        {
            /* non-fatal */
        }
    }

    public static string NormalizeCharacterKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var s = key.Trim();
        if (s.StartsWith("Character_", StringComparison.OrdinalIgnoreCase))
            s = s["Character_".Length..];
        if (s.StartsWith("The_", StringComparison.OrdinalIgnoreCase))
            s = s["The_".Length..];
        s = s.Replace("_", "");
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Build one scene’s clip plan. Returns null when the scene has nothing filmable
    /// (transition-only / phantom unspecified), so callers can omit it.
    /// </summary>
    private static Dictionary<string, object?>? PlanScene(
        Dictionary<string, object?> scene,
        string resolution,
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        string? styleLock,
        Dictionary<string, int>? aiPacing = null,
        string? aiLighting = null,
        Dictionary<string, CameraDirective>? aiCamera = null,
        string? aiNegative = null,
        Dictionary<string, string>? aiWardrobe = null,
        Dictionary<string, EmotionDirective>? aiEmotion = null,
        Dictionary<string, SoundDesignDirective>? aiSound = null,
        Dictionary<string, DepthOfFieldDirective>? aiDof = null,
        ColorGradingDirective? aiColor = null,
        int minSeconds = ClipDurationEstimator.MinSeconds,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int absMaxSeconds = ClipDurationEstimator.AbsMaxSeconds,
        int? extensionMaxSeconds = null,
        int maxSpeakersPerClip = 1)
    {
        var effectiveExtensionMax = extensionMaxSeconds ?? maxSeconds;
        var sceneInput = new Dictionary<string, object?>(scene);
        if (!string.IsNullOrWhiteSpace(aiLighting))
        {
            sceneInput["lighting_continuity_token"] = aiLighting;
        }
        var beats = GetList(sceneInput, "story_beats").OfType<Dictionary<string, object?>>()
            .Where(b => !IsNoopTransitionBeat(b))
            .ToList();
        // Moved ahead of coalescing (was computed after) — PrecomputeExtendsFromPrevious needs the
        // same location fallback chain the final per-clip loop uses, and this is scene-only data
        // that doesn't depend on the beat list.
        var lids = GetList(scene, "location_ids").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        var primary = CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null)
                      ?? (lids.Count > 0 ? lids[0] : null);

        // Idempotent: monologues already split at fountain import stay; legacy long cues expand here
        beats = ClipDurationEstimator.ExpandLongDialogueBeats(beats, modelMaxSeconds: maxSeconds);
        beats = CoalesceSilentPreludeBeats(beats);
        // Recomputed fresh before each coalescing pass (not carried through) since the beat list's
        // indices shift as beats get merged — cheap, pure local computation either way. See
        // PrecomputeExtendsFromPrevious's doc comment for why using each merge group's first beat
        // is equivalent to the final per-clip ForceNone decision.
        beats = CoalesceShortMonologueBeats(
            beats, maxSeconds, effectiveExtensionMax, PrecomputeExtendsFromPrevious(beats, primary, lids));
        // Two-hander coalescing only when the video model can render >=2 speakers in one clip.
        // At 1 (e.g. Grok today) each dialogue beat stays its own clip — one speaker per clip,
        // shot-reverse-shot, which gives the cleanest lip-sync and avoids face morphing.
        beats = ApplyCrossSpeakerCoalescing(
            beats, maxSpeakersPerClip, maxSeconds, effectiveExtensionMax,
            PrecomputeExtendsFromPrevious(beats, primary, lids));
        var cast = UnionCharactersOnScreen(scene);

        // Entire scene was only FADE IN / CUT TO — omit (no empty clip)
        var setting = CoerceString(scene.TryGetValue("setting", out var set) ? set : null) ?? "";
        if (beats.Count == 0 &&
            setting.Contains("UNSPECIFIED", StringComparison.OrdinalIgnoreCase))
            return null;

        if (beats.Count == 0)
        {
            return BaseSceneShell(sceneInput, lids, primary, cast, Math.Max(minSeconds, GrokSceneMin), new List<object?>(), new List<object?>());
        }

        // Prefer per-beat dialogue/action estimates over padding every clip to fill a scene budget
        var target = ToInt(sceneInput.TryGetValue("duration_target_seconds", out var dt) ? dt : 0);
        var durs = ClipDurationEstimator.AllocateForBeats(
            beats,
            sceneTargetSeconds: target > 0 ? target : null,
            minSeconds: minSeconds,
            maxSeconds: maxSeconds,
            absMaxSeconds: absMaxSeconds);

        if (aiPacing is not null && aiPacing.Count > 0)
        {
            for (var i = 0; i < beats.Count; i++)
            {
                var bid = CoerceString(beats[i].TryGetValue("beat_id", out var bval) ? bval : null) ?? $"b{i + 1}";
                if (aiPacing.TryGetValue(bid, out var customDur))
                {
                    durs[i] = customDur;
                }
            }
        }
        var total = durs.Sum();

        var sceneWork = new Dictionary<string, object?>(sceneInput)
        {
            ["characters_on_screen"] = cast.Cast<object?>().ToList(),
        };
        if (!string.IsNullOrWhiteSpace(styleLock))
            sceneWork["render_style_lock"] = styleLock;

        var wardrobe = InitWardrobeState(cast, charSeeds, scene);
        if (aiWardrobe is not null && aiWardrobe.Count > 0)
        {
            foreach (var (k, v) in aiWardrobe)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    wardrobe[k] = new List<string> { v };
                }
            }
        }
        var clips = new List<object?>();
        var beatMap = new List<object?>();
        var t = 0;
        string? prevLid = null;
        Dictionary<string, object?>? prevBeat = null;

        int monologueStep = 0;
        string? activeSpeaker = null;

        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var dur = durs[i];
            var lid = CoerceString(beat.TryGetValue("location_id", out var bl) ? bl : null)
                      ?? primary ?? (lids.Count > 0 ? lids[0] : null);
            var cont = ForceNone(beat, i, prevBeat, prevLid, lid) ? "none" : "extend_previous";
            if (string.Equals(CoerceString(beat.TryGetValue("action_class", out var ac) ? ac : null),
                    "big_action", StringComparison.OrdinalIgnoreCase))
                cont = "none";
            if (prevLid is not null && lid is not null && prevLid != lid)
                cont = "none";

            var clipCast = ClipCastTokens(sceneWork, beat, charSeeds);
            var ps = CoerceString(beat.TryGetValue("primary_subject", out var psv) ? psv : null) ?? "";
            if (ps.StartsWith("Character_", StringComparison.Ordinal) && !clipCast.Contains(ps))
                clipCast.Insert(0, ps);

            UpdateWardrobeFromBeat(wardrobe, beat, clipCast);

            // Track continuous monologue step
            var dlg = CoerceString(beat.TryGetValue("dialogue", out var dv) ? dv : null);
            var spk = CoerceString(beat.TryGetValue("speaker", out var sv) ? sv : null);
            if (!string.IsNullOrWhiteSpace(dlg) && !string.IsNullOrWhiteSpace(spk))
            {
                if (string.Equals(activeSpeaker, spk, StringComparison.OrdinalIgnoreCase))
                {
                    monologueStep++;
                }
                else
                {
                    activeSpeaker = spk;
                    monologueStep = 0;
                }
            }
            else
            {
                activeSpeaker = null;
                monologueStep = 0;
            }

            // Continuity + resolution/fps are owned by ClipVideoPromptBuilder at gen time —
            // keep blueprint visual_prompt declarative (action/style only).
            var vp = BuildVisualPrompt(beat, sceneWork, locSeeds, charSeeds, wardrobe, i);

            // AI cinematic lighting/mood token (locks lighting style across the scene's shots) —
            // previously computed by CinematicLightingClassifier and stored on the scene as
            // lighting_continuity_token, but never appended to any clip's visual_prompt, so it
            // never reached the actual video-generation call. Appended here the same way camera/
            // performance/optics/color directives already are below.
            if (!string.IsNullOrWhiteSpace(aiLighting))
                vp = $"{vp} {PromptTags.Wrap("Lighting", PromptTags.SanitizeValue(aiLighting))}";

            // Story-specific negatives only; provider global negatives applied at gen time.
            var neg = BuildStoryNegativePrompt(beat, wardrobe, clipCast);
            if (!string.IsNullOrWhiteSpace(aiNegative))
            {
                neg = string.IsNullOrWhiteSpace(neg) ? aiNegative : $"{neg}, {aiNegative}";
            }

            var beatIdStr = CoerceString(beat.TryGetValue("beat_id", out var bi) ? bi : null) ?? $"b{i + 1}";
            var sourceBeatIds = PageToMovie.Core.Utils.StableBeatId.CollectIds(beat);
            if (sourceBeatIds.Count == 0 && !string.IsNullOrWhiteSpace(beatIdStr))
                sourceBeatIds.Add(beatIdStr);
            string? cameraMoveToken = null;
            if (aiCamera is not null && aiCamera.TryGetValue(beatIdStr, out var camDir))
            {
                if (!string.IsNullOrWhiteSpace(camDir.FramingPrompt))
                    vp = $"{vp} {PromptTags.Wrap("Camera", PromptTags.SanitizeValue(camDir.FramingPrompt))}";
                cameraMoveToken = $"{camDir.LensSpec}, {camDir.CameraMovement}";
            }
            else if (!string.IsNullOrWhiteSpace(dlg))
            {
                var spkDisplay = !string.IsNullOrWhiteSpace(spk) ? DisplayNameForKey(spk, charSeeds) : "speaker";
                // OTS only when ≥2 on-screen — solo monologue must not invent a listener.
                var framing = GetMonologueCameraFraming(monologueStep, spkDisplay, clipCast.Count);
                vp = $"{vp} {PromptTags.Wrap("Camera", PromptTags.SanitizeValue(framing))}";
            }

            if (aiEmotion is not null && aiEmotion.TryGetValue(beatIdStr, out var emoDir))
            {
                if (!string.IsNullOrWhiteSpace(emoDir.ActingPrompt))
                    vp = $"{vp} {PromptTags.Wrap("Performance", PromptTags.SanitizeValue(emoDir.ActingPrompt))}";
            }

            if (aiDof is not null && aiDof.TryGetValue(beatIdStr, out var dofDir))
            {
                if (!string.IsNullOrWhiteSpace(dofDir.Aperture))
                    vp = $"{vp} {PromptTags.Wrap("Optics", PromptTags.SanitizeValue(dofDir.Aperture))}";
            }

            if (aiColor is not null && !string.IsNullOrWhiteSpace(aiColor.GradingPrompt))
            {
                vp = $"{vp} {aiColor.GradingPrompt}";
            }

            var actionClassVal = beat.TryGetValue("action_class", out var beatAc) ? beatAc : null;
            var primaryVal = beat.TryGetValue("primary_subject", out var psub) ? psub : null;
            var audioPayload = BuildAudioPayload(beat, aiSound is not null && aiSound.TryGetValue(beatIdStr, out var sd) ? sd : null);
            var speakerForFocus = CoerceString(beat.TryGetValue("speaker", out var spkFocus) ? spkFocus : null);
            var secondarySpeakerForFocus = CoerceString(beat.TryGetValue("secondary_speaker", out var spkFocus2) ? spkFocus2 : null);
            var focusKeys = ClipVideoPromptBuilder.ResolveFocusKeys(
                    clipCast,
                    CoerceString(primaryVal),
                    speakerForFocus,
                    CoerceString(actionClassVal),
                    secondarySpeakerForFocus)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var clipDict = new Dictionary<string, object?>
            {
                ["clip_number"] = i + 1,
                ["timestamp"] = FormatTs(t, t + dur),
                ["veo_continuation_source"] = cont,
                ["location_id"] = lid,
                ["visual_prompt"] = vp,
                ["negative_prompt"] = neg,
                ["audio_payload"] = audioPayload,
                ["stage1_beat_id"] = beatIdStr,
                ["stage1_beat_ids"] = sourceBeatIds.Cast<object?>().ToList(),
                ["primary_subject"] = primaryVal,
                // Propagate for gen-time duration (EstimateForClip) — silent big_action etc.
                ["action_class"] = actionClassVal,
                ["characters_on_screen"] = clipCast.Cast<object?>().ToList(),
                // Full identity lock at gen; others on-screen get compact "also present" lines
                ["focus_keys"] = focusKeys.Cast<object?>().ToList(),
                ["duration_seconds"] = dur,
            };

            if (aiColor is not null)
            {
                if (!string.IsNullOrWhiteSpace(aiColor.FilmStock))
                    clipDict["film_stock"] = aiColor.FilmStock;
                if (!string.IsNullOrWhiteSpace(aiColor.ColorPalette))
                    clipDict["color_palette"] = aiColor.ColorPalette;
            }

            if (aiDof is not null && aiDof.TryGetValue(beatIdStr, out var dfd))
            {
                clipDict["aperture"] = dfd.Aperture;
                clipDict["focal_plane"] = dfd.FocalPlane;
                if (!string.IsNullOrWhiteSpace(dfd.RackFocus))
                    clipDict["rack_focus"] = dfd.RackFocus;
            }

            if (aiEmotion is not null && aiEmotion.TryGetValue(beatIdStr, out var ed))
            {
                clipDict["acting_intensity"] = ed.Intensity;
                if (!string.IsNullOrWhiteSpace(ed.MicroExpression))
                    clipDict["micro_expression"] = ed.MicroExpression;
            }

            if (aiCamera is not null && aiCamera.TryGetValue(beatIdStr, out var cd))
            {
                clipDict["shot_scale_hint"] = cd.ShotScale;
                if (!string.IsNullOrWhiteSpace(cameraMoveToken))
                    clipDict["camera_movement_token"] = cameraMoveToken;
            }

            clips.Add(clipDict);
            beatMap.Add(beatIdStr);
            t += dur;
            prevLid = lid;
            prevBeat = beat;
        }

        return BaseSceneShell(scene, lids, primary, cast, total, clips, beatMap);
    }

    private static Dictionary<string, object?> BaseSceneShell(
        Dictionary<string, object?> scene,
        List<string> lids,
        string? primary,
        List<string> cast,
        int total,
        List<object?> clips,
        List<object?> beatMap) => new()
    {
        ["scene_number"] = scene.TryGetValue("scene_number", out var sn) ? sn : null,
        ["setting"] = scene.TryGetValue("setting", out var set) ? set : null,
        ["location_ids"] = lids.Cast<object?>().ToList(),
        ["primary_location_id"] = primary,
        ["characters_on_screen"] = cast.Cast<object?>().ToList(),
        ["scene_filename"] = scene.TryGetValue("scene_filename", out var sf) ? sf : null,
        ["transition_type"] = CoerceString(scene.TryGetValue("transition_type", out var tt) ? tt : null) ?? "cut",
        ["lighting_continuity_token"] =
            CoerceString(scene.TryGetValue("lighting_continuity_token", out var lc) ? lc : null) ?? "",
        ["total_estimated_duration_seconds"] = total,
        ["music_bed"] = MusicBed(scene, total),
        ["veo_clips"] = clips,
        ["stage1_scene_number"] = scene.TryGetValue("scene_number", out var s1) ? s1 : null,
        ["stage1_beat_map"] = beatMap,
        ["video_provider_profile"] = ResolveVideoProviderProfile(null),
        ["spoiler_constraints"] = scene.TryGetValue("spoiler_constraints", out var sp) ? sp : new List<object?>(),
        ["source_book_refs"] = scene.TryGetValue("source_book_refs", out var sbr) ? sbr : new List<object?>(),
    };

    /// <summary>
    /// Filtered, expanded, coalesced story-beat list for a scene, ready to hand to a per-scene
    /// classifier. Called once per classifier that needs it (not shared) — several run
    /// concurrently for the same scene, and each gets its own independent clone so none can
    /// observe another's in-progress work even if a future classifier starts mutating beats.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildSceneBeats(
        Dictionary<string, object?> scene,
        int minSeconds = ClipDurationEstimator.MinSeconds,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int absMaxSeconds = ClipDurationEstimator.AbsMaxSeconds)
    {
        var beats = GetList(scene, "story_beats").OfType<Dictionary<string, object?>>()
            .Where(b => !IsNoopTransitionBeat(b))
            .ToList();
        beats = ClipDurationEstimator.ExpandLongDialogueBeats(beats, modelMaxSeconds: maxSeconds);
        return CoalesceSilentPreludeBeats(beats);
    }

    /// <summary>
    /// If Beat 1 of a scene is a short silent action beat (<= 5s, no dialogue) preceding a dialogue/VO beat (Beat 2)
    /// in the exact same location, fold Beat 1's visual event into Beat 2 so dialogue begins on frame 1.
    /// </summary>
    public static List<Dictionary<string, object?>> CoalesceSilentPreludeBeats(List<Dictionary<string, object?>> beats)
    {
        if (beats.Count < 2) return beats;

        var b1 = beats[0];
        var b2 = beats[1];

        var d1 = CoerceString(b1.TryGetValue("dialogue", out var v1) ? v1 : null);
        var s1 = CoerceString(b1.TryGetValue("speaker", out var sp1) ? sp1 : null);
        var d2 = CoerceString(b2.TryGetValue("dialogue", out var v2) ? v2 : null);

        // Beat 1 must be silent (no dialogue, no speaker) and Beat 2 must have dialogue
        if (string.IsNullOrWhiteSpace(d1) && string.IsNullOrWhiteSpace(s1) && !string.IsNullOrWhiteSpace(d2))
        {
            var l1 = CoerceString(b1.TryGetValue("location_id", out var loc1) ? loc1 : null);
            var l2 = CoerceString(b2.TryGetValue("location_id", out var loc2) ? loc2 : null);

            // Same location or empty
            if (string.Equals(l1, l2, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(l1) || string.IsNullOrEmpty(l2))
            {
                var ve1 = CoerceString(b1.TryGetValue("visual_event", out var vev1) ? vev1 : null);
                var ve2 = CoerceString(b2.TryGetValue("visual_event", out var vev2) ? vev2 : null);

                if (!string.IsNullOrWhiteSpace(ve1))
                {
                    if (string.IsNullOrWhiteSpace(ve2))
                    {
                        b2["visual_event"] = ve1;
                    }
                    else if (!ve2.Contains(ve1, StringComparison.OrdinalIgnoreCase))
                    {
                        b2["visual_event"] = $"{ve1} {ve2}";
                    }
                }

                // Remove silent prelude b1 so b2 becomes clip 1 (frame-1 VO onset)
                PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(b2, b1);
                var result = new List<Dictionary<string, object?>>(beats);
                result.RemoveAt(0);
                return result;
            }
        }

        return beats;
    }

    /// <summary>
    /// Per-beat proxy for "would this beat, if it became the first beat of a new clip, extend from
    /// whatever immediately precedes it" — using the exact same <see cref="ForceNone"/> logic the
    /// final per-clip loop uses to decide each clip's real continuation field, just computed here,
    /// before coalescing, so beat-coalescing can plan against the ceiling that will actually apply
    /// (the model's normal fresh-generation max, or its tighter extension-mode max) instead of
    /// always assuming a fresh cut. Using each merge group's FIRST beat is equivalent to using the
    /// group's true predecessor for this purpose: <see cref="CoalesceShortMonologueBeats"/> and
    /// <see cref="CoalesceCrossSpeakerDialogueBeats"/> only ever merge beats that already share the
    /// same location/action-class/delivery identity as the group's first beat (merging breaks
    /// immediately otherwise), so that identity — the only thing <see cref="ForceNone"/> actually
    /// inspects — never changes as more beats join a group.
    /// </summary>
    private static bool[] PrecomputeExtendsFromPrevious(
        List<Dictionary<string, object?>> beats, string? primary, List<string> lids)
    {
        var result = new bool[beats.Count];
        Dictionary<string, object?>? prevBeat = null;
        string? prevLid = null;
        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var lid = CoerceString(beat.TryGetValue("location_id", out var bl) ? bl : null)
                      ?? primary ?? (lids.Count > 0 ? lids[0] : null);
            result[i] = !ForceNone(beat, i, prevBeat, prevLid, lid);
            prevBeat = beat;
            prevLid = lid;
        }
        return result;
    }

    /// <summary>
    /// Coalesce consecutive short monologue beats (same speaker, same delivery, same location)
    /// aiming for 6–8 second target durations per clip rather than 3–4 second micro-cuts.
    /// Reduces clip transitions and lowers API costs per scene by ~30–40%.
    /// </summary>
    /// <param name="maxSeconds">Resolved per-model clip duration max (see
    /// <see cref="ClipDurationEstimator.ResolveBoundsForModel"/>) — the merge ceiling, so a tighter
    /// or looser model doesn't get a merge limit disconnected from what it can actually generate.</param>
    /// <param name="extensionMaxSeconds">
    /// Tighter ceiling to use instead of <paramref name="maxSeconds"/> when the merge group being
    /// built will itself be generated as an extend-from-previous continuation (some providers, e.g.
    /// Grok, cap the "new portion" of a reference/continue call shorter than a fresh clip). Null or
    /// omitted behaves exactly as before (always <paramref name="maxSeconds"/>).
    /// </param>
    /// <param name="extendsFromPrevious">
    /// Per-beat (index-aligned to <paramref name="beats"/>) precomputed "would this beat, as the
    /// start of a new clip, extend from whatever precedes it" — see
    /// <see cref="PrecomputeExtendsFromPrevious"/>. Only the flag at each merge GROUP's first index
    /// matters, since coalescing never changes a group's effective location/action/speaker identity.
    /// </param>
    public static List<Dictionary<string, object?>> CoalesceShortMonologueBeats(
        List<Dictionary<string, object?>> beats,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int? extensionMaxSeconds = null,
        IReadOnlyList<bool>? extendsFromPrevious = null)
    {
        if (beats is null || beats.Count < 2) return beats ?? new List<Dictionary<string, object?>>();

        var result = new List<Dictionary<string, object?>>();
        var i = 0;
        while (i < beats.Count)
        {
            var groupStart = i;
            var effectiveMax =
                extendsFromPrevious is not null && groupStart < extendsFromPrevious.Count && extendsFromPrevious[groupStart]
                    ? (extensionMaxSeconds ?? maxSeconds)
                    : maxSeconds;
            var cur = new Dictionary<string, object?>(beats[i]);
            var d1 = CoerceString(cur.TryGetValue("dialogue", out var v1) ? v1 : null);
            var sp1 = CoerceString(cur.TryGetValue("speaker", out var s1) ? s1 : null);
            var del1 = CoerceString(cur.TryGetValue("delivery", out var delv1) ? delv1 : null) ?? "spoken_on_camera";
            var loc1 = CoerceString(cur.TryGetValue("location_id", out var l1) ? l1 : null);
            var ac1 = CoerceString(cur.TryGetValue("action_class", out var acv1) ? acv1 : null);

            if (!string.IsNullOrWhiteSpace(d1) &&
                !string.IsNullOrWhiteSpace(sp1) &&
                !string.Equals(ac1, "big_action", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < beats.Count)
                {
                    var next = beats[i + 1];
                    var d2 = CoerceString(next.TryGetValue("dialogue", out var v2) ? v2 : null);
                    var sp2 = CoerceString(next.TryGetValue("speaker", out var s2) ? s2 : null);
                    var del2 = CoerceString(next.TryGetValue("delivery", out var delv2) ? delv2 : null) ?? "spoken_on_camera";
                    var loc2 = CoerceString(next.TryGetValue("location_id", out var l2) ? l2 : null);
                    var ac2 = CoerceString(next.TryGetValue("action_class", out var acv2) ? acv2 : null);

                    if (string.IsNullOrWhiteSpace(d2) ||
                        !string.Equals(sp1, sp2, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(del1, del2, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(loc1) && !string.IsNullOrEmpty(loc2) && !string.Equals(loc1, loc2, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(ac2, "big_action", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var combinedDlg = $"{d1.Trim()} {d2.Trim()}";
                    var estCombined = ClipDurationEstimator.EstimateUncapped(combinedDlg, "", "dialogue", del1);
                    if (estCombined > effectiveMax)
                    {
                        break;
                    }

                    d1 = combinedDlg;
                    cur["dialogue"] = d1;

                    MergeVisualEvent(cur, next);
                    PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(cur, next);

                    i++;
                }
            }

            result.Add(cur);
            i++;
        }

        return result;
    }

    /// <summary>
    /// Coalesce an adjacent pair of short dialogue beats from two <em>different</em> speakers into
    /// one clip, with the second speaker's line carried as <c>secondary_speaker</c>/
    /// <c>secondary_dialogue</c> rather than merged into <c>dialogue</c> — so the clip becomes a
    /// two-line exchange (camera pans from speaker A to speaker B) instead of a same-speaker
    /// monologue. Runs after <see cref="CoalesceShortMonologueBeats"/> so same-speaker runs are
    /// already consolidated; only ever merges exactly two beats (no 3+ speaker chains — see the
    /// "3+ speakers in one clip" backlog item).
    /// </summary>
    /// <param name="maxSeconds">Resolved per-model clip duration max — combined estimated speech
    /// time for both lines (each with its own head/tail pause) must fit within this.</param>
    /// <param name="extensionMaxSeconds">
    /// Tighter ceiling in place of <paramref name="maxSeconds"/> when the pair being considered will
    /// itself extend from the previous clip — see the identical parameter on
    /// <see cref="CoalesceShortMonologueBeats"/>.
    /// </param>
    /// <param name="extendsFromPrevious">Per-beat precomputed extend flag — see
    /// <see cref="PrecomputeExtendsFromPrevious"/>.</param>
    /// <summary>
    /// Characters this video model can render speaking in one clip, from the catalog
    /// (<see cref="SupportedModelEntry.MaxSpeakersPerClip"/>). Unknown/unset → 1 (the safe,
    /// always-renderable default). Raise per model in models_catalog.json as video models improve.
    /// </summary>
    public static int ResolveMaxSpeakersPerClip(string? videoModelId) =>
        SupportedModelCatalog.Find(videoModelId, ModelCapability.Video)?.MaxSpeakersPerClipOrDefault ?? 1;

    /// <summary>
    /// Coalesce adjacent different-speaker dialogue beats into two-hander clips only when the model
    /// allows two speakers per clip; otherwise leave each beat as its own single-speaker clip
    /// (shot-reverse-shot). The seam the planner uses so the policy is one catalog value, not a
    /// hard-coded assumption. (There is no three-hander — a third speaker is always its own clip.)
    /// </summary>
    public static List<Dictionary<string, object?>> ApplyCrossSpeakerCoalescing(
        List<Dictionary<string, object?>> beats,
        int maxSpeakersPerClip,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int? extensionMaxSeconds = null,
        IReadOnlyList<bool>? extendsFromPrevious = null) =>
        maxSpeakersPerClip >= 2
            ? CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds, extensionMaxSeconds, extendsFromPrevious)
            : beats ?? new List<Dictionary<string, object?>>();

    public static List<Dictionary<string, object?>> CoalesceCrossSpeakerDialogueBeats(
        List<Dictionary<string, object?>> beats,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int? extensionMaxSeconds = null,
        IReadOnlyList<bool>? extendsFromPrevious = null)
    {
        if (beats is null || beats.Count < 2) return beats ?? new List<Dictionary<string, object?>>();

        var result = new List<Dictionary<string, object?>>();
        var i = 0;
        while (i < beats.Count)
        {
            var effectiveMax =
                extendsFromPrevious is not null && i < extendsFromPrevious.Count && extendsFromPrevious[i]
                    ? (extensionMaxSeconds ?? maxSeconds)
                    : maxSeconds;
            var perLineCap = effectiveMax / 2.0;
            var cur = new Dictionary<string, object?>(beats[i]);
            var d1 = CoerceString(cur.TryGetValue("dialogue", out var v1) ? v1 : null);
            var sp1 = CoerceString(cur.TryGetValue("speaker", out var s1) ? s1 : null);
            var loc1 = CoerceString(cur.TryGetValue("location_id", out var l1) ? l1 : null);
            var ac1 = CoerceString(cur.TryGetValue("action_class", out var acv1) ? acv1 : null);

            if (i + 1 < beats.Count &&
                !string.IsNullOrWhiteSpace(d1) &&
                !string.IsNullOrWhiteSpace(sp1) &&
                !string.Equals(ac1, "big_action", StringComparison.OrdinalIgnoreCase))
            {
                var next = beats[i + 1];
                var d2 = CoerceString(next.TryGetValue("dialogue", out var v2) ? v2 : null);
                var sp2 = CoerceString(next.TryGetValue("speaker", out var s2) ? s2 : null);
                var loc2 = CoerceString(next.TryGetValue("location_id", out var l2) ? l2 : null);
                var ac2 = CoerceString(next.TryGetValue("action_class", out var acv2) ? acv2 : null);

                var sameLocationOrEmpty = string.IsNullOrEmpty(loc1) || string.IsNullOrEmpty(loc2) ||
                    string.Equals(loc1, loc2, StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(d2) &&
                    !string.IsNullOrWhiteSpace(sp2) &&
                    !string.Equals(sp1, sp2, StringComparison.OrdinalIgnoreCase) &&
                    sameLocationOrEmpty &&
                    !string.Equals(ac2, "big_action", StringComparison.OrdinalIgnoreCase))
                {
                    var del1 = CoerceString(cur.TryGetValue("delivery", out var delv1) ? delv1 : null) ?? "spoken_on_camera";
                    var del2 = CoerceString(next.TryGetValue("delivery", out var delv2) ? delv2 : null) ?? "spoken_on_camera";
                    var est1 = ClipDurationEstimator.EstimateUncapped(d1, "", "dialogue", del1);
                    var est2 = ClipDurationEstimator.EstimateUncapped(d2, "", "dialogue", del2);

                    if (est1 <= perLineCap && est2 <= perLineCap && (est1 + est2) <= effectiveMax)
                    {
                        cur["secondary_speaker"] = sp2;
                        cur["secondary_dialogue"] = d2;

                        // Size the merged clip for BOTH lines (was left at the primary's estimate,
                        // so the second speaker's line got cut). Read the spoken lines back through
                        // the shared accessor and size with the shared estimator, capped at the same
                        // effective max used to decide the merge fit.
                        cur["duration_seconds"] = ClipDurationEstimator.EstimateSpokenLinesSeconds(
                            ClipSpokenLines.FromBeat(cur), maxSeconds: effectiveMax);

                        MergeVisualEvent(cur, next);
                        PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(cur, next);

                        i++;
                    }
                }
            }

            result.Add(cur);
            i++;
        }

        return result;
    }

    /// <summary>
    /// When merging <paramref name="next"/> into <paramref name="cur"/>, fold next's
    /// <c>visual_event</c> into cur's: append it (space-joined) unless it is blank or already
    /// contained (case-insensitive) in cur's existing visual_event.
    /// </summary>
    private static void MergeVisualEvent(Dictionary<string, object?> cur, Dictionary<string, object?> next)
    {
        var ve1 = CoerceString(cur.TryGetValue("visual_event", out var vev1) ? vev1 : null) ?? "";
        var ve2 = CoerceString(next.TryGetValue("visual_event", out var vev2) ? vev2 : null) ?? "";
        if (!string.IsNullOrWhiteSpace(ve2) && !ve1.Contains(ve2, StringComparison.OrdinalIgnoreCase))
        {
            cur["visual_event"] = string.IsNullOrWhiteSpace(ve1) ? ve2 : $"{ve1} {ve2}";
        }
    }

    /// <summary>
    /// Cycle camera framing across continuous monologue beats.
    /// Multi-cast: Medium → ECU eyes → OTS → Hands.
    /// Solo: Medium → ECU eyes → three-quarter profile → Hands (never OTS — no invented listener).
    /// </summary>
    public static string GetMonologueCameraFraming(
        int step,
        string speakerDisplay = "speaker",
        int onScreenCastCount = 1)
    {
        var s = string.IsNullOrWhiteSpace(speakerDisplay) ? "speaker" : speakerDisplay;
        var multi = onScreenCastCount >= 2;
        return (step % 4) switch
        {
            0 => $"Medium shot, 35mm lens, slow push-in as {s} speaks",
            1 => $"Extreme close-up on eyes and facial intensity of {s}, 85mm lens, shallow depth of field",
            2 when multi =>
                $"Over-the-shoulder shot, 50mm lens, listening perspective facing {s}",
            2 =>
                $"Three-quarter profile of {s}, 50mm lens, intimate confessional angle",
            3 => $"Close-up on hands of {s}, 50mm macro lens, capturing subtle hand movements and gestures",
            _ => $"Medium shot, 35mm lens, slow push-in as {s} speaks",
        };
    }

    private static string BuildVisualPrompt(
        Dictionary<string, object?> beat,
        Dictionary<string, object?> scene,
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, List<string>> wardrobe,
        int clipIndex)
    {
        var ve = CoerceString(beat.TryGetValue("visual_event", out var vev) ? vev : null) ?? "";
        // Strip accidental technical suffix from beat text (res/fps owned at gen time)
        ve = Regex.Replace(ve, @"\s*/\s*\d+p.*$", "", RegexOptions.IgnoreCase).Trim();
        var cast = ClipCastTokens(scene, beat, charSeeds);
        var primary = CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null)
                      ?? (cast.Count > 0 ? cast[0] : "");

        var place = LocationLockPhrase(scene, beat, locSeeds);
        var style = RenderStyleLock(scene);
        // Bug fix: previously only fired for human cast tokens ("mom"/"dad"/"human"),
        // so animal-only scenes (e.g. Buster's backyard opener) received no style lock and
        // rendered in a completely different visual style from all subsequent scenes.
        // Fix: fire for any on-screen character that is visually present, i.e. not a
        // pure-voice-only character (display_name_policy = "never_on_screen").
        if (string.IsNullOrWhiteSpace(style) &&
            cast.Any(t => !IsNeverOnScreenCharacter(t, charSeeds)))
        {
            style =
                "STYLE LOCK: stylized 3D animated children's picture-book CG " +
                "(same render family as animal hero) -- not photoreal, not live-action";
        }

        // Attach subject as readable display name — never "Character_X He steadies…"
        if (!string.IsNullOrEmpty(primary) && !VisualMentionsSubject(ve, primary))
        {
            var display = DisplayNameForKey(primary, charSeeds);
            ve = AttachPrimaryToVisual(ve, primary, display);
        }

        var others = cast.Where(t => t != primary && !ve.Contains(t, StringComparison.Ordinal)).Take(3).ToList();
        var othersBit = others.Count > 0 ? $"also on screen: {string.Join(", ", others)}" : "";
        // CAST COUNT + CHARACTER VARIABLES owned by ClipVideoPromptBuilder at gen time.

        var block = CoerceString(beat.TryGetValue("blocking_notes", out var bn) ? bn : null) ?? "";
        if (!string.IsNullOrWhiteSpace(block) &&
            !ve.Contains(block, StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. {block}".Trim();

        var ac = (CoerceString(beat.TryGetValue("action_class", out var acv) ? acv : null) ?? "").ToLowerInvariant();
        if (ac == "big_action" &&
            !ve.Contains("continuous", StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. ONE continuous take no cut; unbroken cause-to-effect motion";

        // Establishing shots otherwise describe only a static composition — a known AI-video
        // failure mode where the "opening wide shot" of a scene looks like a frozen photo. Nudge
        // in setting-appropriate ambient background life (the model invents specifics; no new
        // classifier call), mirroring how big_action gets its own action_class-specific guidance.
        if (ac == "establishing" &&
            !ve.Contains("subtle", StringComparison.OrdinalIgnoreCase) &&
            !ve.Contains("ambient motion", StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. Include subtle background motion appropriate to this setting (e.g. distant " +
                 "traffic or passersby, a sign or light flickering, wind moving debris/foliage/fabric) " +
                 "so the shot feels alive, not a still photo";

        var speech = SpeechClause(beat, cast);
        var mustNot = GetList(beat, "must_not").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(3).ToList();
        var mustBit = mustNot.Count > 0 ? $"must not: {string.Join("; ", mustNot)}" : "";
        // Same wardrobe phrase length for all clips in the scene (consistent continuity language).
        var ward = WardrobeContinuityClause(wardrobe, cast, clipIndex, primary);

        // Join full slots — no length budget, no dropping fields, no ellipsis packing.
        // Identity cues omitted: gen-time CHARACTER VARIABLES + locked refs own identity.
        var parts = new List<(int Order, string Text)>
        {
            (0, style),
            (2, !string.IsNullOrEmpty(place) && !ve.Contains(place, StringComparison.OrdinalIgnoreCase) ? place : ""),
            (3, othersBit),
            (5, ve),
            (6, speech),
            (7, mustBit),
            (8, ward),
        };
        return JoinVisualPromptParts(parts);
    }

    /// <summary>
    /// True if visual text already names the character (full key or bare name like NARRATOR / Old Man).
    /// </summary>
    public static bool VisualMentionsSubject(string visual, string primaryKey)
    {
        if (string.IsNullOrWhiteSpace(visual) || string.IsNullOrWhiteSpace(primaryKey))
            return false;
        if (visual.Contains(primaryKey, StringComparison.OrdinalIgnoreCase))
            return true;
        var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? primaryKey["Character_".Length..]
            : primaryKey;
        if (string.IsNullOrWhiteSpace(bare)) return false;
        // Character_Old_Man → "Old Man", "Old_Man", "OLDMAN"
        var spaced = bare.Replace('_', ' ');
        if (visual.Contains(spaced, StringComparison.OrdinalIgnoreCase))
            return true;
        if (visual.Contains(bare, StringComparison.OrdinalIgnoreCase))
            return true;
        var compact = Regex.Replace(bare, @"[_ ]+", "");
        if (compact.Length >= 3 &&
            Regex.IsMatch(visual, $@"\b{Regex.Escape(compact)}\b", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Join primary subject into action prose as a display name.
    /// Pronoun leads (He/She/They…) become named subjects — never <c>Character_* He …</c>.
    /// </summary>
    public static string AttachPrimaryToVisual(
        string? visualEvent,
        string primaryKey,
        string? displayName = null)
    {
        var ve = (visualEvent ?? "").Trim();
        if (ve.Length == 0)
            return ve;
        if (string.IsNullOrWhiteSpace(primaryKey))
            return ve;
        if (VisualMentionsSubject(ve, primaryKey))
            return ve;

        var name = (displayName ?? "").Trim();
        if (name.Length == 0)
        {
            var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
                ? primaryKey["Character_".Length..]
                : primaryKey;
            name = bare.Replace('_', ' ').Trim();
        }
        if (name.Length == 0)
            return ve;

        // He steadies… / She turns… / They wait…
        var m = Regex.Match(
            ve,
            @"^(He|She|They|Him|Her|Them)\b(\s+)(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
            return $"{name} {m.Groups["rest"].Value.Trim()}".Trim();

        // His hands… / Her eyes…
        m = Regex.Match(
            ve,
            @"^(His|Her|Their)\b(\s+)(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
            return $"{name}'s {m.Groups["rest"].Value.Trim()}".Trim();

        // Prefer human-readable name in action (CAST COUNT / variables still use Character_*)
        return $"{name} {ve}".Trim();
    }

    private static string DisplayNameForKey(
        string primaryKey,
        Dictionary<string, object?> charSeeds)
    {
        if (charSeeds.TryGetValue(primaryKey, out var seed) &&
            seed is Dictionary<string, object?> d)
        {
            var cn = CoerceString(d.TryGetValue("canonical_given_name", out var c) ? c : null);
            if (!string.IsNullOrWhiteSpace(cn))
                return cn!;
            var vl = CoerceString(d.TryGetValue("voice_label", out var v) ? v : null);
            if (!string.IsNullOrWhiteSpace(vl))
                return vl!.Replace('_', ' ');
        }
        var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? primaryKey["Character_".Length..]
            : primaryKey;
        return bare.Replace('_', ' ').Trim();
    }

    private static string JoinVisualPromptParts(IEnumerable<(int Order, string Text)> parts)
    {
        var sentences = parts
            .OrderBy(p => p.Order)
            .Select(p => NormalizeSentencePart(p.Text))
            .Where(t => t.Length > 0)
            .ToList();
        if (sentences.Count == 0)
            sentences.Add("Scene action");
        var body = string.Join(". ", sentences);
        return body.TrimEnd('.', ' ', '\t');
    }

    private static readonly Regex CharacterTokenRegex = new(@"Character_[A-Za-z0-9_]+", RegexOptions.Compiled);

    private static string NormalizeSentencePart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = CommonRegex.WhitespaceCollapse.Replace(text.Trim(), " ");
        // Collapse internal double punctuation / trailing junk
        t = CommonRegex.DotCollapse.Replace(t, ".");
        t = t.TrimEnd('.', ',', ';', ' ', '\t');
        return t;
    }

    /// <summary>Story-specific negatives only (must_not + wardrobe soft ban), deduped.</summary>
    public static string BuildStoryNegativePrompt(
        Dictionary<string, object?> beat,
        Dictionary<string, List<string>> wardrobe,
        List<string> clipCast)
    {
        var items = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (piece.Length == 0) continue;
                if (items.Any(x => x.Equals(piece, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(piece);
            }
        }

        Add(NegExtras(beat));
        Add(WardrobeNegativeExtras(wardrobe, clipCast));
        return string.Join(", ", items);
    }

    /// <summary>FADE IN / CUT TO-only beats produce empty visual prompts — never plan clips for them.</summary>
    private static bool IsNoopTransitionBeat(Dictionary<string, object?> beat)
    {
        var dlg = CoerceString(beat.TryGetValue("dialogue", out var d) ? d : null) ?? "";
        if (!string.IsNullOrWhiteSpace(dlg)) return false;
        var ve = CoerceString(beat.TryGetValue("visual_event", out var v) ? v : null) ?? "";
        if (string.IsNullOrWhiteSpace(ve)) return true;
        if (FountainParser.IsStandaloneTransitionLine(ve)) return true;
        return Regex.IsMatch(
            ve.Trim(),
            @"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO(\s+BLACK)?|DISSOLVE\s+TO|SMASH\s+CUT\s+TO|BLACK\s+OUT|THE\s+END)[\s\.:]*$",
            RegexOptions.IgnoreCase);
    }

    private static bool ForceNone(
        Dictionary<string, object?> beat,
        int clipIndex,
        Dictionary<string, object?>? prevBeat,
        string? prevLocationId,
        string? locationId)
    {
        if (clipIndex == 0) return true;
        // AI / enricher cut decision (hard_cut|extend) — preferred when present
        var cut = (CoerceString(beat.TryGetValue("cut_decision", out var cd) ? cd : null) ?? "").ToLowerInvariant();
        if (cut is "hard_cut" or "hardcut" or "none") return true;
        if (cut is "extend" or "continue" or "continuous") return false;

        var ac = (CoerceString(beat.TryGetValue("action_class", out var a) ? a : null) ?? "").ToLowerInvariant();
        var cont = (CoerceString(beat.TryGetValue("continuity", out var c) ? c : null) ?? "").ToLowerInvariant();
        if (ac is "big_action" or "establishing" or "hard_cut" or "flashback_enter" or "flashback_exit" or "montage")
            return true;
        if (cont is "new_setup" or "return_to_present" or "parallel")
            return true;
        if (prevLocationId is not null && locationId is not null && prevLocationId != locationId)
            return true;
        // Silent establish → first spoken/VO: hard cut so opening words are not clipped by extend
        if (prevBeat is not null && BeatHasSpokenAudio(beat) && !BeatHasSpokenAudio(prevBeat))
            return true;
        if (IsVoBeat(beat) && prevBeat is not null && IsOnCameraSpeech(prevBeat))
            return true;
        if (IsVoBeat(beat))
            return cont != "continuous_from_previous_beat";
        var ve = (CoerceString(beat.TryGetValue("visual_event", out var vev) ? vev : null) ?? "").ToLowerInvariant();
        if (Regex.IsMatch(ve,
                @"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|flashback|back to present|cut to)\b"))
            return true;
        return false;
    }

    /// <summary>True when the beat carries spoken dialogue or VO (not silent action).</summary>
    private static bool BeatHasSpokenAudio(Dictionary<string, object?> beat)
    {
        var (delivery, _) = BeatAudio(beat);
        if (delivery is "none" or "")
            return false;
        var dialogue = CoerceString(beat.TryGetValue("dialogue", out var d) ? d : null) ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) &&
            beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad)
            dialogue = CoerceString(ad.TryGetValue("dialogue", out var d2) ? d2 : null) ?? "";
        if (string.IsNullOrWhiteSpace(dialogue))
            return false;
        return IsOnCameraDelivery(delivery) ||
               delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought" or
                   "voiceover" or "voice_over" or "off_camera" or "offcamera";
    }

    private static bool IsVoBeat(Dictionary<string, object?> beat)
    {
        var (delivery, speaker) = BeatAudio(beat);
        return delivery is "voiceover_internal" or "internal" or "vo_internal" or "thought"
                   or "thinking" or "narration" or "vo"
               || speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnCameraSpeech(Dictionary<string, object?> beat)
    {
        var (delivery, speaker) = BeatAudio(beat);
        return IsOnCameraDelivery(delivery) &&
               !speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blueprint may use on_camera or spoken_on_camera for lip-sync dialogue.</summary>
    public static bool IsOnCameraDelivery(string? delivery)
    {
        var d = (delivery ?? "").Trim().ToLowerInvariant();
        return d is "spoken_on_camera" or "on_camera" or "spoken";
    }

    /// <summary>Normalize delivery aliases to canonical tokens for audio_payload.</summary>
    public static string NormalizeDelivery(string? delivery)
    {
        var d = (delivery ?? "none").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(d)) return "none";
        if (d is "on_camera" or "spoken" or "dialogue_on_camera")
            return "spoken_on_camera";
        if (d is "vo" or "voiceover" or "voice_over" or "off_camera" or "offcamera")
            return "voiceover_internal";
        return d;
    }

    private static (string Delivery, string Speaker) BeatAudio(Dictionary<string, object?> beat)
    {
        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = NormalizeDelivery(CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null));
        var speaker = (CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "").ToLowerInvariant();
        return (delivery, speaker);
    }

    private static Dictionary<string, object?> BuildAudioPayload(
        Dictionary<string, object?> beat,
        SoundDesignDirective? sd = null)
    {
        // Prefer normalized separate keys (Stage1Normalizer / Fountain importer)
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);

        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = NormalizeDelivery(CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null) ?? "none");
        var speaker = CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "";
        var dialogue = CoerceString(nested?.TryGetValue("dialogue", out var dlg) == true ? dlg
            : beat.TryGetValue("dialogue", out var dlg2) ? dlg2 : null) ?? "";
        // Store speech-safe dialogue in the plan (UI + gen see the same text)
        dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
        var ambient = CoerceString(nested?.TryGetValue("ambient", out var am) == true ? am
            : beat.TryGetValue("ambient", out var am2) ? am2 : null) ?? "";
        var sfx = CoerceString(nested?.TryGetValue("sfx", out var sx) == true ? sx
            : beat.TryGetValue("sfx", out var sx2) ? sx2 : null) ?? "";
        var pronHint = CoerceString(nested?.TryGetValue("pronunciation_hint", out var ph) == true ? ph
            : beat.TryGetValue("pronunciation_hint", out var ph2) ? ph2 : null) ?? "";

        var payload = new Dictionary<string, object?>
        {
            ["delivery"] = delivery,
            ["speaker"] = speaker,
            ["dialogue"] = dialogue,
            ["sfx"] = sfx,
            ["ambient"] = ambient,
        };

        // A pronunciation hint only earns its place when the word it targets is actually spoken in this
        // beat's dialogue — carrying one onto a silent/no-dialogue beat (or for a word not in the line)
        // just adds noise to the prompt.
        if (!string.IsNullOrWhiteSpace(pronHint) &&
            Deterministic.Pronunciation.PronunciationResolver.HintAppliesToDialogue(pronHint, dialogue))
        {
            payload["pronunciation_hint"] = pronHint;
        }

        // Cross-speaker two-hander clips (CoalesceCrossSpeakerDialogueBeats) carry a second
        // speaker's line here. Additive only — existing single-speaker readers keep working
        // unmodified since the flat speaker/dialogue keys above are untouched.
        var secondarySpeaker = CoerceString(beat.TryGetValue("secondary_speaker", out var ss) ? ss : null);
        var secondaryDialogue = CoerceString(beat.TryGetValue("secondary_dialogue", out var sd2) ? sd2 : null);
        if (!string.IsNullOrWhiteSpace(secondarySpeaker) && !string.IsNullOrWhiteSpace(secondaryDialogue))
        {
            payload["secondary_speaker"] = secondarySpeaker;
            payload["secondary_dialogue"] = ClipVideoPromptBuilder.SanitizeSpokenDialogue(secondaryDialogue);
        }

        if (sd is not null)
        {
            if (!string.IsNullOrWhiteSpace(sd.AmbientLayer))
                payload["ambient_layer"] = sd.AmbientLayer;
            if (!string.IsNullOrWhiteSpace(sd.FoleyLayer))
                payload["foley_layer"] = sd.FoleyLayer;
            if (!string.IsNullOrWhiteSpace(sd.ScoreLayer))
                payload["score_layer"] = sd.ScoreLayer;
        }

        return payload;
    }

    private static string SpeechClause(Dictionary<string, object?> beat, List<string> cast)
    {
        var ap = BuildAudioPayload(beat);
        var delivery = (ap["delivery"] as string ?? "none").ToLowerInvariant();
        var speaker = ap["speaker"] as string ?? "";
        var dialogue = ap["dialogue"] as string ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) || delivery is "none" or "")
            return "";
        // Full speech-safe line (BuildAudioPayload already sanitized)
        var quote = dialogue.Trim();
        if (IsOnCameraDelivery(delivery))
            return $"{speaker} ON CAMERA lip-syncs \"{quote}\"";
        return $"OFF-CAMERA VOICEOVER {speaker} says \"{quote}\"";
    }

    /// <summary>Test hook — thin public wrapper for <see cref="ClipCastTokens"/>.</summary>
    public static List<string> ClipCastTokensPublic(
        Dictionary<string, object?> scene,
        Dictionary<string, object?> beat,
        Dictionary<string, object?>? charSeeds = null)
        => ClipCastTokens(scene, beat, charSeeds);

    private static List<string> ClipCastTokens(
        Dictionary<string, object?> scene,
        Dictionary<string, object?> beat,
        Dictionary<string, object?>? charSeeds = null)
    {
        var found = new List<string>();
        void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!key.StartsWith("Character_", StringComparison.Ordinal)) return;
            if (!found.Contains(key)) found.Add(key);
        }

        void AddFrom(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in CharacterTokenRegex.Matches(text))
                Add(m.Value);
        }
        // AI / enricher closed-set list preferred when present.
        // Bug fix: previously short-circuited as soon as *any* character was found in
        // characters_on_screen, even when all found characters are pure voice-only
        // (display_name_policy = "never_on_screen", e.g. a narrator V.O.). This caused
        // visible on-screen characters described in visual_event prose (like Buster bounding
        // across the yard in Scene 1) to be silently dropped — resulting in no reference
        // image being attached for the visually present animal lead.
        // Fix: only short-circuit if at least one found character is visually present.
        if (beat.TryGetValue("characters_on_screen", out var cos) && cos is List<object?> cosList && cosList.Count > 0)
        {
            foreach (var x in cosList)
                Add(x?.ToString());
            // Short-circuit only when at least one found character is actually on screen.
            // If every character listed is a never_on_screen voice-only role, fall through
            // and also scan visual_event prose for additional visible characters.
            if (found.Any(k => !IsNeverOnScreenCharacter(k, charSeeds)))
                return found;
        }
        var veText = CoerceString(beat.TryGetValue("visual_event", out var ve) ? ve : null) ?? "";
        AddFrom(veText);
        AddFrom(CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null));
        AddFrom(CoerceString(beat.TryGetValue("speaker", out var sp) ? sp : null));
        AddFrom(CoerceString(beat.TryGetValue("blocking_notes", out var bn) ? bn : null));

        // Promote free-text names (OLD MAN, three officers) using cast seed keys
        if (charSeeds is { Count: > 0 })
        {
            var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in charSeeds)
            {
                if (v is not Dictionary<string, object?> d) continue;
                profiles[k] = new ClipVideoPromptBuilder.CharacterProfile
                {
                    Key = k,
                    DisplayName = CoerceString(d.TryGetValue("canonical_given_name", out var cn) ? cn : null)
                        ?? CoerceString(d.TryGetValue("voice_label", out var vl) ? vl : null)
                        ?? k.Replace("Character_", "").Replace('_', ' '),
                };
            }
            var prose = string.Join(" ", new[]
            {
                veText,
                CoerceString(beat.TryGetValue("blocking_notes", out var bn2) ? bn2 : null) ?? "",
            });
            foreach (var key in ClipVideoPromptBuilder.InferKeysFromProse(prose, profiles))
                Add(key);
        }

        if (found.Count == 0)
            found.AddRange(UnionCharactersOnScreen(scene));
        return found;
    }

    private static List<string> UnionCharactersOnScreen(Dictionary<string, object?> scene)
    {
        var set = new List<string>();
        void Add(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            if (!t.StartsWith("Character_", StringComparison.Ordinal)) return;
            if (!set.Contains(t)) set.Add(t);
        }
        foreach (var x in GetList(scene, "characters_on_screen"))
            Add(x?.ToString());
        foreach (var b in GetList(scene, "story_beats").OfType<Dictionary<string, object?>>())
        {
            Add(CoerceString(b.TryGetValue("primary_subject", out var ps) ? ps : null));
            Add(CoerceString(b.TryGetValue("speaker", out var sp) ? sp : null));
            var ve = CoerceString(b.TryGetValue("visual_event", out var vev) ? vev : null) ?? "";
            foreach (Match m in Regex.Matches(ve, @"Character_[A-Za-z0-9_]+"))
                Add(m.Value);
        }
        return set;
    }

    /// <summary>
    /// Place line for visual_prompt. Prefer the scene's full heading (correct DAY/NIGHT)
    /// so we never stamp the first-visit time-of-day from a shared location seed.
    /// </summary>
    public static string LocationLockPhrase(
        Dictionary<string, object?> scene,
        Dictionary<string, object?> beat,
        Dictionary<string, object?> locSeeds)
    {
        // Current scene heading wins — includes correct time of day for this visit
        var setting = CoerceString(scene.TryGetValue("setting", out var st) ? st : null)?.Trim();
        if (!string.IsNullOrWhiteSpace(setting) && LooksLikeSceneHeading(setting))
            return setting!;

        var lid = CoerceString(beat.TryGetValue("location_id", out var bl) ? bl : null)
                  ?? CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null);
        if (string.IsNullOrEmpty(lid)) return setting ?? "";

        if (locSeeds.TryGetValue(lid, out var seedObj) && seedObj is Dictionary<string, object?> seed)
        {
            var lockTxt = CoerceString(seed.TryGetValue("visual_lock", out var vl) ? vl : null)
                          ?? CoerceString(seed.TryGetValue("description", out var d) ? d : null)
                          ?? lid;
            if (IsPlaceholderIdentityText(lockTxt))
                return lid;
            // If seed still has a full heading with TOD, prefer scene setting when available
            if (!string.IsNullOrWhiteSpace(setting))
                return setting!;
            return lockTxt;
        }

        return !string.IsNullOrWhiteSpace(setting) ? setting! : lid;
    }

    /// <summary>True for Fountain-style INT./EXT. headings (used to prefer scene.setting as place lock).</summary>
    public static bool LooksLikeSceneHeading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return Regex.IsMatch(
            t,
            @"^(INT\.?|EXT\.?|EST\.?|I/?E\.?|INT\.?\s*/\s*EXT\.?)\b",
            RegexOptions.IgnoreCase);
    }

    private static string RenderStyleLock(Dictionary<string, object?> scene) =>
        CoerceString(scene.TryGetValue("render_style_lock", out var r) ? r : null) ?? "";

    /// <summary>
    /// True when a character key has <c>display_name_policy = "never_on_screen"</c> in the
    /// character seed dictionary, meaning it is a pure voice-only role (e.g. a V.O. narrator)
    /// that never appears visually. Used by both bug fixes:
    /// <list type="bullet">
    ///   <item>STYLE LOCK fallback — only fires when at least one <em>visually present</em>
    ///   character is in the cast (Bug 1).</item>
    ///   <item>ClipCastTokens short-circuit — only skips prose scan when at least one
    ///   character that is NOT a voice-only role was found in characters_on_screen (Bug 2).</item>
    /// </list>
    /// </summary>
    private static bool IsNeverOnScreenCharacter(string key, Dictionary<string, object?>? charSeeds)
    {
        if (charSeeds is null) return false;
        if (!charSeeds.TryGetValue(key, out var seedObj) || seedObj is not Dictionary<string, object?> seed)
            return false;
        var policy = CoerceString(seed.TryGetValue("display_name_policy", out var p) ? p : null);
        return string.Equals(policy, "never_on_screen", StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// True for empty / generic import stubs that must not appear in visual prompts.
    /// </summary>
    public static bool IsPlaceholderIdentityText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Contains("as described in the screenplay", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("as described in the scr", StringComparison.OrdinalIgnoreCase))
            return true;
        // "Match Name as cast for this production." — old EnsureCharacter visual_lock
        if (Regex.IsMatch(t, @"^Match\s+.+\s+as cast for this production\.?$", RegexOptions.IgnoreCase))
            return true;
        // Bare name-only or "Name (voice only…)" without real appearance detail is OK to skip for visual
        if (t.Contains("voice only", StringComparison.OrdinalIgnoreCase) &&
            t.Length < 80)
            return true;
        return false;
    }

    private static Dictionary<string, List<string>> InitWardrobeState(
        List<string> cast,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, object?> scene)
    {
        var state = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in cast)
        {
            // Order: wardrobe_always (identity) then scene sticky; put_on prepends later
            var items = new List<string>();
            if (charSeeds.TryGetValue(key, out var s) && s is Dictionary<string, object?> seed)
                items.AddRange(Stage1Normalizer.CoerceStringList(
                    seed.TryGetValue("wardrobe_always", out var wa) ? wa : null));
            if (scene.TryGetValue("wardrobe_by_character", out var wbc) &&
                wbc is Dictionary<string, object?> map &&
                map.TryGetValue(key, out var itemsObj))
                items.AddRange(Stage1Normalizer.CoerceStringList(itemsObj));
            state[key] = PrioritizeWardrobeItems(items).ToList();
        }
        return state;
    }

    private static void UpdateWardrobeFromBeat(
        Dictionary<string, List<string>> state,
        Dictionary<string, object?> beat,
        List<string> cast)
    {
        var putOn = Stage1Normalizer.CoerceStringList(
            beat.TryGetValue("wardrobe_put_on", out var po) ? po : null, 8);
        var remove = Stage1Normalizer.CoerceStringList(
            beat.TryGetValue("wardrobe_remove", out var rm) ? rm : null, 8);
        var subject = CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null)
                      ?? (cast.Count > 0 ? cast[0] : null);
        if (subject is null) return;
        if (!state.TryGetValue(subject, out var list))
        {
            list = new List<string>();
            state[subject] = list;
        }
        foreach (var r in remove)
            list.RemoveAll(x => x.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                                r.Contains(x, StringComparison.OrdinalIgnoreCase));
        // Newest put-on first (most important for current continuity)
        for (var i = putOn.Count - 1; i >= 0; i--)
        {
            var p = putOn[i];
            list.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, p);
        }
        state[subject] = PrioritizeWardrobeItems(list).ToList();
    }

    private static string WardrobeContinuityClause(
        Dictionary<string, List<string>> state,
        List<string> cast,
        int clipIndex,
        string primary)
    {
        // Full sticky list, importance-ordered. Primary subject first among cast.
        var bits = new List<string>();
        var orderedCast = cast
            .OrderBy(k => string.Equals(k, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in orderedCast)
        {
            if (!state.TryGetValue(key, out var items) || items.Count == 0) continue;
            var shown = PrioritizeWardrobeItems(items);
            if (shown.Count == 0) continue;
            bits.Add($"{key} still wears {string.Join(", ", shown)}");
        }
        if (bits.Count == 0) return "";
        return string.Join("; ", bits);
    }

    /// <summary>
    /// Order wardrobe phrases by continuity importance (signature / face-adjacent first,
    /// main garments, then accessories). Keeps all items — no artificial cap.
    /// Stable within rank so recent put-on (front of list) stays preferred when ranks tie.
    /// </summary>
    public static IReadOnlyList<string> PrioritizeWardrobeItems(IEnumerable<string>? items)
    {
        if (items is null) return Array.Empty<string>();
        var list = items
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count <= 1) return list;

        return list
            .Select((item, index) => (item, index, rank: WardrobeImportanceRank(item)))
            .OrderBy(t => t.rank)
            .ThenBy(t => t.index) // preserve relative order within rank
            .Select(t => t.item)
            .ToList();
    }

    /// <summary>0 = identity-critical, 1 = main garments, 2 = other.</summary>
    public static int WardrobeImportanceRank(string item)
    {
        var t = (item ?? "").ToLowerInvariant();
        if (t.Length == 0) return 9;
        // Face / silhouette / signature props — highest continuity value
        if (Regex.IsMatch(t,
                @"\b(hat|cap|bonnet|hood|wig|glasses|spectacles|monocle|mask|veil|" +
                @"badge|collar|leash|nightshirt|nightgown|robe|uniform|armor|" +
                @"scarf|cravat|tie|eyepatch)\b"))
            return 0;
        // Core clothing body
        if (Regex.IsMatch(t,
                @"\b(coat|cloak|jacket|dress|gown|suit|shirt|blouse|vest|waistcoat|" +
                @"trousers|pants|skirt|boots|shoes|slippers|pajamas|pyjamas|" +
                @"sweater|jumper|overalls|apron)\b"))
            return 1;
        return 2;
    }

    private static string WardrobeNegativeExtras(
        Dictionary<string, List<string>> state,
        List<string> cast)
    {
        // Soft negatives: avoid inventing extra props when wardrobe is known
        if (cast.Count == 0) return "";
        var hasWardrobe = cast.Any(c => state.TryGetValue(c, out var i) && i.Count > 0);
        return hasWardrobe ? "no extra unmentioned hats or jackets" : "";
    }

    private static string NegExtras(Dictionary<string, object?> beat)
    {
        var must = GetList(beat, "must_not").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(4);
        return string.Join(", ", must);
    }

    private static Dictionary<string, object?> MusicBed(Dictionary<string, object?> scene, int total)
    {
        var mi = scene.TryGetValue("music_intent", out var m) && m is Dictionary<string, object?> md
            ? md : new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["style_description"] =
                CoerceString(mi.TryGetValue("style_description", out var sd) ? sd : null)
                ?? "cinematic underscore",
            ["duration_seconds"] = total,
        };
    }

    private static void NormalizeCharPlaceholders(Dictionary<string, object?> charSeeds)
    {
        foreach (var (_, val) in charSeeds)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var ph = (CoerceString(seed.TryGetValue("reference_image_placeholder", out var p) ? p : null) ?? "")
                .Replace('\\', '/');
            if (ph.Contains('/') || ph.StartsWith("assets", StringComparison.OrdinalIgnoreCase))
                seed["reference_image_placeholder"] = Path.GetFileName(ph);
        }
    }

    private static string Stage1Fingerprint(Dictionary<string, object?> stage1)
    {
        var raw = JsonSerializer.Serialize(new
        {
            scenes = GetScenes(stage1).Select(s => new
            {
                n = s.TryGetValue("scene_number", out var sn) ? sn : null,
                b = GetList(s, "story_beats").Count,
                d = s.TryGetValue("duration_target_seconds", out var d) ? d : null,
            }),
            chars = GetDict(GetDict(stage1, "global_production_variables"), "character_seed_tokens").Keys.OrderBy(k => k),
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static HashSet<int>? ParseSceneRange(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) ||
            string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
            return null;
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ends = part.Split('-', 2);
                if (int.TryParse(ends[0], out var a) && int.TryParse(ends[1], out var b))
                {
                    for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                        set.Add(i);
                }
            }
            else if (int.TryParse(part, out var n))
                set.Add(n);
        }
        return set.Count == 0 ? null : set;
    }

    private static string FormatTs(int start, int end)
    {
        static string Fmt(int s) => $"{s / 60:D2}:{s % 60:D2}";
        return $"{Fmt(start)}-{Fmt(end)}";
    }

    public static List<Dictionary<string, object?>> GetScenes(Dictionary<string, object?> d) =>
        GetList(d, "scenes").OfType<Dictionary<string, object?>>().ToList();

    public static Dictionary<string, object?> GetDict(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is Dictionary<string, object?> x ? x : new();

    public static List<object?> GetList(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> list ? list : new();

    /// <summary>
    /// Ensure each scene's <c>characters_on_screen</c> is a superset of its clips' casts. A character
    /// on-screen in a clip is on-screen in the scene, so this is a safe deterministic heal for models
    /// that under-list the scene cast (prevents a spurious clip_cast_not_in_scene hard-fail).
    /// </summary>
    internal static void HealSceneCastFromClips(Dictionary<string, object?> plan)
    {
        foreach (var scene in GetList(plan, "scenes").OfType<Dictionary<string, object?>>())
        {
            var cast = GetList(scene, "characters_on_screen")
                .Select(x => x?.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            var seen = new HashSet<string>(cast, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var clip in GetList(scene, "veo_clips").OfType<Dictionary<string, object?>>())
            {
                foreach (var ch in GetList(clip, "characters_on_screen")
                             .Select(x => x?.ToString() ?? "")
                             .Where(s => s.Length > 0))
                {
                    if (seen.Add(ch)) { cast.Add(ch); changed = true; }
                }
            }
            if (changed)
                scene["characters_on_screen"] = cast.Cast<object?>().ToList();
        }
    }

    public static int ToInt(object? v) => v switch
    {
        null => 0, int i => i, long l => (int)l, double d => (int)d,
        string s when int.TryParse(s, out var n) => n, _ => 0,
    };

    private static string? CoerceString(object? v) => v switch
    {
        null => null, string s => s, _ => v.ToString(),
    };

    /// <summary>
    /// Catalog provider id for the project's video model. Empty when not yet selected —
    /// never invents "grok".
    /// </summary>
    private static string ResolveVideoProviderProfile(Dictionary<string, object?>? stage1)
    {
        // Prefer explicit stamp already on stage1 / plan if a prior step wrote a catalog id.
        if (stage1 is not null
            && stage1.TryGetValue("video_provider_profile", out var existing)
            && CoerceString(existing) is { Length: > 0 } prior
            && SupportedModelCatalog.IsKnownProviderId(prior))
        {
            return SupportedModelCatalog.NormalizeProviderId(prior);
        }

        if (stage1 is not null
            && stage1.TryGetValue("video_model", out var vmObj)
            && CoerceString(vmObj) is { Length: > 0 } videoModel)
        {
            var pid = SupportedModelCatalog.CatalogProviderId(videoModel, "video");
            if (!string.IsNullOrWhiteSpace(pid)) return pid;
        }

        return "";
    }
}

public sealed class Stage2PlanResult
{
    public bool Ok { get; set; }
    public string OutPath { get; set; } = "";
    public int SceneCount { get; set; }
    public int ClipCount { get; set; }
    public int DurationSeconds { get; set; }
}
