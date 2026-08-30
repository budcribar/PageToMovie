using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
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
        "bodies stay intact (no detached or separating heads, limbs or body parts), " +
        "blur/obscure environmental signage or screens, no name tags, no name badges, " +
        "no embroidered names, no lower thirds, no personal names on clothing or props";

    // Duration floors/caps live in ClipDurationEstimator (dialogue-aware, cost-sensitive)
    private const int GrokMinClip = ClipDurationEstimator.MinSeconds;
    private const int GrokMaxClip = ClipDurationEstimator.MaxSeconds;
    private const int GrokAbsMax = ClipDurationEstimator.AbsMaxSeconds;
    private const int GrokDefault = 6;
    private const int GrokSceneMin = 6;

    private static class Keys
    {
        public const string VisualEvent = "visual_event";
        public const string Stage2Meta = "stage2_meta";
        public const string DurationSeconds = "duration_seconds";
        public const string Delivery = "delivery";
        public const string Scenes = "scenes";
        public const string CharactersOnScreen = "characters_on_screen";
        public const string VeoClips = "veo_clips";
        public const string VideoProviderProfile = "video_provider_profile";
        public const string LocationId = "location_id";
        /// <summary>Which rule decided this clip's continuation — see <see cref="ExtendCutClassifier.CutDecisionRuleKey"/>.</summary>
        public const string ContinuityRule = "continuity_rule";
        public const string ActionClass = "action_class";
        public const string BigAction = "big_action";
        /// <summary>Marker set by beat coalescing: this beat is an action with a line spoken over it and must
        /// start its own clip — later dialogue coalescing must not absorb it into the previous speech beat
        /// (that concatenates the line onto the previous one and the action-with-its-line is lost).</summary>
        public const string OwnClip = "own_clip";
        public const string SecondarySpeaker = "secondary_speaker";
        public const string TertiarySpeaker = "tertiary_speaker";
        public const string SpokenOnCamera = "spoken_on_camera";
        public const string PrimarySubject = "primary_subject";
        public const string StoryBeats = "story_beats";
        public const string Setting = "setting";
        public const string GlobalProductionVariables = "global_production_variables";
        public const string CharacterSeedTokens = "character_seed_tokens";
        public const string SourceBookTitle = "source_book_title";
        public const string BlockingNotes = "blocking_notes";
    }

    // No design-time length budget — send full visual prompts.
    // If the video API rejects for length, GrokVideoClient shortens and retries.

    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectStore _projects;
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
    private readonly ContinuationActionClassifier? _continuationActionClassifier;
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
        ContinuationActionClassifier? continuationActionClassifier = null,
        GenerationErrorLogger? errorLog = null)
    {
        _projects = projects;
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
        _continuationActionClassifier = continuationActionClassifier;
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
        var maxSpeakersPerClip = ResolveMaxSpeakersPerClip(videoModelId, isExtendHop: false);
        var extendMaxSpeakersPerClip = ResolveMaxSpeakersPerClip(videoModelId, isExtendHop: true);

        // Fountain is the only screenplay source of truth.
        ScreenplayService.EnsureCanonicalDraft(_projects, projectId);
        var fountainPath = ScreenplayService.GetDraftPath(_projects, projectId);
        if (!File.Exists(fountainPath))
            throw new InvalidOperationException(
                "No screenplay draft. Create and approve a Fountain screenplay first.");

        var screenplay = ScreenplayService.Get(_projects, projectId);
        if (!screenplay.Status.Signed && screenplay.Status.DraftExists)
        {
            // Say what was compared: the Screenplay page can read "Approved" while this gate disagrees
            // (draft saved/normalised after sign-off, or a stale page) — the hashes settle it.
            var st = screenplay.Status;
            throw new InvalidOperationException(
                "Approve the screenplay before building a shot plan (draft has unapproved changes). " +
                $"draft {Short(st.DraftHash)} saved {st.DraftMtime ?? "?"} vs approved {Short(st.SignedHash)} at {st.SignedAt ?? "never"}. " +
                "Open Screenplay → Re-approve.");
        }

        onProgress?.Invoke($"Loading screenplay: {Path.GetFileName(fountainPath)}");
        var stage1 = ScreenplayService.BuildModelFromFountainText(screenplay.Text, durMinSeconds, durMaxSeconds, durAbsMaxSeconds);
        var sourceLabel = Path.GetFileName(fountainPath);

        // Overlay plate/voice edits from cast_seeds.json when present
        MergeCastSeedsOverlay(_projects, projectId, stage1);

        // Resolved before enrichment, not after: the Stage 1 classifiers only need the scenes about
        // to be replanned, and running them over the whole screenplay to plan one scene is pure
        // spend. Mary19 logged "extend vs hard-cut for 22 beat(s)" while planning a single scene.
        var want = ParseSceneRange(scenes);
        var scenesIn = FilterScenesToRange(GetScenes(stage1), want);

        if (scenesIn.Count == 0)
            throw new InvalidOperationException("Screenplay has no scenes to plan.");

        var (classifyMeta, enrichMeta) = await RunStage1EnrichmentsAsync(
                stage1, scenesIn, onProgress, ct, planningModel)
            .ConfigureAwait(false);

        var gpv = GetDict(stage1, Keys.GlobalProductionVariables);
        var charSeeds = GetDict(gpv, Keys.CharacterSeedTokens);
        NormalizeCharPlaceholders(charSeeds);

        onProgress?.Invoke($"Planning {scenesIn.Count} scene(s) @ {resolution}…");
        var vision = ProjectVisionMeta.RequireDecided(projectDir);
        var styleLock = vision.RenderStyleLock;
        var visualMedium = vision.VisualMedium;
        var targetAspectRatio = CoerceString(gpv.TryGetValue("target_aspect_ratio", out var tar) ? tar : null)
            ?? ProjectVisionMeta.DefaultAspectRatio(visualMedium);
        gpv[JsonKeys.RenderStyleLock] = styleLock;
        gpv[JsonKeys.VisualMedium] = visualMedium;

        var planned = await PlanScenesInParallelAsync(
                scenesIn, charSeeds, styleLock, targetAspectRatio, visualMedium,
                durMinSeconds, durMaxSeconds, durAbsMaxSeconds, durExtensionMaxSeconds, maxSpeakersPerClip,
                extendMaxSpeakersPerClip, planningModel, onProgress, ct)
            .ConfigureAwait(false);

        if (planned.Count == 0)
            throw new InvalidOperationException("Screenplay has no filmable scenes to plan.");

        var outPath = await _projects.FindBlueprintPathAsync(projectId, ct).ConfigureAwait(false)
            ?? Path.Combine(projectDir, "blueprint.clips.grok.json");
        BackupExistingBlueprint(outPath, onProgress);

        // Single source of truth for the auto-inserted credits scene's content (title + author + creator site).
        var creditsVisualPrompt = _projects.BuildCreditsVisualPrompt(projectId);

        var plan = await LoadOrBuildPlanAsync(
                want, outPath, planned, stage1, gpv, sourceLabel, resolution, scenes,
                classifyMeta, enrichMeta, creditsVisualPrompt, onProgress, ct)
            .ConfigureAwait(false);

        return await FinalizeAndWritePlanAsync(
                plan, stage1, projectId, projectDir, outPath, videoModelId, planningModel,
                sourceLabel, resolution, scenes, enrichMeta, operationTrace, onProgress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// AI enrichments (each: chat preferred → retry → heuristic fallback).
    /// All use the Settings Script & planning model — no host option invent defaults.
    /// </summary>
    /// <param name="scenesToEnrich">
    /// The scenes about to be replanned. Each classifier walks <c>scenes[]</c> and mutates the beat
    /// dictionaries in place, so they get a shallow view of <paramref name="stage1"/> whose scene
    /// list is narrowed to these — every other key (global production variables, cast seeds) is the
    /// same object, and the scene dictionaries are the same references, so the real beats still get
    /// the labels. Only the enrichment is scoped; <paramref name="stage1"/> itself stays whole,
    /// because dialogue coverage later checks the full screenplay against the merged plan.
    /// </param>
    /// <remarks>
    /// The counts in the returned meta describe the scenes enriched, not the whole screenplay —
    /// which is what a partial replan actually did.
    /// </remarks>
    private async Task<(SilentBeatClassifyResult? ClassifyMeta, Dictionary<string, object?> EnrichMeta)> RunStage1EnrichmentsAsync(
        Dictionary<string, object?> stage1,
        List<Dictionary<string, object?>> scenesToEnrich,
        Action<string>? onProgress,
        CancellationToken ct,
        string planningModel)
    {
        SilentBeatClassifyResult? classifyMeta = null;
        var enrichMeta = new Dictionary<string, object?>();
        stage1 = new Dictionary<string, object?>(stage1)
        {
            [Keys.Scenes] = scenesToEnrich.Cast<object?>().ToList(),
        };
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
        return (classifyMeta, enrichMeta);
    }

    private static List<Dictionary<string, object?>> FilterScenesToRange(
        List<Dictionary<string, object?>> scenes,
        HashSet<int>? want)
    {
        return scenes.Where(s => SceneMatchesRange(s, want)).ToList();
    }

    private static bool SceneMatchesRange(Dictionary<string, object?> scene, HashSet<int>? want)
    {
        if (want is null) return true;
        var n = ToInt(scene.TryGetValue(JsonKeys.SceneNumber, out var sn) ? sn : 0);
        return want.Contains(n);
    }

    /// <summary>
    /// Fan out scenes with a small concurrency cap. Within each scene the 9 classifiers
    /// already run via Task.WhenAll; without a scene-level cap that would be 9×N concurrent
    /// chat calls (provider throttling + noisy progress). Degree 2 ≈ 18 peak chat calls.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> PlanScenesInParallelAsync(
        List<Dictionary<string, object?>> scenesIn,
        Dictionary<string, object?> charSeeds,
        string? styleLock,
        string? targetAspectRatio,
        string? visualMedium,
        int durMinSeconds,
        int durMaxSeconds,
        int durAbsMaxSeconds,
        int durExtensionMaxSeconds,
        int maxSpeakersPerClip,
        int extendMaxSpeakersPerClip,
        string planningModel,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        const int maxParallelScenes = 2;
        var fanout = new SceneFanoutState
        {
            TotalScenes = scenesIn.Count,
            SceneGate = new SemaphoreSlim(maxParallelScenes),
            OnProgress = onProgress,
            PlannedBag = new System.Collections.Concurrent.ConcurrentBag<(int SceneNumber, Dictionary<string, object?> Scene)>(),
        };
        using (fanout.SceneGate)
        {
            var sceneTasks = scenesIn.Select(s => PlanOneSceneAsync(
                    s, charSeeds, styleLock, targetAspectRatio, visualMedium,
                    durMinSeconds, durMaxSeconds, durAbsMaxSeconds, durExtensionMaxSeconds, maxSpeakersPerClip,
                    extendMaxSpeakersPerClip, planningModel, fanout, ct))
                .ToArray();
            await Task.WhenAll(sceneTasks).ConfigureAwait(false);
        }

        return fanout.PlannedBag
            .OrderBy(x => x.SceneNumber)
            .Select(x => x.Scene)
            .ToList();
    }

    private async Task PlanOneSceneAsync(
        Dictionary<string, object?> s,
        Dictionary<string, object?> charSeeds,
        string? styleLock,
        string? targetAspectRatio,
        string? visualMedium,
        int durMinSeconds,
        int durMaxSeconds,
        int durAbsMaxSeconds,
        int durExtensionMaxSeconds,
        int maxSpeakersPerClip,
        int extendMaxSpeakersPerClip,
        string planningModel,
        SceneFanoutState fanout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(targetAspectRatio) && !s.ContainsKey("target_aspect_ratio"))
            s["target_aspect_ratio"] = targetAspectRatio;
        if (!string.IsNullOrWhiteSpace(visualMedium))
            s[JsonKeys.VisualMedium] = visualMedium;
        if (!string.IsNullOrWhiteSpace(styleLock))
            s[JsonKeys.RenderStyleLock] = styleLock;

        var sn = ToInt(s.TryGetValue(JsonKeys.SceneNumber, out var n) ? n : 0);
        await fanout.SceneGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            fanout.Report($"Scene {sn} of {fanout.TotalScenes}…");
            var tasks = StartSceneClassifierTasks(s, sn, durMaxSeconds, planningModel, fanout, ct);
            await Task.WhenAll(
                tasks.Pacing, tasks.Lighting, tasks.Camera, tasks.Negative, tasks.Wardrobe,
                tasks.Emotion, tasks.Sound, tasks.Dof, tasks.Color,
                tasks.ContinuationAction).ConfigureAwait(false);

            var plannedScene = PlanScene(
                s, charSeeds, styleLock,
                tasks.Pacing.Result, tasks.Lighting.Result, tasks.Camera.Result, tasks.Negative.Result,
                tasks.Wardrobe.Result, tasks.Emotion.Result, tasks.Sound.Result, tasks.Dof.Result, tasks.Color.Result,
                durMinSeconds, durMaxSeconds, durAbsMaxSeconds, durExtensionMaxSeconds, maxSpeakersPerClip,
                extendMaxSpeakersPerClip, tasks.ContinuationAction.Result);
            // Skip transition-only phantoms (e.g. FADE IN before first heading)
            if (plannedScene is null)
            {
                fanout.Report($"Scene {sn} of {fanout.TotalScenes}: skipped (no filmable content)");
                return;
            }
            if (_shotPlanRefiner is not null)
            {
                await _shotPlanRefiner.RefinePlannedSceneAsync(
                    plannedScene,
                    line => fanout.Report($"  Scene {sn}: {line}"),
                    ct,
                    model: planningModel).ConfigureAwait(false);
            }
            fanout.PlannedBag.Add((sn, plannedScene));
        }
        finally
        {
            var done = Interlocked.Increment(ref fanout.CompletedScenes);
            fanout.Report($"Planning scenes: {done}/{fanout.TotalScenes} complete");
            fanout.SceneGate.Release();
        }
    }

    /// <summary>
    /// All 9 read only from <c>s</c> / their own independently-built sceneBeats clone and
    /// return a fresh result — none mutate shared state — so they run concurrently
    /// instead of one round-trip at a time. Each classifier's underlying IChatClient
    /// sets auth per-request now (not on shared HttpClient.DefaultRequestHeaders), so
    /// this fan-out is safe there too.
    /// </summary>
    private SceneClassifierTasks StartSceneClassifierTasks(
        Dictionary<string, object?> s,
        int sn,
        int durMaxSeconds,
        string planningModel,
        SceneFanoutState fanout,
        CancellationToken ct)
    {
        Action<string> report = line => fanout.Report($"  Scene {sn}: {line}");
        var pacingTask = _beatPacingClassifier is not null
            ? _beatPacingClassifier.ClassifyScenePacingAsync(s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, int>?>(null);
        var lightingTask = _lightingClassifier is not null
            ? _lightingClassifier.ClassifySceneLightingAsync(s, report, ct, model: planningModel)
            : Task.FromResult<string?>(null);
        var cameraTask = _cameraClassifier is not null
            ? _cameraClassifier.ClassifySceneCameraAsync(s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, CameraDirective>?>(null);
        var negativeTask = _negativeClassifier is not null
            ? _negativeClassifier.ClassifySceneNegativeAsync(s, report, ct, model: planningModel)
            : Task.FromResult<string?>(null);
        var wardrobeTask = _wardrobeClassifier is not null
            ? _wardrobeClassifier.ClassifySceneWardrobeAsync(s, UnionCharactersOnScreen(s), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, string>?>(null);
        var emotionTask = _emotionClassifier is not null
            ? _emotionClassifier.ClassifySceneEmotionAsync(s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, EmotionDirective>?>(null);
        var soundTask = _soundComposerClassifier is not null
            ? _soundComposerClassifier.ClassifySceneSoundDesignAsync(s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, SoundDesignDirective>?>(null);
        var dofTask = _dofClassifier is not null
            ? _dofClassifier.ClassifySceneDepthOfFieldAsync(s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, DepthOfFieldDirective>?>(null);
        var colorTask = _colorGradingClassifier is not null
            ? _colorGradingClassifier.ClassifySceneColorGradingAsync(s, report, ct, model: planningModel)
            : Task.FromResult<ColorGradingDirective?>(null);
        // Only continuation beats go out — the classifier filters them itself, and a scene with
        // none makes no call at all.
        var continuationActionTask = _continuationActionClassifier is not null
            ? _continuationActionClassifier.ClassifySceneContinuationActionsAsync(
                s, BuildSceneBeats(s, durMaxSeconds), report, ct, model: planningModel)
            : Task.FromResult<Dictionary<string, string>?>(null);
        return new SceneClassifierTasks(
            pacingTask, lightingTask, cameraTask, negativeTask, wardrobeTask,
            emotionTask, soundTask, dofTask, colorTask, continuationActionTask);
    }

    private static void BackupExistingBlueprint(string outPath, Action<string>? onProgress)
    {
        if (!File.Exists(outPath))
            return;
        var bak = outPath + $".bak_pre_stage2_{DateTime.Now:yyyyMMdd_HHmmss}";
        File.Copy(outPath, bak, overwrite: true);
        onProgress?.Invoke($"Backed up blueprint → {Path.GetFileName(bak)}");
    }

    private static async Task<Dictionary<string, object?>> LoadOrBuildPlanAsync(
        HashSet<int>? want,
        string outPath,
        List<Dictionary<string, object?>> planned,
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        string sourceLabel,
        string resolution,
        string scenes,
        SilentBeatClassifyResult? classifyMeta,
        Dictionary<string, object?> enrichMeta,
        string? creditsVisualPrompt,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (want is not null && File.Exists(outPath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false);
                var existing = GrokChatClient.ParseJsonObject(existingText);
                var merged = MergePlannedScenes(existing, planned, stage1, gpv, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
                onProgress?.Invoke("Merged planned scenes into existing blueprint");
                return merged;
            }
            catch
            {
                return BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
            }
        }

        return BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes, classifyMeta, enrichMeta, creditsVisualPrompt);
    }

    private async Task<Stage2PlanResult> FinalizeAndWritePlanAsync(
        Dictionary<string, object?> plan,
        Dictionary<string, object?> stage1,
        string projectId,
        string projectDir,
        string outPath,
        string videoModelId,
        string planningModel,
        string sourceLabel,
        string resolution,
        string scenes,
        Dictionary<string, object?> enrichMeta,
        ModelOperationTraceScope operationTrace,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Heal: a character on-screen in a clip is by definition on-screen in the scene. Union each
        // clip's cast into its scene cast so a model that under-listed the scene cast (e.g. omitting
        // the lead) does not hard-fail the clip⊆scene validation below.
        HealSceneCastFromClips(plan);

        // Middle-layer guard: every spoken line in the approved screenplay must survive planning into
        // some clip's audio_payload. Record coverage in stage2_meta (always), surface any drop as a
        // plan issue, and log it to generation_errors so we can trace which transform silenced it.
        var coverage = Stage2DialogueCoverage.Verify(stage1, plan);
        GetDict(plan, Keys.Stage2Meta)["dialogue_coverage"] = coverage.Meta;
        if (coverage.HasGaps)
        {
            onProgress?.Invoke(
                $"Dialogue coverage: {coverage.CoveredLines}/{coverage.ExpectedLines} screenplay lines reach a clip " +
                $"({coverage.Gaps.Count} not spoken in the shot plan).");
            await LogDialogueCoverageGapsAsync(coverage, videoModelId, planningModel, ct).ConfigureAwait(false);
        }

        var planIssues = StructuredOperationArtifacts.RequireJsonProperties(plan, Keys.Stage2Meta, Keys.Scenes)
            .Concat(Stage2AggregateValidator.Validate(plan))
            .Concat(coverage.Issues)
            .ToArray();
        var classifierProvenance = Stage2AggregateValidator.BuildClassifierProvenance(enrichMeta);
        await StructuredOperationArtifacts.WriteAsync(
            projectDir, "stage2_shot_plan", videoModelId,
            new { projectId, sourceLabel, resolution, scenes }, plan, planIssues, ct).ConfigureAwait(false);
        await Stage2AggregateValidator.WriteManifestAsync(
            projectDir, classifierProvenance, operationTrace.Snapshot(), planIssues, ct).ConfigureAwait(false);
        if (planIssues.Any(i => i.Severity == ModelValidationSeverity.Error))
            throw new InvalidOperationException(string.Join(" ", planIssues.Select(i => i.Message)));

        // Fail loud at the source if the plan has duplicate clip numbers in a scene — that doubles the
        // scene downstream (the stitch concatenates one file per veo_clips entry). Throwing here catches
        // the bug during generation, before any video spend, and never touches an already-saved movie.
        var planJson = JsonSerializer.Serialize(plan, JsonWrite);
        ThrowIfDuplicateClipNumbers(planJson);

        await File.WriteAllTextAsync(outPath, planJson + "\n", ct).ConfigureAwait(false);
        var meta = GetDict(plan, Keys.Stage2Meta);
        var totalClips = ToInt(meta.TryGetValue("total_clips", out var tc) ? tc : 0);
        var sceneCount = GetList(plan, Keys.Scenes).Count;
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

    private static void ThrowIfDuplicateClipNumbers(string planJson)
    {
        using var planDoc = System.Text.Json.JsonDocument.Parse(planJson);
        if (BlueprintClipValidation.DescribeDuplicates(planDoc.RootElement) is { } dupDesc)
            throw new InvalidOperationException(
                "Shot plan has duplicate clip numbers (each duplicate would double its scene): " + dupDesc);
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
            [JsonKeys.MovieTitle] = stage1.TryGetValue(JsonKeys.MovieTitle, out var mt) ? mt : null,
            [Keys.SourceBookTitle] = stage1.TryGetValue(Keys.SourceBookTitle, out var sbt) ? sbt : null,
            [Keys.VideoProviderProfile] = ResolveVideoProviderProfile(stage1),
            [Keys.GlobalProductionVariables] = gpv,
            [Keys.Scenes] = planned.Cast<object?>().ToList(),
            [Keys.Stage2Meta] = MakeMeta(stage1, planned, sourceLabel, resolution, scenesFilter, classifyMeta, enrichMeta),
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
        AbsorbScenesByNumber(byN, GetList(existing, Keys.Scenes).OfType<Dictionary<string, object?>>());
        AbsorbScenesByNumber(byN, planned);
        var all = byN.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        EnsureEndCreditsScene(all, creditsVisualPrompt);
        existing["schema_version"] = "stage2.v1";
        existing[JsonKeys.MovieTitle] = CoalesceStage1OrExisting(stage1, existing, JsonKeys.MovieTitle);
        existing[Keys.SourceBookTitle] = CoalesceStage1OrExisting(stage1, existing, Keys.SourceBookTitle);
        existing[Keys.VideoProviderProfile] = ResolveVideoProviderProfile(stage1);
        existing[Keys.GlobalProductionVariables] = gpv;
        existing[Keys.Scenes] = all.Cast<object?>().ToList();
        existing[Keys.Stage2Meta] = MakeMeta(stage1, all, sourceLabel, resolution, scenesFilter, classifyMeta, enrichMeta);
        return existing;
    }

    private static void AbsorbScenesByNumber(
        Dictionary<int, Dictionary<string, object?>> byN,
        IEnumerable<Dictionary<string, object?>> scenes)
    {
        foreach (var s in scenes)
        {
            var n = ToInt(s.TryGetValue(JsonKeys.SceneNumber, out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
    }

    private static object? CoalesceStage1OrExisting(
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> existing,
        string key)
    {
        if (stage1.TryGetValue(key, out var fromStage1))
            return fromStage1;
        if (existing.TryGetValue(key, out var fromExisting))
            return fromExisting;
        return null;
    }

    public static void EnsureEndCreditsScene(List<Dictionary<string, object?>> scenes, string? creditsVisualPrompt = null)
    {
        if (scenes == null || scenes.Count == 0) return;

        // Dedupe via the single credits predicate (ProjectStore.IsCreditsScene) so a re-plan never
        // appends a second credits card — including when the existing credits scene is only marked by
        // its clip-level is_credits flag (older auto-inserts) rather than a heading/setting.
        if (scenes.Any(ProjectStore.IsCreditsScene))
            return;

        var maxSn = scenes.Select(s => ToInt(s.TryGetValue(JsonKeys.SceneNumber, out var sn) ? sn : 0)).DefaultIfEmpty(0).Max();
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
            [JsonKeys.ClipNumber] = 1,
            ["clip_index"] = 1,
            ["timestamp"] = "",
            ["veo_continuation_source"] = "none",
            [Keys.PrimarySubject] = "End Credits Title Card",
            [Keys.CharactersOnScreen] = new List<object?>(),
            ["focus_keys"] = new List<object?>(),
            ["action_summary"] = "Scrolling film end credits and attribution title card.",
            [Keys.DurationSeconds] = 6,
            ["is_credits"] = true,
            ["visual_prompt"] = visualPrompt,
            [JsonKeys.AudioPayload] = new Dictionary<string, object?>
            {
                [Keys.Delivery] = "none",
                [JsonKeys.Speaker] = "",
                [JsonKeys.Dialogue] = "",
            },
        };

        var creditsScene = new Dictionary<string, object?>
        {
            [JsonKeys.SceneNumber] = creditsSceneNumber,
            ["scene_heading"] = "FADE OUT. END CREDITS",
            ["is_credits"] = true,
            ["total_estimated_duration_seconds"] = 6,
            [Keys.VeoClips] = new List<object?> { creditsClip },
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
            ["total_clips"] = planned.Sum(s => GetList(s, Keys.VeoClips).Count),
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
            var overlaySeeds = GetDict(overlay, Keys.CharacterSeedTokens);
            if (overlaySeeds.Count == 0)
                overlaySeeds = GetDict(GetDict(overlay, Keys.GlobalProductionVariables), Keys.CharacterSeedTokens);
            if (overlaySeeds.Count == 0)
                return;

            var gpv = GetDict(stage1, Keys.GlobalProductionVariables);
            var seeds = GetDict(gpv, Keys.CharacterSeedTokens);
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
            gpv[Keys.CharacterSeedTokens] = seeds;
            stage1[Keys.GlobalProductionVariables] = gpv;
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
        if (s.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[JsonKeys.CharacterPrefix.Length..];
        if (s.StartsWith("The_", StringComparison.OrdinalIgnoreCase))
            s = s["The_".Length..];
        s = s.Replace("_", "");
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Build one scene’s clip plan. Returns null when the scene has nothing filmable
    /// (transition-only / phantom unspecified), so callers can omit it.
    /// </summary>
    internal static Dictionary<string, object?>? PlanScene(
        Dictionary<string, object?> scene,
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
        int maxSpeakersPerClip = 1,
        int? extendMaxSpeakersPerClip = null,
        Dictionary<string, string>? aiContinuationActions = null)
    {
        var effectiveExtensionMax = extensionMaxSeconds ?? maxSeconds;
        var sceneInput = new Dictionary<string, object?>(scene);
        if (!string.IsNullOrWhiteSpace(aiLighting))
        {
            sceneInput["lighting_continuity_token"] = aiLighting;
        }
        var beats = GetList(sceneInput, Keys.StoryBeats).OfType<Dictionary<string, object?>>()
            .Where(b => !IsNoopTransitionBeat(b))
            .ToList();
        // Moved ahead of coalescing (was computed after) — PrecomputeExtendsFromPrevious needs the
        // same location fallback chain the final per-clip loop uses, and this is scene-only data
        // that doesn't depend on the beat list.
        var lids = CollectLocationIds(scene);
        var primary = ResolvePrimaryLocation(scene, lids);

        beats = ApplyBeatCoalescing(
            beats, primary, lids, maxSeconds, effectiveExtensionMax, maxSpeakersPerClip,
            extendMaxSpeakersPerClip ?? maxSpeakersPerClip);
        var cast = UnionCharactersOnScreen(scene);

        // Entire scene was only FADE IN / CUT TO — omit (no empty clip)
        var setting = CoerceString(scene.TryGetValue(Keys.Setting, out var set) ? set : null) ?? "";
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

        ApplyAiPacingOverrides(beats, durs, aiPacing);
        var total = durs.Sum();

        var sceneWork = new Dictionary<string, object?>(sceneInput)
        {
            [Keys.CharactersOnScreen] = cast.Cast<object?>().ToList(),
        };
        if (!string.IsNullOrWhiteSpace(styleLock))
            sceneWork[JsonKeys.RenderStyleLock] = styleLock;

        var wardrobe = InitWardrobeState(cast, charSeeds, scene);
        ApplyAiWardrobeOverrides(wardrobe, aiWardrobe);
        var clips = new List<object?>();
        var beatMap = new List<object?>();
        var t = 0;
        string? prevLid = null;
        Dictionary<string, object?>? prevBeat = null;
        string? prevCamera = null;

        int monologueStep = 0;
        string? activeSpeaker = null;

        for (var i = 0; i < beats.Count; i++)
        {
            var planned = PlanSingleClip(
                beats[i], i, durs[i], t, sceneWork, charSeeds, wardrobe, lids, primary,
                prevBeat, prevLid, activeSpeaker, monologueStep, prevCamera,
                aiLighting, aiNegative, aiCamera, aiEmotion, aiSound, aiDof, aiColor,
                aiContinuationActions);
            clips.Add(planned.Clip);
            beatMap.Add(planned.BeatId);
            t += planned.Duration;
            prevLid = planned.LocationId;
            prevBeat = beats[i];
            activeSpeaker = planned.ActiveSpeaker;
            monologueStep = planned.MonologueStep;
            prevCamera = CameraTagWriter.ReadCameraTag(CoerceString(planned.Clip.TryGetValue("visual_prompt", out var vpObj) ? vpObj : null));
        }

        return BaseSceneShell(sceneWork, lids, primary, cast, total, clips, beatMap);
    }

    private static List<string> CollectLocationIds(Dictionary<string, object?> scene) =>
        GetList(scene, "location_ids").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();

    private static string? ResolvePrimaryLocation(Dictionary<string, object?> scene, List<string> lids) =>
        CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null)
        ?? (lids.Count > 0 ? lids[0] : null);

    /// <summary>
    /// Idempotent: monologues already split at fountain import stay; legacy long cues expand here.
    /// Extends-from-previous flags are recomputed fresh before each coalescing pass (not carried
    /// through) since the beat list's indices shift as beats get merged. See
    /// <see cref="PrecomputeExtendsFromPrevious"/> for why using each merge group's first beat
    /// is equivalent to the final per-clip ForceNone decision.
    /// Two-hander coalescing only when the video model can render >=2 speakers in one clip.
    /// At 1 each dialogue beat stays its own clip — one speaker per clip, shot-reverse-shot,
    /// which gives the cleanest lip-sync and avoids face morphing.
    /// </summary>
    private static List<Dictionary<string, object?>> ApplyBeatCoalescing(
        List<Dictionary<string, object?>> beats,
        string? primary,
        List<string> lids,
        int maxSeconds,
        int effectiveExtensionMax,
        int maxSpeakersPerClip,
        int extendMaxSpeakersPerClip)
    {
        beats = ClipDurationEstimator.ExpandLongDialogueBeats(beats, modelMaxSeconds: maxSeconds);
        beats = CoalesceSilentPreludeBeats(beats);
        beats = CoalesceDuplicateActionVoBeats(beats);
        beats = CoalesceShortMonologueBeats(
            beats, maxSeconds, effectiveExtensionMax, PrecomputeExtendsFromPrevious(beats, primary, lids));
        return ApplyCrossSpeakerCoalescing(
            beats, maxSpeakersPerClip, maxSeconds, effectiveExtensionMax,
            PrecomputeExtendsFromPrevious(beats, primary, lids),
            extendMaxSpeakersPerClip);
    }

    private static void ApplyAiPacingOverrides(
        List<Dictionary<string, object?>> beats,
        List<int> durs,
        Dictionary<string, int>? aiPacing)
    {
        if (aiPacing is null || aiPacing.Count == 0)
            return;
        for (var i = 0; i < beats.Count; i++)
        {
            var bid = ReadBeatString(beats[i], "beat_id") ?? $"b{i + 1}";
            if (aiPacing.TryGetValue(bid, out var customDur))
                durs[i] = customDur;
        }
    }

    private static void ApplyAiWardrobeOverrides(
        Dictionary<string, List<string>> wardrobe,
        Dictionary<string, string>? aiWardrobe)
    {
        if (aiWardrobe is null || aiWardrobe.Count == 0)
            return;
        foreach (var (k, v) in aiWardrobe)
        {
            if (!string.IsNullOrWhiteSpace(v))
                wardrobe[k] = new List<string> { v };
        }
    }

    private static (string? ActiveSpeaker, int MonologueStep) UpdateMonologueTracking(
        string? dlg,
        string? spk,
        string? activeSpeaker,
        int monologueStep)
    {
        if (string.IsNullOrWhiteSpace(dlg) || string.IsNullOrWhiteSpace(spk))
        {
            return (null, 0);
        }

        if (string.Equals(activeSpeaker, spk, StringComparison.OrdinalIgnoreCase))
            return (activeSpeaker, monologueStep + 1);

        return (spk, 0);
    }

    private static string ResolveClipContinuation(
        Dictionary<string, object?> beat,
        int i,
        Dictionary<string, object?>? prevBeat,
        string? prevLid,
        string? lid)
    {
        var cont = ForceNone(beat, i, prevBeat, prevLid, lid) ? "none" : "extend_previous";
        if (string.Equals(ReadBeatString(beat, Keys.ActionClass),
                Keys.BigAction, StringComparison.OrdinalIgnoreCase))
            cont = "none";
        if (prevLid is not null && lid is not null && prevLid != lid)
            cont = "none";
        return cont;
    }

    /// <summary>
    /// The action-only rewrite for a continuation clip, or null to use the beat's own action.
    /// </summary>
    private static string? ResolveContinuationAction(
        Dictionary<string, object?> beat, string cont, Dictionary<string, string>? rewrites)
    {
        if (rewrites is null || rewrites.Count == 0)
            return null;
        if (!string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase))
            return null;
        var beatId = ReadBeatString(beat, "beat_id");
        if (string.IsNullOrWhiteSpace(beatId) || !rewrites.TryGetValue(beatId, out var rewritten))
            return null;
        return string.IsNullOrWhiteSpace(rewritten) ? null : rewritten;
    }

    private static void EnsurePrimaryInClipCast(List<string> clipCast, string ps)
    {
        if (ps.StartsWith(JsonKeys.CharacterPrefix, StringComparison.Ordinal) && !clipCast.Contains(ps))
            clipCast.Insert(0, ps);
    }

    private static PlannedClip PlanSingleClip(
        Dictionary<string, object?> beat,
        int i,
        int dur,
        int t,
        Dictionary<string, object?> sceneWork,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, List<string>> wardrobe,
        List<string> lids,
        string? primary,
        Dictionary<string, object?>? prevBeat,
        string? prevLid,
        string? activeSpeaker,
        int monologueStep,
        string? previousCamera,
        string? aiLighting,
        string? aiNegative,
        Dictionary<string, CameraDirective>? aiCamera,
        Dictionary<string, EmotionDirective>? aiEmotion,
        Dictionary<string, SoundDesignDirective>? aiSound,
        Dictionary<string, DepthOfFieldDirective>? aiDof,
        ColorGradingDirective? aiColor,
        Dictionary<string, string>? aiContinuationActions = null)
    {
        var lid = ResolveBeatLocation(beat, primary, lids);
        var cont = ResolveClipContinuation(beat, i, prevBeat, prevLid, lid);
        var clipCast = ClipCastTokens(sceneWork, beat, charSeeds);
        var ps = ReadBeatString(beat, Keys.PrimarySubject) ?? "";
        EnsurePrimaryInClipCast(clipCast, ps);

        UpdateWardrobeFromBeat(wardrobe, beat, clipCast);

        var dlg = ReadBeatString(beat, JsonKeys.Dialogue);
        var spk = ReadBeatString(beat, JsonKeys.Speaker);
        (activeSpeaker, monologueStep) = UpdateMonologueTracking(dlg, spk, activeSpeaker, monologueStep);

        // Continuity + resolution/fps are owned by ClipVideoPromptBuilder at gen time —
        // keep blueprint visual_prompt declarative (action/style only).
        // Applied only when this clip really ends up continuing. The classifier judges beats, but
        // ResolveClipContinuation can still force a cut afterwards (big_action, location change) —
        // and a fresh shot needs its placement, because staging the shot is what it is for.
        var continuationAction = ResolveContinuationAction(beat, cont, aiContinuationActions);
        var vp = BuildVisualPrompt(beat, sceneWork, charSeeds, wardrobe, continuationAction);
        var (vpOut, neg, cameraMoveToken, beatIdStr, sourceBeatIds) = AppendVisualDirectives(
            vp, beat, wardrobe, clipCast, i, dlg, spk, monologueStep, previousCamera, charSeeds,
            aiLighting, aiNegative, aiCamera, aiEmotion, aiDof, aiColor);
        vp = vpOut;

        var actionClassVal = beat.TryGetValue(Keys.ActionClass, out var beatAc) ? beatAc : null;
        var primaryVal = beat.TryGetValue(Keys.PrimarySubject, out var psub) ? psub : null;
        var audioPayload = BuildAudioPayload(beat, LookupSoundDesign(aiSound, beatIdStr));
        var speakerForFocus = ReadBeatString(beat, JsonKeys.Speaker);
        var secondarySpeakerForFocus = ReadBeatString(beat, Keys.SecondarySpeaker);
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
            [JsonKeys.ClipNumber] = i + 1,
            ["timestamp"] = FormatTs(t, t + dur),
            ["veo_continuation_source"] = cont,
            // Which rule decided that continuation. Carried onto the clip so ShotPlanLint can tell
            // a plan whose extends were checked against the previous clip's staging from one built
            // before that test existed — the latter is what put a subject on the far side of the
            // room in an extend that was told to pick up from the previous last frame.
            [Keys.ContinuityRule] = ReadBeatString(beat, ExtendCutClassifier.CutDecisionRuleKey)
                                    ?? ExtendCutClassifier.HeuristicRule,
            [Keys.LocationId] = lid,
            ["visual_prompt"] = vp,
            ["negative_prompt"] = neg,
            [JsonKeys.AudioPayload] = audioPayload,
            ["stage1_beat_id"] = beatIdStr,
            ["stage1_beat_ids"] = sourceBeatIds.Cast<object?>().ToList(),
            [Keys.PrimarySubject] = primaryVal,
            // Propagate for gen-time duration (EstimateForClip) — silent big_action etc.
            [Keys.ActionClass] = actionClassVal,
            [Keys.CharactersOnScreen] = clipCast.Cast<object?>().ToList(),
            // Full identity lock at gen; others on-screen get compact "also present" lines
            ["focus_keys"] = focusKeys.Cast<object?>().ToList(),
            [Keys.DurationSeconds] = dur,
        };

        ApplyClipClassifierFields(clipDict, beatIdStr, cameraMoveToken, aiColor, aiDof, aiEmotion, aiCamera);
        return new PlannedClip(clipDict, beatIdStr, dur, lid, activeSpeaker, monologueStep);
    }

    private static SoundDesignDirective? LookupSoundDesign(
        Dictionary<string, SoundDesignDirective>? aiSound, string beatIdStr) =>
        aiSound is not null && aiSound.TryGetValue(beatIdStr, out var sd) ? sd : null;

    /// <summary>
    /// AI cinematic lighting/mood token (locks lighting style across the scene's shots) —
    /// previously computed by CinematicLightingClassifier and stored on the scene as
    /// lighting_continuity_token, but never appended to any clip's visual_prompt, so it
    /// never reached the actual video-generation call. Appended here the same way camera/
    /// performance/optics/color directives already are.
    /// Story-specific negatives only; provider global negatives applied at gen time.
    /// </summary>
    private static (string Vp, string Neg, string? CameraMoveToken, string BeatIdStr, List<string> SourceBeatIds)
        AppendVisualDirectives(
            string vp,
            Dictionary<string, object?> beat,
            Dictionary<string, List<string>> wardrobe,
            List<string> clipCast,
            int i,
            string? dlg,
            string? spk,
            int monologueStep,
            string? previousCamera,
            Dictionary<string, object?> charSeeds,
            string? aiLighting,
            string? aiNegative,
            Dictionary<string, CameraDirective>? aiCamera,
            Dictionary<string, EmotionDirective>? aiEmotion,
            Dictionary<string, DepthOfFieldDirective>? aiDof,
            ColorGradingDirective? aiColor)
    {
        if (!string.IsNullOrWhiteSpace(aiLighting))
            vp = $"{vp} {PromptTags.Wrap("Lighting", PromptTags.SanitizeValue(aiLighting))}";

        var neg = CombineNegative(BuildStoryNegativePrompt(beat, wardrobe, clipCast), aiNegative);
        var beatIdStr = ReadBeatString(beat, "beat_id") ?? $"b{i + 1}";
        var sourceBeatIds = PageToMovie.Core.Utils.StableBeatId.CollectIds(beat);
        if (sourceBeatIds.Count == 0 && !string.IsNullOrWhiteSpace(beatIdStr))
            sourceBeatIds.Add(beatIdStr);

        string? cameraMoveToken;
        (vp, cameraMoveToken) = ApplyCameraDirective(
            vp, beat, aiCamera, beatIdStr, dlg, spk, monologueStep, previousCamera, clipCast, charSeeds);
        vp = ApplyEmotionDofColorDirectives(vp, aiEmotion, aiDof, aiColor, beatIdStr);
        return (vp, neg, cameraMoveToken, beatIdStr, sourceBeatIds);
    }

    private static string CombineNegative(string neg, string? aiNegative)
    {
        if (string.IsNullOrWhiteSpace(aiNegative))
            return neg;
        return string.IsNullOrWhiteSpace(neg) ? aiNegative : $"{neg}, {aiNegative}";
    }

    private static (string Vp, string? CameraMoveToken) ApplyCameraDirective(
        string vp,
        Dictionary<string, object?> beat,
        Dictionary<string, CameraDirective>? aiCamera,
        string beatIdStr,
        string? dlg,
        string? spk,
        int monologueStep,
        string? previousCamera,
        List<string> clipCast,
        Dictionary<string, object?> charSeeds)
    {
        CameraDirective? camDir = null;
        if (aiCamera is not null)
            aiCamera.TryGetValue(beatIdStr, out camDir);

        var actionAndBlocking = BeatActionAndBlocking(beat);
        var sameSpeakerRun = monologueStep > 0;
        var visualCastCount = clipCast.Count(t => !IsNeverOnScreenCharacter(t, charSeeds));
        var framing = CameraTagWriter.Resolve(
            camDir,
            actionAndBlocking,
            previousCamera,
            sameSpeakerRun,
            hasSpeech: !string.IsNullOrWhiteSpace(dlg),
            onScreenCastCount: visualCastCount);
        if (!string.IsNullOrWhiteSpace(framing))
            vp = $"{vp} {PromptTags.Wrap(PromptFieldTags.Camera, PromptTags.SanitizeValue(framing))}";

        string? cameraMoveToken = null;
        if (camDir is not null)
            cameraMoveToken = $"{camDir.LensSpec}, {camDir.CameraMovement}";
        return (vp, cameraMoveToken);
    }

    private static string BeatActionAndBlocking(Dictionary<string, object?> beat)
    {
        var ve = CoerceString(beat.TryGetValue(Keys.VisualEvent, out var vev) ? vev : null) ?? "";
        var block = CoerceString(beat.TryGetValue(Keys.BlockingNotes, out var bn) ? bn : null) ?? "";
        if (string.IsNullOrWhiteSpace(block) ||
            ve.Contains(block, StringComparison.OrdinalIgnoreCase))
            return ve;
        return $"{ve}. {block}".Trim();
    }

    private static string ApplyEmotionDofColorDirectives(
        string vp,
        Dictionary<string, EmotionDirective>? aiEmotion,
        Dictionary<string, DepthOfFieldDirective>? aiDof,
        ColorGradingDirective? aiColor,
        string beatIdStr)
    {
        if (aiEmotion is not null && aiEmotion.TryGetValue(beatIdStr, out var emoDir) &&
            !string.IsNullOrWhiteSpace(emoDir.ActingPrompt))
        {
            vp = $"{vp} {PromptTags.Wrap("Performance", PromptTags.SanitizeValue(emoDir.ActingPrompt))}";
        }

        if (aiDof is not null && aiDof.TryGetValue(beatIdStr, out var dofDir))
        {
            var fstop = DepthOfFieldClassifier.SanitizeAperture(dofDir.Aperture);
            if (!string.IsNullOrWhiteSpace(fstop))
                vp = $"{vp} {PromptTags.Wrap(PromptFieldTags.Optics, PromptTags.SanitizeValue(fstop))}";
        }

        if (aiColor is not null && !string.IsNullOrWhiteSpace(aiColor.GradingPrompt))
        {
            // The classifier is told to return the look only — the tag names the field. This strip
            // stays as a belt-and-braces guard: it is the one slot whose text a model writes, and
            // an older cached directive (or a model that ignores the instruction) can still arrive
            // with the label attached.
            var grade = ColorPaletteGradingClassifier.StripGradeLabel(aiColor.GradingPrompt);
            if (grade.Length > 0)
                vp = $"{vp} {PromptTags.Wrap(PromptFieldTags.Grade, PromptTags.SanitizeValue(grade))}";
        }

        return vp;
    }

    private static void ApplyClipClassifierFields(
        Dictionary<string, object?> clipDict,
        string beatIdStr,
        string? cameraMoveToken,
        ColorGradingDirective? aiColor,
        Dictionary<string, DepthOfFieldDirective>? aiDof,
        Dictionary<string, EmotionDirective>? aiEmotion,
        Dictionary<string, CameraDirective>? aiCamera)
    {
        ApplyColorClipFields(clipDict, aiColor);
        ApplyDofClipFields(clipDict, aiDof, beatIdStr);
        ApplyEmotionClipFields(clipDict, aiEmotion, beatIdStr);
        ApplyCameraClipFields(clipDict, aiCamera, beatIdStr, cameraMoveToken);
    }

    private static void ApplyColorClipFields(Dictionary<string, object?> clipDict, ColorGradingDirective? aiColor)
    {
        if (aiColor is null)
            return;
        if (!string.IsNullOrWhiteSpace(aiColor.FilmStock))
            clipDict["film_stock"] = aiColor.FilmStock;
        if (!string.IsNullOrWhiteSpace(aiColor.ColorPalette))
            clipDict["color_palette"] = aiColor.ColorPalette;
    }

    private static void ApplyDofClipFields(
        Dictionary<string, object?> clipDict,
        Dictionary<string, DepthOfFieldDirective>? aiDof,
        string beatIdStr)
    {
        if (aiDof is null || !aiDof.TryGetValue(beatIdStr, out var dfd))
            return;
        clipDict["aperture"] = DepthOfFieldClassifier.SanitizeAperture(dfd.Aperture);
        clipDict["focal_plane"] = dfd.FocalPlane;
        if (!string.IsNullOrWhiteSpace(dfd.RackFocus))
            clipDict["rack_focus"] = dfd.RackFocus;
    }

    private static void ApplyEmotionClipFields(
        Dictionary<string, object?> clipDict,
        Dictionary<string, EmotionDirective>? aiEmotion,
        string beatIdStr)
    {
        if (aiEmotion is null || !aiEmotion.TryGetValue(beatIdStr, out var ed))
            return;
        clipDict["acting_intensity"] = ed.Intensity;
        if (!string.IsNullOrWhiteSpace(ed.MicroExpression))
            clipDict["micro_expression"] = ed.MicroExpression;
    }

    private static void ApplyCameraClipFields(
        Dictionary<string, object?> clipDict,
        Dictionary<string, CameraDirective>? aiCamera,
        string beatIdStr,
        string? cameraMoveToken)
    {
        if (aiCamera is null || !aiCamera.TryGetValue(beatIdStr, out var cd))
            return;
        clipDict["shot_scale_hint"] = cd.ShotScale;
        if (!string.IsNullOrWhiteSpace(cameraMoveToken))
            clipDict["camera_movement_token"] = cameraMoveToken;
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
        [JsonKeys.SceneNumber] = SceneValue(scene, JsonKeys.SceneNumber),
        [Keys.Setting] = SceneValue(scene, Keys.Setting),
        ["location_ids"] = lids.Cast<object?>().ToList(),
        ["primary_location_id"] = primary,
        [Keys.CharactersOnScreen] = cast.Cast<object?>().ToList(),
        ["scene_filename"] = SceneValue(scene, "scene_filename"),
        ["transition_type"] = CoerceString(SceneValue(scene, "transition_type")) ?? "cut",
        ["lighting_continuity_token"] =
            CoerceString(SceneValue(scene, "lighting_continuity_token")) ?? "",
        ["total_estimated_duration_seconds"] = total,
        ["music_bed"] = MusicBed(scene, total),
        [Keys.VeoClips] = clips,
        ["stage1_scene_number"] = SceneValue(scene, JsonKeys.SceneNumber),
        ["stage1_beat_map"] = beatMap,
        [Keys.VideoProviderProfile] = ResolveVideoProviderProfile(null),
        ["spoiler_constraints"] = SceneValueOrList(scene, "spoiler_constraints"),
        ["source_book_refs"] = SceneValueOrList(scene, "source_book_refs"),
        [JsonKeys.VisualMedium] = SceneValue(scene, JsonKeys.VisualMedium),
        [JsonKeys.RenderStyleLock] = SceneValue(scene, JsonKeys.RenderStyleLock),
    };

    private static object? SceneValue(Dictionary<string, object?> scene, string key) =>
        scene.TryGetValue(key, out var v) ? v : null;

    private static object? SceneValueOrList(Dictionary<string, object?> scene, string key) =>
        scene.TryGetValue(key, out var v) ? v : new List<object?>();

    /// <summary>
    /// Filtered, expanded, coalesced story-beat list for a scene, ready to hand to a per-scene
    /// classifier. Called once per classifier that needs it (not shared) — several run
    /// concurrently for the same scene, and each gets its own independent clone so none can
    /// observe another's in-progress work even if a future classifier starts mutating beats.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildSceneBeats(
        Dictionary<string, object?> scene,
        int maxSeconds = ClipDurationEstimator.MaxSeconds)
    {
        var beats = GetList(scene, Keys.StoryBeats).OfType<Dictionary<string, object?>>()
            .Where(b => !IsNoopTransitionBeat(b))
            .ToList();
        beats = ClipDurationEstimator.ExpandLongDialogueBeats(beats, modelMaxSeconds: maxSeconds);
        return CoalesceDuplicateActionVoBeats(CoalesceSilentPreludeBeats(beats));
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
        if (!IsSilentPreludePair(b1, b2) || !SameOrEmptyBeatLocation(b1, b2))
            return beats;

        MergeSilentPreludeVisual(b1, b2);

        // Remove silent prelude b1 so b2 becomes clip 1 (frame-1 VO onset)
        PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(b2, b1);
        var result = new List<Dictionary<string, object?>>(beats);
        result.RemoveAt(0);
        return result;
    }

    /// <summary>
    /// Anywhere in a scene: a silent action beat immediately followed by a dialogue/VO beat that shows
    /// the SAME action (its visual_event is empty, or equal to / contained in the silent beat's) is one
    /// shot — the line is spoken over the action. Left as two beats, the shot plan replays the action
    /// in back-to-back clips (Mary19 S02: C04 teacher steers the lamb out, C05 the same steer with the
    /// narration). Beats whose visuals differ are NOT merged: that would drop an action.
    /// </summary>
    public static List<Dictionary<string, object?>> CoalesceDuplicateActionVoBeats(List<Dictionary<string, object?>> beats)
    {
        if (beats.Count < 2) return beats;
        var result = new List<Dictionary<string, object?>>(beats);
        var i = 0;
        while (i < result.Count - 1)
        {
            var b1 = result[i];
            var b2 = result[i + 1];
            if (!IsSilentPreludePair(b1, b2) || !SameOrEmptyBeatLocation(b1, b2) || !VoRepeatsAction(b1, b2))
            {
                i++;
                continue;
            }
            MergeSilentPreludeVisual(b1, b2);
            PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(b2, b1);
            b2[Keys.OwnClip] = true; // the action keeps its own shot; the line rides on it
            result.RemoveAt(i);
            // Re-check the merged beat against what follows (do not advance).
        }
        return result;
    }

    private static string Short(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return "(none)";
        return hash.Length > 8 ? hash[..8] : hash;
    }

    private static bool IsOwnClip(Dictionary<string, object?> beat) =>
        beat.TryGetValue(Keys.OwnClip, out var v) && v is true;

    private static bool VoRepeatsAction(Dictionary<string, object?> silent, Dictionary<string, object?> vo)
    {
        var ve1 = NormalizeVisual(CoerceString(silent.TryGetValue(Keys.VisualEvent, out var v1) ? v1 : null));
        var ve2 = NormalizeVisual(CoerceString(vo.TryGetValue(Keys.VisualEvent, out var v2) ? v2 : null));
        if (ve1.Length == 0) return false;
        if (ve2.Length == 0) return true;
        return ve1 == ve2 || ve2.Contains(ve1, StringComparison.Ordinal) || ve1.Contains(ve2, StringComparison.Ordinal);
    }

    private static string NormalizeVisual(string s) =>
        CommonRegex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static bool IsSilentPreludePair(Dictionary<string, object?> b1, Dictionary<string, object?> b2)
    {
        var d1 = CoerceString(b1.TryGetValue(JsonKeys.Dialogue, out var v1) ? v1 : null);
        var s1 = CoerceString(b1.TryGetValue(JsonKeys.Speaker, out var sp1) ? sp1 : null);
        var d2 = CoerceString(b2.TryGetValue(JsonKeys.Dialogue, out var v2) ? v2 : null);
        // Beat 1 must be silent (no dialogue, no speaker) and Beat 2 must have dialogue
        return string.IsNullOrWhiteSpace(d1) && string.IsNullOrWhiteSpace(s1) && !string.IsNullOrWhiteSpace(d2);
    }

    private static bool SameOrEmptyBeatLocation(Dictionary<string, object?> b1, Dictionary<string, object?> b2)
    {
        var l1 = CoerceString(b1.TryGetValue(Keys.LocationId, out var loc1) ? loc1 : null);
        var l2 = CoerceString(b2.TryGetValue(Keys.LocationId, out var loc2) ? loc2 : null);
        return string.Equals(l1, l2, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(l1)
            || string.IsNullOrEmpty(l2);
    }

    private static void MergeSilentPreludeVisual(Dictionary<string, object?> b1, Dictionary<string, object?> b2)
    {
        var ve1 = CoerceString(b1.TryGetValue(Keys.VisualEvent, out var vev1) ? vev1 : null);
        var ve2 = CoerceString(b2.TryGetValue(Keys.VisualEvent, out var vev2) ? vev2 : null);
        if (string.IsNullOrWhiteSpace(ve1))
            return;
        if (string.IsNullOrWhiteSpace(ve2))
            b2[Keys.VisualEvent] = ve1;
        else if (!ve2.Contains(ve1, StringComparison.OrdinalIgnoreCase))
            b2[Keys.VisualEvent] = $"{ve1} {ve2}";
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
            var lid = ResolveBeatLocation(beat, primary, lids);
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
        IReadOnlyList<bool>? extendsFromPrevious = null) =>
        WalkCoalesceGroups(beats, maxSeconds, extensionMaxSeconds, extendsFromPrevious, AbsorbMonologueGroup);

    private static int AbsorbMonologueGroup(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        int effectiveMax)
    {
        var d1 = ReadBeatString(cur, JsonKeys.Dialogue);
        var sp1 = ReadBeatString(cur, JsonKeys.Speaker);
        var del1 = ReadBeatString(cur, Keys.Delivery) ?? Keys.SpokenOnCamera;
        var loc1 = ReadBeatString(cur, Keys.LocationId);
        var ac1 = ReadBeatString(cur, Keys.ActionClass);

        if (IsMonologueMergeAnchor(d1, sp1, ac1))
            i = AbsorbMonologueFollowers(beats, i, cur, ref d1, sp1, del1, loc1, effectiveMax);
        return i;
    }

    private static bool IsMonologueMergeAnchor(string? d1, string? sp1, string? ac1) =>
        !string.IsNullOrWhiteSpace(d1) &&
        !string.IsNullOrWhiteSpace(sp1) &&
        !string.Equals(ac1, Keys.BigAction, StringComparison.OrdinalIgnoreCase);

    private static int AbsorbMonologueFollowers(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        ref string? d1,
        string? sp1,
        string del1,
        string? loc1,
        int effectiveMax)
    {
        while (i + 1 < beats.Count)
        {
            if (!TryAbsorbNextMonologueBeat(beats, i, cur, ref d1, sp1, del1, loc1, effectiveMax))
                break;
            i++;
        }
        return i;
    }

    private static int EffectiveMaxForBeat(
        IReadOnlyList<bool>? extendsFromPrevious,
        int index,
        int maxSeconds,
        int? extensionMaxSeconds) =>
        extendsFromPrevious is not null && index < extendsFromPrevious.Count && extendsFromPrevious[index]
            ? (extensionMaxSeconds ?? maxSeconds)
            : maxSeconds;

    private static bool CanMergeMonologueNext(
        string? d2,
        string? sp1,
        string? sp2,
        string? del1,
        string? del2,
        string? loc1,
        string? loc2,
        string? ac2)
    {
        if (string.IsNullOrWhiteSpace(d2) ||
            !string.Equals(sp1, sp2, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(del1, del2, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(loc1) && !string.IsNullOrEmpty(loc2) && !string.Equals(loc1, loc2, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(ac2, Keys.BigAction, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool TryAbsorbNextMonologueBeat(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        ref string? d1,
        string? sp1,
        string del1,
        string? loc1,
        int effectiveMax)
    {
        var next = beats[i + 1];
        var d2 = ReadBeatString(next, JsonKeys.Dialogue);
        var sp2 = ReadBeatString(next, JsonKeys.Speaker);
        var del2 = ReadBeatString(next, Keys.Delivery) ?? Keys.SpokenOnCamera;
        var loc2 = ReadBeatString(next, Keys.LocationId);
        var ac2 = ReadBeatString(next, Keys.ActionClass);

        if (IsOwnClip(next) || !CanMergeMonologueNext(d2, sp1, sp2, del1, del2, loc1, loc2, ac2))
            return false;

        var combinedDlg = $"{d1!.Trim()} {d2!.Trim()}";
        var estCombined = ClipDurationEstimator.EstimateUncapped(combinedDlg, "", JsonKeys.Dialogue, del1);
        if (estCombined > effectiveMax)
            return false;

        d1 = combinedDlg;
        cur[JsonKeys.Dialogue] = d1;
        MergeVisualEvent(cur, next);
        PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(cur, next);
        return true;
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
    public static int ResolveMaxSpeakersPerClip(string? videoModelId, bool isExtendHop = false)
    {
        if (string.IsNullOrWhiteSpace(videoModelId))
            return 1;
        var selected = SupportedModelCatalog.Find(videoModelId.Trim(), ModelCapability.Video);
        if (selected is null)
            return 1;
        try
        {
            var roles = SupportedModelCatalog.ResolveVideoRoles(videoModelId);
            if (!isExtendHop)
                return roles.Generate.MaxSpeakersPerClipOrDefault;
            // Hops use the extend sibling only. No extend role (1.5 alone) stays 1 speaker.
            return roles.Extend?.MaxSpeakersPerClipOrDefault ?? 1;
        }
        catch (InvalidOperationException)
        {
            return selected.MaxSpeakersPerClipOrDefault;
        }
    }

    /// <summary>
    /// Coalesce adjacent different-speaker dialogue beats when the generate-role model allows
    /// more than one speaker per clip. Extend-role hops use <paramref name="extendMaxSpeakersPerClip"/>
    /// (1 for the v1 sibling) so hops stay single-speaker.
    /// </summary>
    public static List<Dictionary<string, object?>> ApplyCrossSpeakerCoalescing(
        List<Dictionary<string, object?>> beats,
        int maxSpeakersPerClip,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int? extensionMaxSeconds = null,
        IReadOnlyList<bool>? extendsFromPrevious = null,
        int? extendMaxSpeakersPerClip = null)
    {
        var extendMax = extendMaxSpeakersPerClip ?? maxSpeakersPerClip;
        if (maxSpeakersPerClip < 2 && extendMax < 2)
            return beats ?? new List<Dictionary<string, object?>>();
        return CoalesceCrossSpeakerDialogueBeats(
            beats, maxSeconds, extensionMaxSeconds, extendsFromPrevious, maxSpeakersPerClip, extendMax);
    }

    public static List<Dictionary<string, object?>> CoalesceCrossSpeakerDialogueBeats(
        List<Dictionary<string, object?>> beats,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int? extensionMaxSeconds = null,
        IReadOnlyList<bool>? extendsFromPrevious = null,
        int maxSpeakersPerClip = 2,
        int? extendMaxSpeakersPerClip = null) =>
        WalkCoalesceGroups(
            beats, maxSeconds, extensionMaxSeconds, extendsFromPrevious,
            (list, i, cur, effectiveMax) => AbsorbCrossSpeakerGroup(
                list, i, cur, effectiveMax, SpeakerCapForBeat(extendsFromPrevious, i, maxSpeakersPerClip, extendMaxSpeakersPerClip ?? maxSpeakersPerClip)));

    private static int SpeakerCapForBeat(
        IReadOnlyList<bool>? extendsFromPrevious,
        int index,
        int generateMax,
        int extendMax) =>
        extendsFromPrevious is not null && index < extendsFromPrevious.Count && extendsFromPrevious[index]
            ? extendMax
            : generateMax;

    private static int AbsorbCrossSpeakerGroup(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        int effectiveMax,
        int maxSpeakers)
    {
        if (maxSpeakers < 2)
            return i;
        var speakers = 1;
        var perLineCap = effectiveMax / (double)maxSpeakers;
        while (speakers < maxSpeakers && i + 1 < beats.Count)
        {
            if (!TryMergeCrossSpeakerPair(beats, i, cur, effectiveMax, perLineCap, speakers + 1))
                break;
            i++;
            speakers++;
        }
        return i;
    }

    private static List<Dictionary<string, object?>> WalkCoalesceGroups(
        List<Dictionary<string, object?>> beats,
        int maxSeconds,
        int? extensionMaxSeconds,
        IReadOnlyList<bool>? extendsFromPrevious,
        Func<List<Dictionary<string, object?>>, int, Dictionary<string, object?>, int, int> absorbIntoCurrent)
    {
        if (beats is null || beats.Count < 2) return beats ?? new List<Dictionary<string, object?>>();

        var result = new List<Dictionary<string, object?>>();
        var i = 0;
        while (i < beats.Count)
        {
            var effectiveMax = EffectiveMaxForBeat(extendsFromPrevious, i, maxSeconds, extensionMaxSeconds);
            var cur = new Dictionary<string, object?>(beats[i]);
            i = absorbIntoCurrent(beats, i, cur, effectiveMax);
            result.Add(cur);
            i++;
        }

        return result;
    }

    private static bool TryMergeCrossSpeakerPair(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        int effectiveMax,
        double perLineCap,
        int nextSpeakerSlot = 2)
    {
        if (!IsEligibleCrossSpeakerPrimary(beats, i, cur, out var d1, out _, out var loc1))
            return false;
        var already = SpeakersAlreadyOnClip(cur);
        if (!IsEligibleCrossSpeakerNext(beats[i + 1], already, loc1, out var d2, out var sp2))
            return false;
        if (!CrossSpeakerPairFitsDuration(cur, beats[i + 1], d1, d2, effectiveMax, perLineCap))
            return false;

        var next = beats[i + 1];
        if (nextSpeakerSlot >= 3)
        {
            cur[Keys.TertiarySpeaker] = sp2;
            cur["tertiary_dialogue"] = d2;
        }
        else
        {
            cur[Keys.SecondarySpeaker] = sp2;
            cur["secondary_dialogue"] = d2;
        }

        // Size the merged clip for BOTH lines (was left at the primary's estimate,
        // so the second speaker's line got cut). Read the spoken lines back through
        // the shared accessor and size with the shared estimator, capped at the same
        // effective max used to decide the merge fit.
        cur[Keys.DurationSeconds] = ClipDurationEstimator.EstimateSpokenLinesSeconds(
            ClipSpokenLines.FromBeat(cur), maxSeconds: effectiveMax);

        MergeVisualEvent(cur, next);
        PageToMovie.Core.Utils.StableBeatId.MergeSourceIds(cur, next);
        return true;
    }

    private static bool IsEligibleCrossSpeakerPrimary(
        List<Dictionary<string, object?>> beats,
        int i,
        Dictionary<string, object?> cur,
        out string? d1,
        out string? sp1,
        out string? loc1)
    {
        d1 = ReadBeatString(cur, JsonKeys.Dialogue);
        sp1 = ReadBeatString(cur, JsonKeys.Speaker);
        loc1 = ReadBeatString(cur, Keys.LocationId);
        var ac1 = ReadBeatString(cur, Keys.ActionClass);
        return i + 1 < beats.Count &&
               !string.IsNullOrWhiteSpace(d1) &&
               !string.IsNullOrWhiteSpace(sp1) &&
               !string.Equals(ac1, Keys.BigAction, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEligibleCrossSpeakerNext(
        Dictionary<string, object?> next,
        IReadOnlyCollection<string> alreadyOnClip,
        string? loc1,
        out string? d2,
        out string? sp2)
    {
        d2 = ReadBeatString(next, JsonKeys.Dialogue);
        sp2 = ReadBeatString(next, JsonKeys.Speaker);
        var loc2 = ReadBeatString(next, Keys.LocationId);
        var ac2 = ReadBeatString(next, Keys.ActionClass);
        var sameLocationOrEmpty = string.IsNullOrEmpty(loc1) || string.IsNullOrEmpty(loc2) ||
            string.Equals(loc1, loc2, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(d2) &&
               !string.IsNullOrWhiteSpace(sp2) &&
               !alreadyOnClip.Contains(sp2, StringComparer.OrdinalIgnoreCase) &&
               sameLocationOrEmpty &&
               !IsOwnClip(next) &&
               !string.Equals(ac2, Keys.BigAction, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SpeakersAlreadyOnClip(Dictionary<string, object?> cur)
    {
        var speakers = ClipSpokenLines.FromBeat(cur)
            .Select(line => line.Speaker)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        if (speakers.Count == 0)
        {
            var primary = ReadBeatString(cur, JsonKeys.Speaker);
            if (!string.IsNullOrWhiteSpace(primary))
                speakers.Add(primary);
        }
        return speakers;
    }

    private static bool CrossSpeakerPairFitsDuration(
        Dictionary<string, object?> cur,
        Dictionary<string, object?> next,
        string? d1,
        string? d2,
        int effectiveMax,
        double perLineCap)
    {
        var del1 = ReadBeatString(cur, Keys.Delivery) ?? Keys.SpokenOnCamera;
        var del2 = ReadBeatString(next, Keys.Delivery) ?? Keys.SpokenOnCamera;
        var est1 = ClipDurationEstimator.EstimateUncapped(d1, "", JsonKeys.Dialogue, del1);
        var est2 = ClipDurationEstimator.EstimateUncapped(d2, "", JsonKeys.Dialogue, del2);
        return est1 <= perLineCap && est2 <= perLineCap && (est1 + est2) <= effectiveMax;
    }

    /// <summary>
    /// When merging <paramref name="next"/> into <paramref name="cur"/>, fold next's
    /// <c>visual_event</c> into cur's: append it (space-joined) unless it is blank or already
    /// contained (case-insensitive) in cur's existing visual_event.
    /// </summary>
    private static void MergeVisualEvent(Dictionary<string, object?> cur, Dictionary<string, object?> next)
    {
        var ve1 = CoerceString(cur.TryGetValue(Keys.VisualEvent, out var vev1) ? vev1 : null) ?? "";
        var ve2 = CoerceString(next.TryGetValue(Keys.VisualEvent, out var vev2) ? vev2 : null) ?? "";
        if (!string.IsNullOrWhiteSpace(ve2) && !ve1.Contains(ve2, StringComparison.OrdinalIgnoreCase))
        {
            cur[Keys.VisualEvent] = string.IsNullOrWhiteSpace(ve1) ? ve2 : $"{ve1} {ve2}";
        }
    }

    /// <summary>
    /// Fallback Camera helper when the classifier has no row: medium hold, or a copy/vary
    /// of the previous same-speaker Camera. Does not invent DoF, ECU eyes, macro hands,
    /// or OTS without a second on-screen body. Does not run when action already named a
    /// camera — call <see cref="CameraTagWriter.Resolve"/> for that gate.
    /// </summary>
    public static string GetMonologueCameraFraming(
        int step,
        string speakerDisplay = JsonKeys.Speaker,
        int onScreenCastCount = 1,
        string? previousCamera = null)
    {
        return CameraTagWriter.FallbackFraming(previousCamera, onScreenCastCount, speakerDisplay, step);
    }

    private static string ResolveVisualEvent(
        Dictionary<string, object?> beat,
        string? continuationAction)
    {
        if (!string.IsNullOrWhiteSpace(continuationAction))
            return continuationAction;
        beat.TryGetValue(Keys.VisualEvent, out var vev);
        return CoerceString(vev) ?? "";
    }

    /// <param name="continuationAction">
    /// For a clip that continues the previous one, the beat's action rewritten to events only —
    /// see <see cref="ContinuationActionClassifier"/>. Null keeps the beat's own action, which is
    /// what a fresh shot needs. Everything downstream is unchanged: the rewrite goes through the
    /// same subject, cast-key, blocking and sound-cue handling the original would have.
    /// </param>
    internal static string BuildVisualPrompt(
        Dictionary<string, object?> beat,
        Dictionary<string, object?> scene,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, List<string>> wardrobe,
        string? continuationAction = null)
    {
        var ve = ResolveVisualEvent(beat, continuationAction);
        // Strip accidental technical suffix from beat text (res/fps owned at gen time)
        ve = CommonRegex.Replace(ve, @"\s*/\s*\d+p.*$", "", RegexOptions.IgnoreCase).Trim();
        var cast = ClipCastTokens(scene, beat, charSeeds);
        var primary = CoerceString(beat.TryGetValue(Keys.PrimarySubject, out var ps) ? ps : null)
                      ?? (cast.Count > 0 ? cast[0] : "");

        var place = LocationLockPhrase(scene);
        var style = EnsureCastStyleLock(RenderStyleLock(scene), SceneVisualMedium(scene), cast, charSeeds);
        // A voice-only role (never_on_screen) is never "on screen" and has no wardrobe to keep: listing
        // the narrator here (and "Character_Narrator still wears wool jacket…" below) put a man in the
        // yard in Mary19 S03. Visual cast = cast minus voice-only roles.
        var visualCast = cast.Where(t => !IsNeverOnScreenCharacter(t, charSeeds)).ToList();
        if (IsNeverOnScreenCharacter(primary, charSeeds))
            primary = visualCast.Count > 0 ? visualCast[0] : "";
        ve = AttachSubjectIfMissing(ve, primary, charSeeds);
        ve = NormalizeCastMentionsToKeys(ve, visualCast, charSeeds);

        var others = visualCast.Where(t => t != primary && !ve.Contains(t, StringComparison.Ordinal)).Take(3).ToList();
        var othersBit = others.Count > 0 ? $"also on screen: {string.Join(", ", others)}" : "";
        // CAST COUNT + CHARACTER VARIABLES owned by ClipVideoPromptBuilder at gen time.

        ve = AppendBlockingNotes(ve, beat);
        var ac = (CoerceString(beat.TryGetValue(Keys.ActionClass, out var acv) ? acv : null) ?? "").ToLowerInvariant();
        ve = AppendActionClassMotion(ve, ac);

        var mustNot = GetList(beat, "must_not").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(3).ToList();
        var mustBit = mustNot.Count > 0 ? $"must not: {string.Join("; ", mustNot)}" : "";
        // Same wardrobe phrase length for all clips in the scene (consistent continuity language).
        var ward = WardrobeContinuityClause(wardrobe, visualCast, primary);

        // Sound cues arrive inside the beat's visual_event (Stage 1 fountain writes "(SOUND: …)").
        // Lift them into their own slot rather than leaving them buried in the action.
        var (action, _) = SplitSoundCues(ve);
        action = CameraTagWriter.StripFromAction(action);

        // Emit full slots — no length budget, no dropping fields, no ellipsis packing.
        // Identity cues omitted: gen-time CHARACTER VARIABLES + locked refs own identity.
        //
        // Each slot is tagged. This method knows exactly where every field starts and ends, so
        // flattening them into one prose blob (which is what this used to do) threw that away and
        // made every later consumer guess it back with regexes — the editor, the style-lock lint,
        // and PreviousClipLookOnly all had to pattern-match "STYLE LOCK:" / "INT." / "Color
        // grading:" out of running text. Lighting/Camera/Performance/Optics were already tagged
        // in AppendVisualDirectives; these are the rest of the same idea.
        var parts = new List<(int Order, string Tag, string Text)>
        {
            (0, PromptFieldTags.StyleLock, StripLabel(style, "STYLE LOCK:")),
            (2, PromptFieldTags.Setting, PlaceLockIfMissing(place, ve)),
            (3, PromptFieldTags.Cast, StripLabel(othersBit, "also on screen:")),
            (5, PromptFieldTags.Action, action),
            // No <Sound>. The screenplay's own (SOUND: …) cue is parsed at Stage 1 into the
            // beat's ambient/sfx, reaches the clip as audio_payload, and ClipVideoPromptBuilder
            // renders it into <Audio> as <Foley>/<Score> at gen time. Emitting it here too asked
            // the model for the same foley twice against ONE request for the narration, and the
            // foley won the opening moment: measured in the provider playground, adding this
            // block alone to a working extend made the narrator drop the line's first word.
            // <Foley> by itself does not. Same duplication as <Speech>, but this one is audible.
            // No <Speech>. The spoken line lives in audio_payload, and ClipVideoPromptBuilder
            // renders it into <Audio> at gen time — that copy is the one the model obeys. Baking
            // a second copy in here put every line in the prompt twice: wasted budget on prompts
            // already being compressed, and two editable surfaces for one fact, only one of which
            // changed what was actually said. Same reasoning that moved continuity and
            // resolution/fps out to gen time (see PlanSingleClip) — keep visual_prompt visual.
            (8, PromptFieldTags.MustNot, StripLabel(mustBit, "must not:")),
            (9, PromptFieldTags.Wardrobe, ward),
        };
        return JoinVisualPromptParts(parts);
    }

    /// <summary>
    /// Rewrite screenplay-cased cast mentions in action prose to their <c>Character_*</c> keys —
    /// "THE CHILDREN twist in their seats" becomes "Character_The_Children twist in their seats".
    /// </summary>
    /// <remarks>
    /// Every structured block in a clip prompt names cast by key (and CompressPromptText aliases
    /// those to C1/C2), while the action named them in screenplay caps. One prompt therefore
    /// carried two naming schemes for the same person with nothing linking them — measured on real
    /// Mary19 traffic: &lt;Characters&gt;, &lt;CastCount&gt; and "On-screen:" all said C1/C2 while
    /// the action said MARY and THE LAMB. It also made SanitizeActionText's "{key} is on screen."
    /// fallback fire on every single clip, because Contains(key) could never match a caps mention.
    ///
    /// <para>Only ALL-CAPS mentions are rewritten. That is the screenplay convention the planner
    /// and the fountain both use for a character, and it is what keeps a generic lowercase noun
    /// out of it — "to see a lamb at school" is not THE LAMB.</para>
    ///
    /// <para>This does NOT retire ClipVideoPromptBuilder.InferKeysFromProse. That maps Stage 1
    /// fountain prose to keys and has consumers in the on-screen-cast classifier, Stage 2 cast
    /// resolution and the eval tools; the fountain is authored in screenplay case and always
    /// will be.</para>
    /// </remarks>
    internal static string NormalizeCastMentionsToKeys(
        string? actionText,
        IReadOnlyList<string> cast,
        Dictionary<string, object?> charSeeds)
    {
        var text = actionText ?? "";
        if (text.Length == 0 || cast is not { Count: > 0 })
            return text;

        // Longest form first, so "THE OLD MAN" is not half-consumed by "MAN".
        var candidates = cast
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .SelectMany(key => CastMentionForms(key, charSeeds).Select(name => (Key: key, Name: name)))
            .Where(c => c.Name.Length >= 3)
            .OrderByDescending(c => c.Name.Length)
            .ToList();

        foreach (var (key, name) in candidates)
        {
            // Case-SENSITIVE against the upper form: lowercase use is generic prose, not a cue.
            // The lookarounds keep it off word fragments and off an already-written key.
            var pattern =
                $@"(?<![A-Za-z_]){System.Text.RegularExpressions.Regex.Escape(name.ToUpperInvariant())}(?![A-Za-z_])";
            text = CommonRegex.Replace(text, pattern, key);
        }
        return text;
    }

    /// <summary>Ways a screenplay might name this cast key: "The Lamb", "Lamb", its given name.</summary>
    private static IEnumerable<string> CastMentionForms(string key, Dictionary<string, object?> charSeeds)
    {
        var suffix = key.Replace(JsonKeys.CharacterPrefix, "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ').Trim();
        if (suffix.Length > 0)
        {
            yield return suffix;
            // A key can carry an article the prose leaves off (Character_The_Lamb / "THE LAMB"
            // in one beat, plain "LAMB" in the next).
            var bare = CommonRegex.Replace(suffix, @"^(?:the|a|an)\s+", "", RegexOptions.IgnoreCase).Trim();
            if (bare.Length > 0 && !string.Equals(bare, suffix, StringComparison.OrdinalIgnoreCase))
                yield return bare;
        }

        if (charSeeds.TryGetValue(key, out var raw) && raw is Dictionary<string, object?> seed
            && seed.TryGetValue("canonical_given_name", out var gn)
            && CoerceString(gn) is { Length: > 0 } given)
            yield return given.Trim();
    }

    /// <summary>Pull every <c>(SOUND: …)</c> cue out of the action prose. Returns (action, cues).</summary>
    internal static (string Action, string Sound) SplitSoundCues(string? visualEvent)
    {
        var text = visualEvent ?? "";
        if (text.Length == 0)
            return ("", "");
        var cues = new List<string>();
        var stripped = CommonRegex.Replace(text, @"\(\s*SOUND:\s*(?<cue>[^)]*)\)", m =>
        {
            var cue = m.Groups["cue"].Value.Trim();
            if (cue.Length > 0)
                cues.Add(cue);
            return "";
        }, RegexOptions.IgnoreCase);
        stripped = CommonRegex.WhitespaceCollapse.Replace(stripped, " ").Trim();
        stripped = CommonRegex.Replace(stripped, @"\s+([.,;])", "$1");
        return (stripped, string.Join("; ", cues));
    }

    /// <summary>Drop a prose label now that the tag carries the field's identity.</summary>
    private static string StripLabel(string? text, string label)
    {
        var t = (text ?? "").Trim();
        return t.StartsWith(label, StringComparison.OrdinalIgnoreCase)
            ? t[label.Length..].Trim()
            : t;
    }

    /// <summary>
    /// When the scene has no <c>render_style_lock</c>, fill from the project's visual medium.
    /// Never invent a 3D CG lock — that flipped illustrated films to photoreal/CG backgrounds.
    /// </summary>
    internal static string EnsureCastStyleLock(
        string style,
        string? visualMedium,
        List<string> cast,
        Dictionary<string, object?> charSeeds)
    {
        // Fire for any on-screen character that is visually present, i.e. not a
        // pure-voice-only character (display_name_policy = "never_on_screen").
        if (!string.IsNullOrWhiteSpace(style))
            return style;
        if (!cast.Any(t => !IsNeverOnScreenCharacter(t, charSeeds)))
            return style;
        if (!VisualMediumStyles.IsDecidedMedium(visualMedium))
            return style;
        return VisualMediumStyles.StyleLockFor(VisualMediumStyles.NormalizeMedium(visualMedium));
    }

    private static string? SceneVisualMedium(Dictionary<string, object?> scene) =>
        CoerceString(scene.TryGetValue(JsonKeys.VisualMedium, out var vm) ? vm : null);

    private static string AttachSubjectIfMissing(
        string ve,
        string primary,
        Dictionary<string, object?> charSeeds)
    {
        // Attach subject as readable display name — never "Character_X He steadies…"
        if (string.IsNullOrEmpty(primary) || VisualMentionsSubject(ve, primary))
            return ve;
        var display = DisplayNameForKey(primary, charSeeds);
        return AttachPrimaryToVisual(ve, primary, display);
    }

    private static string AppendBlockingNotes(string ve, Dictionary<string, object?> beat)
    {
        var block = CoerceString(beat.TryGetValue(Keys.BlockingNotes, out var bn) ? bn : null) ?? "";
        if (string.IsNullOrWhiteSpace(block) ||
            ve.Contains(block, StringComparison.OrdinalIgnoreCase))
            return ve;
        return $"{ve}. {block}".Trim();
    }

    private static string AppendActionClassMotion(string ve, string ac)
    {
        if (ac == Keys.BigAction &&
            !ve.Contains("continuous", StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. ONE continuous take no cut; unbroken cause-to-effect motion";

        // Establishing shots otherwise describe only a static composition — a known AI-video
        // failure mode where the "opening wide shot" of a scene looks like a frozen photo. Nudge
        // in setting-appropriate ambient background life (the model invents specifics; no new
        // classifier call), mirroring how big_action gets its own action_class-specific guidance.
        if (ac == "establishing" &&
            !ve.Contains("subtle", StringComparison.OrdinalIgnoreCase) &&
            !ve.Contains("ambient motion", StringComparison.OrdinalIgnoreCase))
        {
            ve = $"{ve}. Include subtle background motion appropriate to this setting (e.g. distant " +
                 "traffic or passersby, a sign or light flickering, wind moving debris/foliage/fabric) " +
                 "so the shot feels alive, not a still photo";
        }
        return ve;
    }

    private static string PlaceLockIfMissing(string place, string ve)
    {
        if (string.IsNullOrEmpty(place) || ve.Contains(place, StringComparison.OrdinalIgnoreCase))
            return "";
        return place;
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
        var bare = primaryKey.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? primaryKey[JsonKeys.CharacterPrefix.Length..]
            : primaryKey;
        if (string.IsNullOrWhiteSpace(bare)) return false;
        // Character_Old_Man → "Old Man", "Old_Man", "OLDMAN"
        var spaced = bare.Replace('_', ' ');
        if (visual.Contains(spaced, StringComparison.OrdinalIgnoreCase))
            return true;
        if (visual.Contains(bare, StringComparison.OrdinalIgnoreCase))
            return true;
        var compact = CommonRegex.Replace(bare, @"[_ ]+", "");
        if (compact.Length >= 3 &&
            CommonRegex.IsMatch(visual, $@"\b{Regex.Escape(compact)}\b", RegexOptions.IgnoreCase))
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
            var bare = primaryKey.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
                ? primaryKey[JsonKeys.CharacterPrefix.Length..]
                : primaryKey;
            name = bare.Replace('_', ' ').Trim();
        }
        if (name.Length == 0)
            return ve;

        // He steadies… / She turns… / They wait…
        var m = CommonRegex.Match(
            ve,
            @"^(He|She|They|Him|Her|Them)\b(\s+)(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
            return $"{name} {m.Groups["rest"].Value.Trim()}".Trim();

        // His hands… / Her eyes…
        m = CommonRegex.Match(
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
                return cn;
            var vl = CoerceString(d.TryGetValue("voice_label", out var v) ? v : null);
            if (!string.IsNullOrWhiteSpace(vl))
                return vl.Replace('_', ' ');
        }
        var bare = primaryKey.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? primaryKey[JsonKeys.CharacterPrefix.Length..]
            : primaryKey;
        return bare.Replace('_', ' ').Trim();
    }

    /// <summary>
    /// Emit each populated slot as its own tag. Tags are self-delimiting, so the sentence-joining
    /// that used to fuse these fields into one blob is gone; empty slots are simply absent.
    /// </summary>
    private static string JoinVisualPromptParts(IEnumerable<(int Order, string Tag, string Text)> parts)
    {
        var blocks = parts
            .OrderBy(p => p.Order)
            .Select(p => (p.Tag, Text: NormalizeSentencePart(p.Text)))
            .Where(p => p.Text.Length > 0)
            .Select(p => PromptTags.Wrap(p.Tag, PromptTags.SanitizeValue(p.Text)))
            .ToList();
        if (blocks.Count == 0)
            blocks.Add(PromptTags.Wrap(PromptFieldTags.Action, "Scene action"));
        return string.Join(" ", blocks);
    }

    private static readonly Regex CharacterTokenRegex = new(@"Character_[A-Za-z0-9_]+", RegexOptions.Compiled, CommonRegex.Timeout);

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
        var dlg = CoerceString(beat.TryGetValue(JsonKeys.Dialogue, out var d) ? d : null) ?? "";
        if (!string.IsNullOrWhiteSpace(dlg)) return false;
        var ve = CoerceString(beat.TryGetValue(Keys.VisualEvent, out var v) ? v : null) ?? "";
        if (string.IsNullOrWhiteSpace(ve)) return true;
        if (FountainParser.IsStandaloneTransitionLine(ve)) return true;
        return CommonRegex.IsMatch(
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
        var cut = (CoerceString(beat.TryGetValue("cut_decision", out var cd) ? cd : null) ?? "").ToLowerInvariant();
        if (TryCutDecisionForceNone(cut, out var fromCut))
            return fromCut;

        var ac = (CoerceString(beat.TryGetValue(Keys.ActionClass, out var a) ? a : null) ?? "").ToLowerInvariant();
        var cont = (CoerceString(beat.TryGetValue("continuity", out var c) ? c : null) ?? "").ToLowerInvariant();
        if (ActionOrContinuityForcesNone(ac, cont))
            return true;
        if (LocationsDiffer(prevLocationId, locationId))
            return true;
        if (SpokenTransitionForcesNone(beat, prevBeat))
            return true;
        if (IsVoBeat(beat))
            return cont != "continuous_from_previous_beat";
        var ve = (CoerceString(beat.TryGetValue(Keys.VisualEvent, out var vev) ? vev : null) ?? "").ToLowerInvariant();
        return VisualEventForcesNone(ve);
    }

    private static bool TryCutDecisionForceNone(string cut, out bool forceNone)
    {
        if (cut is "hard_cut" or "hardcut" or "none")
        {
            forceNone = true;
            return true;
        }
        if (cut is "extend" or "continue" or "continuous")
        {
            forceNone = false;
            return true;
        }
        forceNone = false;
        return false;
    }

    private static bool ActionOrContinuityForcesNone(string ac, string cont)
    {
        if (ac is Keys.BigAction or "establishing" or "hard_cut" or "flashback_enter" or "flashback_exit" or "montage")
            return true;
        return cont is "new_setup" or "return_to_present" or "parallel";
    }

    private static bool LocationsDiffer(string? prevLocationId, string? locationId) =>
        prevLocationId is not null && locationId is not null && prevLocationId != locationId;

    private static bool SpokenTransitionForcesNone(
        Dictionary<string, object?> beat, Dictionary<string, object?>? prevBeat)
    {
        // Silent establish → first spoken/VO: hard cut so opening words are not clipped by extend
        if (prevBeat is not null && BeatHasSpokenAudio(beat) && !BeatHasSpokenAudio(prevBeat))
            return true;
        return IsVoBeat(beat) && prevBeat is not null && IsOnCameraSpeech(prevBeat);
    }

    private static bool VisualEventForcesNone(string ve) =>
        CommonRegex.IsMatch(ve,
            @"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|flashback|back to present|cut to)\b");

    /// <summary>True when the beat carries spoken dialogue or VO (not silent action).</summary>
    private static bool BeatHasSpokenAudio(Dictionary<string, object?> beat)
    {
        var (delivery, _) = BeatAudio(beat);
        if (delivery is "none" or "")
            return false;
        var dialogue = CoerceString(beat.TryGetValue(JsonKeys.Dialogue, out var d) ? d : null) ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) &&
            beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad)
            dialogue = CoerceString(ad.TryGetValue(JsonKeys.Dialogue, out var d2) ? d2 : null) ?? "";
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
        return d is Keys.SpokenOnCamera or "on_camera" or "spoken";
    }

    /// <summary>Normalize delivery aliases to canonical tokens for audio_payload.</summary>
    public static string NormalizeDelivery(string? delivery)
    {
        var d = (delivery ?? "none").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(d)) return "none";
        if (d is "on_camera" or "spoken" or "dialogue_on_camera")
            return Keys.SpokenOnCamera;
        if (d is "vo" or "voiceover" or "voice_over" or "off_camera" or "offcamera")
            return "voiceover_internal";
        return d;
    }

    private static (string Delivery, string Speaker) BeatAudio(Dictionary<string, object?> beat)
    {
        var nested = NestedAudioDict(beat);
        var delivery = NormalizeDelivery(ReadBeatAudioField(beat, nested, Keys.Delivery));
        var speaker = (ReadBeatAudioField(beat, nested, JsonKeys.Speaker) ?? "").ToLowerInvariant();
        return (delivery, speaker);
    }

    private static Dictionary<string, object?>? NestedAudioDict(Dictionary<string, object?> beat) =>
        beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;

    private static string? ReadBeatAudioField(
        Dictionary<string, object?> beat,
        Dictionary<string, object?>? nested,
        string key)
    {
        if (nested?.TryGetValue(key, out var n) == true)
            return CoerceString(n);
        if (beat.TryGetValue(key, out var b))
            return CoerceString(b);
        return CoerceString(null);
    }

    private static Dictionary<string, object?> BuildAudioPayload(
        Dictionary<string, object?> beat,
        SoundDesignDirective? sd = null)
    {
        // Prefer normalized separate keys (Stage1Normalizer / Fountain importer)
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);

        var nested = NestedAudioDict(beat);
        var delivery = NormalizeDelivery(ReadBeatAudioField(beat, nested, Keys.Delivery) ?? "none");
        var speaker = ReadBeatAudioField(beat, nested, JsonKeys.Speaker) ?? "";
        // Store speech-safe dialogue in the plan (UI + gen see the same text)
        var dialogue = ClipVideoPromptBuilder.SanitizeSpokenDialogue(
            ReadBeatAudioField(beat, nested, JsonKeys.Dialogue) ?? "");
        var ambient = ReadBeatAudioField(beat, nested, "ambient") ?? "";
        var sfx = ReadBeatAudioField(beat, nested, "sfx") ?? "";
        var pronHint = ReadBeatAudioField(beat, nested, "pronunciation_hint") ?? "";

        var payload = new Dictionary<string, object?>
        {
            [Keys.Delivery] = delivery,
            [JsonKeys.Speaker] = speaker,
            [JsonKeys.Dialogue] = dialogue,
            ["sfx"] = sfx,
            ["ambient"] = ambient,
        };

        ApplyPronunciationHint(payload, pronHint, dialogue);
        ApplySecondaryDialogue(payload, beat);
        ApplySoundDesignLayers(payload, sd);
        return payload;
    }

    private static void ApplyPronunciationHint(
        Dictionary<string, object?> payload, string pronHint, string dialogue)
    {
        // A pronunciation hint only earns its place when the word it targets is actually spoken in this
        // beat's dialogue — carrying one onto a silent/no-dialogue beat (or for a word not in the line)
        // just adds noise to the prompt.
        if (!string.IsNullOrWhiteSpace(pronHint) &&
            Deterministic.Pronunciation.PronunciationResolver.HintAppliesToDialogue(pronHint, dialogue))
        {
            payload["pronunciation_hint"] = pronHint;
        }
    }

    private static void ApplySecondaryDialogue(
        Dictionary<string, object?> payload, Dictionary<string, object?> beat)
    {
        // Cross-speaker clips carry extra speaker lines here. Additive only — existing
        // single-speaker readers keep working unmodified since the flat speaker/dialogue
        // keys above are untouched.
        CopySpokenSlot(payload, beat, Keys.SecondarySpeaker, "secondary_dialogue");
        CopySpokenSlot(payload, beat, Keys.TertiarySpeaker, "tertiary_dialogue");
    }

    private static void CopySpokenSlot(
        Dictionary<string, object?> payload,
        Dictionary<string, object?> beat,
        string speakerKey,
        string dialogueKey)
    {
        var speaker = ReadBeatString(beat, speakerKey);
        var dialogue = ReadBeatString(beat, dialogueKey);
        if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(dialogue))
            return;
        payload[speakerKey] = speaker;
        payload[dialogueKey] = ClipVideoPromptBuilder.SanitizeSpokenDialogue(dialogue);
    }

    private static void ApplySoundDesignLayers(
        Dictionary<string, object?> payload, SoundDesignDirective? sd)
    {
        if (sd is null)
            return;
        if (!string.IsNullOrWhiteSpace(sd.AmbientLayer))
            payload["ambient_layer"] = sd.AmbientLayer;
        if (!string.IsNullOrWhiteSpace(sd.FoleyLayer))
            payload["foley_layer"] = sd.FoleyLayer;
        if (!string.IsNullOrWhiteSpace(sd.ScoreLayer))
            payload["score_layer"] = sd.ScoreLayer;
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
        TryTakeOnScreenCastList(beat, found);

        var veText = CoerceString(beat.TryGetValue(Keys.VisualEvent, out var ve) ? ve : null) ?? "";
        AddCharacterKeysFromText(found, veText);
        AddCharacterKey(found, CoerceString(beat.TryGetValue(Keys.PrimarySubject, out var ps) ? ps : null));
        AddCharacterKey(found, CoerceString(beat.TryGetValue(JsonKeys.Speaker, out var sp) ? sp : null));
        AddCharacterKeysFromText(found, CoerceString(beat.TryGetValue(Keys.BlockingNotes, out var bn) ? bn : null));

        PromoteNamesFromCastSeeds(found, charSeeds, veText, beat);

        if (found.Count == 0)
            found.AddRange(UnionCharactersOnScreen(scene));
        return found;
    }

    private static void TryTakeOnScreenCastList(
        Dictionary<string, object?> beat,
        List<string> found)
    {
        if (beat.TryGetValue(Keys.CharactersOnScreen, out var cos) && cos is List<object?> cosList && cosList.Count > 0)
        {
            foreach (var x in cosList)
                AddCharacterKey(found, x?.ToString());
        }
    }

    private static void PromoteNamesFromCastSeeds(
        List<string> found,
        Dictionary<string, object?>? charSeeds,
        string veText,
        Dictionary<string, object?> beat)
    {
        // Promote free-text names using cast seed keys
        if (charSeeds is not { Count: > 0 })
            return;

        var profiles = BuildClipCastProfiles(charSeeds);
        var prose = string.Join(" ",
            veText,
            CoerceString(beat.TryGetValue(Keys.BlockingNotes, out var bn2) ? bn2 : null) ?? "");
        foreach (var key in ClipVideoPromptBuilder.InferKeysFromProse(prose, profiles))
            AddCharacterKey(found, key);
    }

    private static Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> BuildClipCastProfiles(
        Dictionary<string, object?> charSeeds)
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
                    ?? k.Replace(JsonKeys.CharacterPrefix, "").Replace('_', ' '),
            };
        }

        return profiles;
    }

    private static void AddCharacterKey(List<string> found, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!key.StartsWith(JsonKeys.CharacterPrefix, StringComparison.Ordinal)) return;
        if (!found.Contains(key)) found.Add(key);
    }

    private static void AddCharacterKeysFromText(List<string> found, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in CharacterTokenRegex.Matches(text))
            AddCharacterKey(found, m.Value);
    }

    private static List<string> UnionCharactersOnScreen(Dictionary<string, object?> scene)
    {
        var set = new List<string>();
        foreach (var x in GetList(scene, Keys.CharactersOnScreen))
            AddCharacterKey(set, x?.ToString());
        foreach (var b in GetList(scene, Keys.StoryBeats).OfType<Dictionary<string, object?>>())
        {
            AddCharacterKey(set, CoerceString(b.TryGetValue(Keys.PrimarySubject, out var ps) ? ps : null));
            AddCharacterKey(set, CoerceString(b.TryGetValue(JsonKeys.Speaker, out var sp) ? sp : null));
            var ve = CoerceString(b.TryGetValue(Keys.VisualEvent, out var vev) ? vev : null) ?? "";
            foreach (Match m in CommonRegex.Matches(ve, @"Character_[A-Za-z0-9_]+"))
                AddCharacterKey(set, m.Value);
        }
        return set;
    }

    /// <summary>
    /// <c>&lt;Setting&gt;</c> is the scene heading / time-of-day only (INT./EXT. DAY/NIGHT).
    /// Architecture lives on the set plate; lighting mood lives on <c>&lt;Lighting&gt;</c>.
    /// No fallback to location-seed visual_lock — old projects without a heading must re-gen Stage 2.
    /// </summary>
    public static string LocationLockPhrase(Dictionary<string, object?> scene)
    {
        var setting = CoerceString(scene.TryGetValue(Keys.Setting, out var st) ? st : null)?.Trim();
        return HasSceneHeadingSetting(setting) ? setting ?? "" : "";
    }

    private static bool HasSceneHeadingSetting(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && LooksLikeSceneHeading(setting);

    /// <summary>True for Fountain-style INT./EXT. headings (used to prefer scene.setting as place lock).</summary>
    public static bool LooksLikeSceneHeading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return CommonRegex.IsMatch(
            t,
            @"^(INT\.?|EXT\.?|EST\.?|I/?E\.?|INT\.?\s*/\s*EXT\.?)\b",
            RegexOptions.IgnoreCase);
    }

    private static string RenderStyleLock(Dictionary<string, object?> scene) =>
        CoerceString(scene.TryGetValue(JsonKeys.RenderStyleLock, out var r) ? r : null) ?? "";

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
        // "Match Name as cast for this production." — old EnsureCharacter visual_lock.
        // Absent from every sample project's cast seeds, and it stays: this is a guard against
        // placeholder text reaching a visual prompt, not a compression rule. It reads cast-seed
        // DATA rather than plan format, so retagging the plan does not retire it, and never
        // matching is the outcome it exists to produce.
        if (CommonRegex.IsMatch(t, @"^Match\s+.+\s+as cast for this production\.?$", RegexOptions.IgnoreCase))
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
        var subject = CoerceString(beat.TryGetValue(Keys.PrimarySubject, out var ps) ? ps : null)
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
        if (CommonRegex.IsMatch(t,
                @"\b(hat|cap|bonnet|hood|wig|glasses|spectacles|monocle|mask|veil|" +
                @"badge|collar|leash|nightshirt|nightgown|robe|uniform|armor|" +
                @"scarf|cravat|tie|eyepatch)\b"))
            return 0;
        // Core clothing body
        if (CommonRegex.IsMatch(t,
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
            [Keys.DurationSeconds] = total,
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
                n = s.TryGetValue(JsonKeys.SceneNumber, out var sn) ? sn : null,
                b = GetList(s, Keys.StoryBeats).Count,
                d = s.TryGetValue("duration_target_seconds", out var d) ? d : null,
            }),
            chars = GetDict(GetDict(stage1, Keys.GlobalProductionVariables), Keys.CharacterSeedTokens).Keys.OrderBy(k => k),
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
        GetList(d, Keys.Scenes).OfType<Dictionary<string, object?>>().ToList();

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
        foreach (var scene in GetList(plan, Keys.Scenes).OfType<Dictionary<string, object?>>())
        {
            var cast = GetList(scene, Keys.CharactersOnScreen)
                .Select(x => x?.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            var seen = new HashSet<string>(cast, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var clip in GetList(scene, Keys.VeoClips).OfType<Dictionary<string, object?>>())
            {
                foreach (var ch in GetList(clip, Keys.CharactersOnScreen)
                             .Select(x => x?.ToString() ?? "")
                             .Where(s => s.Length > 0)
                             .Where(ch => seen.Add(ch)))
                {
                    cast.Add(ch);
                    changed = true;
                }
            }
            if (changed)
                scene[Keys.CharactersOnScreen] = cast.Cast<object?>().ToList();
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

    private static string? ReadBeatString(Dictionary<string, object?> beat, string key) =>
        CoerceString(beat.TryGetValue(key, out var v) ? v : null);

    private static string? ResolveBeatLocation(
        Dictionary<string, object?> beat, string? primary, List<string> lids) =>
        ReadBeatString(beat, Keys.LocationId)
        ?? primary ?? (lids.Count > 0 ? lids[0] : null);

    private sealed class SceneFanoutState
    {
        public int CompletedScenes;
        public required int TotalScenes;
        public required SemaphoreSlim SceneGate;
        public readonly object ProgressGate = new();
        public Action<string>? OnProgress;
        public required System.Collections.Concurrent.ConcurrentBag<(int SceneNumber, Dictionary<string, object?> Scene)> PlannedBag;

        public void Report(string line)
        {
            lock (ProgressGate)
                OnProgress?.Invoke(line);
        }
    }

    private sealed record SceneClassifierTasks(
        Task<Dictionary<string, int>?> Pacing,
        Task<string?> Lighting,
        Task<Dictionary<string, CameraDirective>?> Camera,
        Task<string?> Negative,
        Task<Dictionary<string, string>?> Wardrobe,
        Task<Dictionary<string, EmotionDirective>?> Emotion,
        Task<Dictionary<string, SoundDesignDirective>?> Sound,
        Task<Dictionary<string, DepthOfFieldDirective>?> Dof,
        Task<ColorGradingDirective?> Color,
        Task<Dictionary<string, string>?> ContinuationAction);

    private sealed record PlannedClip(
        Dictionary<string, object?> Clip,
        string BeatId,
        int Duration,
        string? LocationId,
        string? ActiveSpeaker,
        int MonologueStep);

    /// <summary>
    /// Catalog provider id for the project's video model. Empty when not yet selected —
    /// never invents "grok".
    /// </summary>
    private static string ResolveVideoProviderProfile(Dictionary<string, object?>? stage1)
    {
        // Prefer explicit stamp already on stage1 / plan if a prior step wrote a catalog id.
        if (stage1 is not null
            && stage1.TryGetValue(Keys.VideoProviderProfile, out var existing)
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
