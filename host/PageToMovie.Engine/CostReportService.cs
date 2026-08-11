using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Billing;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

// Lets tests exercise the internal rate-table/pricing math (BuildVideoRateTable,
// BuildVideoBaseRateTable, RatesFromModels) directly, rather than only through the full
// ProjectStore-backed public API — pure calculation logic is worth unit-testing in isolation.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PageToMovie.Tests")]

namespace PageToMovie.Engine;

/// <summary>
/// Cost ledger (pipeline_state.cost_ledger) + planning estimates from blueprint + list rates.
/// Rates come from <see cref="SupportedModelCatalog"/> for the project's selected video/image
/// models (vendor list prices), not free-form config dollars. Customer-facing amounts apply the
/// admin <see cref="BillingOptions.ChargeMultiplier"/> is display-only (and credit debit); ledger stores list rates only. Events may include
/// charged <c>usd</c>.
/// </summary>
public sealed class CostReportService
{
    // Cost rates: models_catalog.json only. Missing prices throw — Engine does not invent USD.
    // Per-clip effective duration (duration_seconds numeric → timestamp span → default) is resolved
    // by the shared ClipDuration.Resolve helper.

    private readonly ProjectStore _projects;
    private readonly CreditService? _credits;
    private readonly UserDatabaseService? _userDb;
    private readonly ClipTimingTelemetryRepository? _timingDb;
    private readonly IOptions<PageToMovieOptions>? _opts;

    private const int MinApiSamples = 8;
    private const int MinTimingSamples = 5;

            public CostReportService(
        ProjectStore projects,
        CreditService? credits = null,
        UserDatabaseService? userDb = null,
        ClipTimingTelemetryRepository? timingDb = null,
        IOptions<PageToMovieOptions>? opts = null)
    {
        _projects = projects;
        _credits = credits;
        _userDb = userDb;
        _timingDb = timingDb;
        _opts = opts;
    }

    /// <summary>Current admin charge multiplier (hot-applied via runtime config onto options).</summary>
    public double GetChargeMultiplier() =>
        ChargePricing.ClampMultiplier(_opts?.Value.Billing?.ChargeMultiplier ?? 1.0);

    public async Task<CostReport> GetReportAsync(
        string projectId,
        string? draftResolution = null,
        string? heroResolution = null,
        double? assumeAvgRetries = null,
        int recentLimit = 200,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        // Cost rates come from the models catalog (SSoT) for the models this project has chosen.
        // There is no default model — if none is set (models are chosen on the Configuration page),
        // fail fast with a clear, actionable message instead of a cryptic downstream rate-table error.
        if (string.IsNullOrWhiteSpace(GetStr(cfg, "model_name", "")))
            throw new InvalidOperationException(
                "No models are set for this project yet. Choose a video and image model on the "
                + "Configuration page to see a cost estimate.");
        var rates = RatesFromConfig(cfg);
        var draftRes = draftResolution
            ?? GetStr(cfg, "resolution", "480p");
        var heroRes = heroResolution ?? "720p";
        var retries = assumeAvgRetries
            ?? GetDouble(rates, "assume_avg_retries", 0);

        // Quality Gate Retry (config) + history-refined video multiplier.
        var qaRetryOnFail = GetCfgBool(cfg, "qa_retry_on_fail", defaultValue: true);
        var qaMaxRetries = GetCfgInt(cfg, "qa_max_retries", defaultValue: 1);
        qaMaxRetries = Math.Clamp(qaMaxRetries, 0, 5);
        var priorVideoMultiplier = 1.3;
        if (cfg.TryGetValue("cost_estimates", out var ceQa) && ceQa.ValueKind == JsonValueKind.Object)
        {
            if (ceQa.TryGetProperty("qa_retry_video_multiplier", out var qm) &&
                qm.TryGetDouble(out var qmv) && qmv >= 1.0)
                priorVideoMultiplier = Math.Clamp(qmv, 1.0, 3.0);
            else if (ceQa.TryGetProperty("qa_fail_rate", out var fr) && fr.TryGetDouble(out var frv) && frv >= 0)
                priorVideoMultiplier = 1.0 + Math.Clamp(frv, 0, 1) * Math.Max(1, qaMaxRetries);
        }

        var ledger = await GetCostLedgerAsync(projectId, ct).ConfigureAwait(false);
        var multEarly = GetChargeMultiplier();
        var actual = SummarizeLedger(ledger, multEarly);

        var refinement = await BuildHistoryRefinementAsync(
            projectId, priorVideoMultiplier, qaRetryOnFail, qaMaxRetries, actual, ct)
            .ConfigureAwait(false);
        // H5: scale video estimate with blended expected takes (QA mult and/or learned p50).
        var qaVideoMultiplier = Math.Max(
            qaRetryOnFail ? refinement.AppliedVideoMultiplier : 1.0,
            refinement.ExpectedTakes > 0 ? refinement.ExpectedTakes : 1.0);
        var qaExpectedExtraGens = Math.Max(0, qaVideoMultiplier - 1.0);
        if (qaExpectedExtraGens > retries)
            retries = qaExpectedExtraGens;

        var blueprintClips = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var estimateBasis = blueprintClips.Any(s => s.Clips.Count > 0) ? "shot_plan" : "none";
        if (estimateBasis == "none")
        {
            // A2: post-import / fountain shortcut (before shot plan) — always estimate from screenplay.
            blueprintClips = await LoadScreenplayDerivedClipsAsync(projectId, cfg, ct).ConfigureAwait(false);
            if (blueprintClips.Any(s => s.Clips.Count > 0))
                estimateBasis = "screenplay";
        }

        var onDisk = IndexOnDiskClips(projectId);
        var heroes = await LoadHeroMapAsync(projectId, ct).ConfigureAwait(false);

        var draftCfg = CloneCfg(cfg, draftRes, retries);
        var heroCfg = CloneCfg(cfg, heroRes, retries);
        var draftRates = RatesFromConfig(draftCfg);
        var heroRates = RatesFromConfig(heroCfg);

        double spent = 0, remainingDraft = 0, remainingHero = 0, allDraft = 0, allHero = 0;
        int clipsOnDisk = 0, clipsMissing = 0, clipsTotal = 0;
        double secOnDisk = 0, secMissing = 0;
        var rows = new List<CostSceneRow>();

        foreach (var scene in blueprintClips)
        {
            var sn = scene.SceneNumber;
            var diskMap = onDisk.GetValueOrDefault(sn) ?? new Dictionary<int, bool>();
            heroes.TryGetValue(sn, out var heroResForScene);
            var isHero = !string.IsNullOrEmpty(heroResForScene);

            double sSpent = 0, sMiss = 0, sHero = 0, sAllD = 0, sAllH = 0;
            double dOn = 0, dMiss = 0;
            int nDisk = 0, nMiss = 0, nAll = 0;

            foreach (var clip in scene.Clips)
            {
                nAll++;
                var on = diskMap.GetValueOrDefault(clip.ClipNumber);
                if (on) nDisk++;
                else nMiss++;

                var spentRes = isHero ? (heroResForScene ?? heroRes) : draftRes;
                var spentEst = EstimateClip(clip, spentRes, rates, retries);
                var missEst = EstimateClip(clip, draftRes, draftRates, retries);
                var heroEst = EstimateClip(clip, heroRes, heroRates, retries);
                var allD = EstimateClip(clip, draftRes, draftRates, retries);
                var allH = EstimateClip(clip, heroRes, heroRates, retries);

                sAllD += allD.Usd;
                sAllH += allH.Usd;
                if (on)
                {
                    sSpent += spentEst.Usd;
                    dOn += spentEst.DurationSec;
                    if (!isHero)
                        sHero += heroEst.Usd;
                }
                else
                {
                    sMiss += missEst.Usd;
                    dMiss += missEst.DurationSec;
                }
            }

            spent += sSpent;
            remainingDraft += sMiss;
            remainingHero += sHero;
            allDraft += sAllD;
            allHero += sAllH;
            clipsOnDisk += nDisk;
            clipsMissing += nMiss;
            clipsTotal += nAll;
            secOnDisk += dOn;
            secMissing += dMiss;

            actual.ByScene.TryGetValue(sn.ToString(CultureInfo.InvariantCulture), out var actualScene);

            rows.Add(new CostSceneRow
            {
                Scene = sn,
                Setting = scene.Setting.Length > 60 ? scene.Setting[..60] : scene.Setting,
                ClipsTotal = nAll,
                ClipsOnDisk = nDisk,
                ClipsMissing = nMiss,
                IsHero = isHero,
                HeroResolution = heroResForScene,
                CharactersOnScreen = scene.CharactersOnScreen,
                LocationIds = scene.LocationIds,
                PrimaryLocationId = scene.PrimaryLocationId,
                SpentUsd = Math.Round(sSpent, 2),
                ActualUsd = Math.Round(actualScene, 2),
                RemainingDraftUsd = Math.Round(sMiss, 2),
                HeroUpgradeUsd = Math.Round(sHero, 2),
                AllDraftUsd = Math.Round(sAllD, 2),
                AllHeroUsd = Math.Round(sAllH, 2),
                DurationOnDiskSec = Math.Round(dOn, 1),
                DurationMissingSec = Math.Round(dMiss, 1),
            });
        }

        rows.Sort((a, b) => a.Scene.CompareTo(b.Scene));

        // A1: when any media is on disk, upgrade basis to remaining (spent + missing operational).
        if ((estimateBasis is "shot_plan" or "screenplay") && clipsOnDisk > 0)
            estimateBasis = "remaining";

        var scenarios = BuildScenarios(blueprintClips, onDisk, cfg, rates, retries, draftRes, heroRes);

        // Non-video scope (model-dependent): cast portraits, optional voice, music, planning.
        var videoModel = GetStr(cfg, "model_name", "");
        var imageModel = GetStr(cfg, "image_model_name", "");
        var planningModel = GetStr(cfg, "planning_model_name",
            GetStr(cfg, "chat_model_name", ""));
        var voiceModel = GetStr(cfg, "voice_model_name", "");
        var audioModel = GetStr(cfg, "audio_model_name", "");

        var castPlan = EstimateCharacterGeneration(projectId, rates, cfg);
        var voicePlan = EstimateVoiceGeneration(projectId, blueprintClips, rates, cfg);
        var musicPlan = EstimateMusicGeneration(blueprintClips, rates, cfg);
        var planningPlan = EstimatePlanningWork(blueprintClips, estimateBasis, rates, cfg);
        var reviewPlan = EstimateAutomatedReview(
            clipsTotal, rates, cfg, qaRetryOnFail, qaVideoMultiplier);

        var catalogVideoDraft = allDraft;
        var catalogVideoHero = allHero;
        var estimateByCategory = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [CostCategories.Screenplay] = Math.Round(planningPlan.Usd, 2),
            [CostCategories.Characters] = Math.Round(castPlan.Usd, 2),
            [CostCategories.Video] = Math.Round(allDraft, 2),
            [CostCategories.Voice] = Math.Round(voicePlan.Usd, 2),
            [CostCategories.Music] = Math.Round(musicPlan.Usd, 2),
            [CostCategories.Review] = Math.Round(reviewPlan.Usd, 2),
            [CostCategories.Other] = 0,
        };

        // Blend unit costs with portfolio averages when sample sizes allow.
        ApplyHistoryUnitCosts(estimateByCategory, refinement, clipsTotal);

        allDraft = estimateByCategory.GetValueOrDefault(CostCategories.Video);
        var nonVideo =
            estimateByCategory.GetValueOrDefault(CostCategories.Screenplay) +
            estimateByCategory.GetValueOrDefault(CostCategories.Characters) +
            estimateByCategory.GetValueOrDefault(CostCategories.Voice) +
            estimateByCategory.GetValueOrDefault(CostCategories.Music) +
            estimateByCategory.GetValueOrDefault(CostCategories.Review) +
            estimateByCategory.GetValueOrDefault(CostCategories.Other);
        var fullDraft = allDraft + nonVideo;
        var videoScale = catalogVideoDraft > 0.01 ? allDraft / catalogVideoDraft : 1.0;
        var fullHero = catalogVideoHero * videoScale + nonVideo;
        // Remaining first-pass: missing video + unfinished cast/voice/music (planning mostly already spent).
        var remainingExtras = castPlan.RemainingUsd + voicePlan.RemainingUsd + musicPlan.RemainingUsd
            + reviewPlan.RemainingUsd;
        remainingDraft += remainingExtras;

        var basisNote = estimateBasis switch
        {
            "shot_plan" => "Clip count from the shot plan.",
            "screenplay" => "Clip count estimated from screenplay scene lengths (before shot plan).",
            "remaining" => "Operational estimate: spent ledger + remaining planned clips.",
            _ => "Import a book or fountain screenplay to unlock a film estimate.",
        };

        // A1 clip source + confidence for DecisionCard / API consumers.
        var clipSource = estimateBasis switch
        {
            "shot_plan" => "blueprint",
            "screenplay" => "synthetic_screenplay",
            "remaining" => "remaining",
            _ => "none",
        };
        var estimateConfidence = estimateBasis switch
        {
            "remaining" => "best",
            "shot_plan" => "good",
            "screenplay" => "rough",
            _ => "very_low",
        };

        var mult = multEarly;
        // Snapshot list-rate category totals before applying charge multiplier for customer display.
        var estimateList = estimateByCategory.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        ApplyChargeMultiplierInPlace(estimateByCategory, mult);
        spent = ChargePricing.ToCharge(spent, mult);
        remainingDraft = ChargePricing.ToCharge(remainingDraft, mult);
        remainingHero = ChargePricing.ToCharge(remainingHero, mult);
        fullDraft = ChargePricing.ToCharge(fullDraft, mult);
        fullHero = ChargePricing.ToCharge(fullHero, mult);
        foreach (var row in rows)
        {
            row.SpentUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(row.SpentUsd, mult));
            row.RemainingDraftUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(row.RemainingDraftUsd, mult));
            row.HeroUpgradeUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(row.HeroUpgradeUsd, mult));
            row.AllDraftUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(row.AllDraftUsd, mult));
            row.AllHeroUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(row.AllHeroUsd, mult));
            // ActualUsd on scene rows = list × current multiplier (display only).
        }
        foreach (var sc in scenarios)
        {
            sc.FullFilmUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(sc.FullFilmUsd, mult));
            sc.RemainingMissingUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(sc.RemainingMissingUsd, mult));
            sc.RegenOnDiskUsd = ChargePricing.RoundMoney(ChargePricing.ToCharge(sc.RegenOnDiskUsd, mult));
        }

        var listFullDraft = estimateList.Values.Sum();
        var listFullHero = ChargePricing.RoundMoney(
            catalogVideoHero * videoScale +
            estimateList.GetValueOrDefault(CostCategories.Screenplay) +
            estimateList.GetValueOrDefault(CostCategories.Characters) +
            estimateList.GetValueOrDefault(CostCategories.Voice) +
            estimateList.GetValueOrDefault(CostCategories.Music) +
            estimateList.GetValueOrDefault(CostCategories.Review) +
            estimateList.GetValueOrDefault(CostCategories.Other));

        // A1/A3/H5/H6 decision-facing $ band and duration labels.
        // Prefer learned expected takes (p50 blend); fall back to QA video multiplier.
        var expectedTakes = Math.Max(1.0, refinement.ExpectedTakes > 0
            ? refinement.ExpectedTakes
            : qaVideoMultiplier);
        refinement.ExpectedTakes = expectedTakes;

        // H4/H6 — optional p25/p75 from global/project takes telemetry (fail-open).
        double takesP25 = 1.0;
        double takesP75 = Math.Max(expectedTakes, 1.5);
        var takesLearning = new CostTakesLearning
        {
            PriorTakes = Math.Max(1.0, refinement.PriorVideoMultiplier),
            ExpectedTakes = expectedTakes,
            BlendWeight = refinement.HistoryWeight,
            UsedLearnedTakes = refinement.LearnedTakesP50 is > 0,
        };
        try
        {
            if (_userDb is not null)
            {
                var g = await _userDb.GetTakesTelemetryStatsAsync(projectId: null, ct).ConfigureAwait(false);
                var p = await _userDb.GetTakesTelemetryStatsAsync(projectId, ct).ConfigureAwait(false);
                takesLearning.GlobalClipSamples = g.ClipSampleCount;
                takesLearning.ProjectClipSamples = p.ClipSampleCount;
                if (g.ClipSampleCount > 0)
                {
                    takesLearning.GlobalP25 = g.P25TakesPerClip;
                    takesLearning.GlobalP50 = g.P50TakesPerClip;
                    takesLearning.GlobalP75 = g.P75TakesPerClip;
                }
                if (p.ClipSampleCount > 0)
                {
                    takesLearning.ProjectMeanTakes = p.MeanTakesPerClip;
                    takesLearning.ProjectRegenRate = p.RegenRate;
                }
                var rangeSrc = p.SufficientForBlend ? p : g.SufficientForBlend ? g : null;
                if (rangeSrc is not null)
                {
                    takesP25 = Math.Max(1.0, rangeSrc.P25TakesPerClip);
                    takesP75 = Math.Max(takesP25, rangeSrc.P75TakesPerClip);
                    takesLearning.SufficientForRange = true;
                    takesLearning.HistoryLabel =
                        $"typical ~{rangeSrc.P50TakesPerClip:0.##} takes/clip from studio history (n={rangeSrc.ClipSampleCount})";
                }
                else if (g.ClipSampleCount > 0 || p.ClipSampleCount > 0)
                {
                    takesLearning.HistoryLabel =
                        $"learning takes/clip (n={Math.Max(g.ClipSampleCount, p.ClipSampleCount)}; need {UserDatabaseService.MinTakesClipSamples})";
                }
                if (p.ClipSampleCount > 0 && expectedTakes > 0)
                {
                    var delta = p.MeanTakesPerClip - expectedTakes;
                    takesLearning.CalibrationLabel =
                        $"This project ~{p.MeanTakesPerClip:0.##} takes/clip actual vs ~{expectedTakes:0.##} in estimate" +
                        (Math.Abs(delta) < 0.05 ? " (on track)." : delta > 0 ? " (running hotter)." : " (running cooler).");
                }
            }
        }
        catch
        {
            // H9 fail-open
        }

        // first-pass unit cost ≈ point / expectedTakes (point already includes expected takes via retries)
        var costPoint = Math.Round(fullDraft, 2);
        var firstPassUnit = expectedTakes > 0.01 ? costPoint / expectedTakes : costPoint;
        var costLow = Math.Round(firstPassUnit * takesP25, 2);
        var costHigh = Math.Round(firstPassUnit * takesP75, 2);
        if (costLow > costPoint) costLow = costPoint;
        if (costHigh < costPoint) costHigh = costPoint;

        var durationSec = secOnDisk + secMissing;
        double? durationMinutes = durationSec > 0.5
            ? Math.Round(durationSec / 60.0, 1)
            : null;
        var durationLabel = durationMinutes is > 0
            ? $"~{durationMinutes:0.#} min"
            : "duration TBD";
        var showRange = estimateBasis != "none" && costPoint > 0 &&
            (takesLearning.SufficientForRange || estimateConfidence is "rough" or "very_low") &&
            Math.Abs(costHigh - costLow) >= 0.5;
        var costLabel = estimateBasis == "none" || costPoint <= 0
            ? "—"
            : showRange
                ? $"~${costLow:0.##}–${costHigh:0.##}"
                : $"~${costPoint:0.##}";

        return new CostReport
        {
            ProjectId = projectId,
            DraftResolution = draftRes,
            HeroResolution = heroRes,
            ModelName = videoModel,
            VideoProvider = ResolveVideoProvider(cfg, videoModel),
            ImageModelName = imageModel,

            PlanningModelName = planningModel,
            VoiceModelName = voicePlan.Included ? voiceModel : null,
            EstimateBasis = estimateBasis,
            ClipSource = clipSource,
            EstimateConfidence = estimateConfidence,
            CostLowUsd = estimateBasis == "none" ? null : costLow,
            CostPointUsd = estimateBasis == "none" ? null : costPoint,
            CostHighUsd = estimateBasis == "none" ? null : costHigh,
            DurationMinutes = durationMinutes,
            DurationLabel = durationLabel,
            CostLabel = costLabel,
            TakesLearning = takesLearning,
            VoiceIncludedInEstimate = voicePlan.Included,
            ChargeMultiplier = mult,
            OutputRateDraft = OutputRate(draftRes, draftRates),
            OutputRateHero = OutputRate(heroRes, heroRates),
            AssumeAvgRetries = retries,
            Summary = new CostReportSummary
            {
                ClipsTotal = clipsTotal,
                ClipsOnDisk = clipsOnDisk,
                ClipsMissing = clipsMissing,
                SecOnDisk = Math.Round(secOnDisk, 1),
                SecMissing = Math.Round(secMissing, 1),
                SpentUsd = Math.Round(spent, 2),
                ActualUsd = actual.ActualUsd,
                ActualEvents = actual.EventCount,
                ActualVideoJobs = actual.VideoJobs,
                ActualVideoSec = actual.VideoSec,
                RemainingFirstPassUsd = Math.Round(remainingDraft, 2),
                RemainingHeroUpgradeUsd = Math.Round(remainingHero, 2),
                FinishDraftUsd = Math.Round(spent + remainingDraft, 2),
                FinishDraftPlusHeroUsd = Math.Round(spent + remainingDraft + remainingHero, 2),
                FinishFromActualUsd = Math.Round(actual.ActualUsd + remainingDraft, 2),
                FullFilmAllDraftUsd = Math.Round(fullDraft, 2),
                FullFilmAllHeroUsd = Math.Round(fullHero, 2),
                FullFilmAllDraftListUsd = Math.Round(listFullDraft, 2),
                FullFilmAllHeroListUsd = listFullHero,
                ScenesWithMedia = rows.Count(r => r.ClipsOnDisk > 0),
                ScenesHero = rows.Count(r => r.IsHero),
                ScenesTotal = rows.Count,
            },
            Actual = actual,
            EstimateByCategory = estimateByCategory,
            EstimateByCategoryListRate = estimateList,
            Refinement = refinement,
            Scenes = rows,
            Scenarios = scenarios,
            RecentEvents = ledger
                .OrderByDescending(e => e.Ts ?? "")
                .Take(Math.Clamp(recentLimit, 1, 200))
                .ToList(),
            Notes =
                basisNote + " " +
                $"Rates from selected models (video={videoModel}, image={imageModel}" +
                (voicePlan.Included ? $", voice={voiceModel}" : "") + "). " +
                $"Charge multiplier ×{mult:0.##} on estimates and new actual charges. " +
                (qaRetryOnFail
                    ? $"Quality gate retry ON (admin auto-regen; video ×{qaVideoMultiplier:0.##}). "
                    : "Quality gate retry OFF. ") +
                (string.IsNullOrWhiteSpace(refinement.Notes) ? "" : refinement.Notes + " ") +
                "Actual display = list rates in cost_ledger × admin charge multiplier (list rates only in storage).",
        };
    }

    private static void ApplyChargeMultiplierInPlace(Dictionary<string, double> byCategory, double mult)
    {
        foreach (var key in byCategory.Keys.ToList())
            byCategory[key] = ChargePricing.RoundMoney(ChargePricing.ToCharge(byCategory[key], mult));
    }

    public async Task<CostBackfillResult> BackfillFromDiskAsync(
        string projectId,
        bool onlyMissing = true,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var defaultRates = RatesFromConfig(cfg);
        var ledger = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var seen = new HashSet<(int, int)>();
        foreach (var e in ledger)
        {
            if (!string.Equals(GetRawKind(e), "video", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetInt(e, "scene", out var sn) && TryGetInt(e, "clip", out var cn))
                seen.Add((sn, cn));
        }

        var blueprint = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var onDisk = IndexOnDiskClips(projectId);
        var clipJobs = await LoadClipJobsAsync(projectId, ct).ConfigureAwait(false);
        var defaultRes = GetStr(cfg, "resolution", "480p");
        var defaultModel = GetStr(cfg, "model_name", "");
        var imageModel = GetStr(cfg, "image_model_name", "");
        var defaultDur = GetDouble(cfg, "duration_seconds", 8);
        var assumeRef = GetBool(defaultRates, "assume_ref_image_per_clip", true);

        var added = 0;
        var skipped = 0;
        foreach (var scene in blueprint)
        {
            var diskMap = onDisk.GetValueOrDefault(scene.SceneNumber) ?? new Dictionary<int, bool>();
            foreach (var clip in scene.Clips)
            {
                if (!diskMap.GetValueOrDefault(clip.ClipNumber))
                {
                    skipped++;
                    continue;
                }

                if (onlyMissing && seen.Contains((scene.SceneNumber, clip.ClipNumber)))
                {
                    skipped++;
                    continue;
                }

                clipJobs.TryGetValue($"{scene.SceneNumber}_{clip.ClipNumber}", out var job);
                var duration = clip.DurationSec > 0 ? clip.DurationSec : defaultDur;
                if (job is not null && job.TryGetValue("duration_sec", out var ds) &&
                    ds.TryGetDouble(out var jdur) && jdur > 0)
                    duration = jdur;

                var res = defaultRes;
                if (job is not null && job.TryGetValue("resolution", out var jr) &&
                    jr.ValueKind == JsonValueKind.String && jr.GetString() is { Length: > 0 } rs)
                    res = rs;

                var model = defaultModel;
                if (job is not null && job.TryGetValue("model", out var jm) &&
                    jm.ValueKind == JsonValueKind.String && jm.GetString() is { Length: > 0 } md)
                    model = md;

                var isExtend = string.Equals(
                    clip.Continuation, "extend_previous", StringComparison.OrdinalIgnoreCase);
                var rates = RatesFromModels(model, imageModel, cfg);
                var priced = PriceVideo(duration, res, rates, assumeRef, isExtend, attempts: 1);
                var listUsd = priced.Usd;

                var evt = new Dictionary<string, object?>
                {
                    ["kind"] = "video",
            ["category"] = CostCategories.Video,
                    ["scene"] = scene.SceneNumber,
                    ["clip"] = clip.ClipNumber,
                    ["model"] = model,
                    ["provider"] = rates.TryGetValue("video_provider", out var vp) ? vp : null,
                    ["pricing_source"] = rates.TryGetValue("video_pricing_source", out var vps) ? vps : null,
                    ["request_id"] = job is not null && job.TryGetValue("request_id", out var rid)
                        ? rid.GetString() ?? ""
                        : "",
                    ["has_ref_image"] = assumeRef,
                    ["is_extend"] = isExtend,
                    ["source"] = "backfill",
                    ["duration_sec"] = priced.DurationSec,
                    ["attempts"] = 1.0,
                    ["resolution"] = res,
                    ["output_rate_per_sec"] = priced.RatePerSec,
                    ["video_output_usd"] = priced.VideoOut,
                    ["ref_image_usd"] = priced.RefImg,
                    ["extend_input_usd"] = priced.ExtendIn,
                    ["list_usd"] = listUsd,
                    ["usd"] = listUsd,
                    ["currency"] = "USD",
                    ["extra"] = new Dictionary<string, object?> { ["backfill"] = true },
                };
                await AppendCostEventAsync(projectId, evt, save: true, ct).ConfigureAwait(false);
                seen.Add((scene.SceneNumber, clip.ClipNumber));
                added++;
            }
        }

        var summary = SummarizeLedger(await GetCostLedgerAsync(projectId, ct).ConfigureAwait(false), GetChargeMultiplier());
        return new CostBackfillResult
        {
            Added = added,
            Skipped = skipped,
            LedgerEvents = summary.EventCount,
            ActualUsd = summary.ActualUsd,
        };
    }

    /// <param name="durationSec">
    /// Billed length — prefer probed final file seconds after silence trim; fall back to API request duration.
    /// </param>
    /// <param name="requestedDurationSec">
    /// Optional API-requested duration when <paramref name="durationSec"/> is measured (for audit).
    /// </param>
    /// <param name="takeKind">
    /// H1/H2 trigger: <c>initial</c> | <c>user_regen</c> | <c>stale_regen</c> | <c>qa_auto</c> | <c>fill_holes</c>.
    /// </param>
    public async Task RecordVideoGenerationAsync(
        string projectId,
        int scene,
        int clip,
        double durationSec,
        string resolution,
        string model,
        bool hasRefImage = false,
        bool isExtend = false,
        string? requestId = null,
        double? requestedDurationSec = null,
        string? userId = null,
        string? keyMode = null,
        string? takeKind = null,
        string? stableBeatId = null,
        bool? hadCharRefs = null,
        bool? hadLocRef = null,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        // Price this event with the model that actually ran (vendor catalog), not a stale config table.
        var rates = RatesFromModels(
            videoModelId: model,
            imageModelId: GetStr(cfg, "image_model_name", ""),
            cfgOverrides: cfg);
        var priced = PriceVideo(durationSec, resolution, rates, hasRefImage, isExtend, 1);
        var listUsd = priced.Usd;
        var mult = GetChargeMultiplier(); // display / credit only — not stored on the event
        var chargeUsd = ChargePricing.ToCharge(listUsd, mult);

        // H1: take_index + minutes_since_prev from prior video events for this scene+clip.
        var (takeIndex, minutesSincePrev) = await ComputeTakeIndexAsync(projectId, scene, clip, ct)
            .ConfigureAwait(false);
        var resolvedKind = VideoTakeKinds.Normalize(
            takeKind,
            fallback: isExtend ? VideoTakeKinds.UserRegen : VideoTakeKinds.Initial);

        var evt = new Dictionary<string, object?>
        {
            ["kind"] = "video",
            ["category"] = CostCategories.Video,
            ["scene"] = scene,
            ["clip"] = clip,
            ["model"] = model,
            ["provider"] = rates.TryGetValue("video_provider", out var vp) ? vp : null,
            ["pricing_source"] = rates.TryGetValue("video_pricing_source", out var vps) ? vps : null,
            ["request_id"] = requestId ?? "",
            ["has_ref_image"] = hasRefImage,
            ["is_extend"] = isExtend,
            ["source"] = "list_rate",
            // Primary duration used for pricing (probed when available)
            ["duration_sec"] = priced.DurationSec,
            ["attempts"] = 1.0,
            ["resolution"] = resolution,
            ["output_rate_per_sec"] = priced.RatePerSec,
            ["video_output_usd"] = priced.VideoOut,
            ["ref_image_usd"] = priced.RefImg,
            ["extend_input_usd"] = priced.ExtendIn,
            // List rate only in ledger — multiplier applied at display / credit debit time.
            ["list_usd"] = listUsd,
            ["usd"] = listUsd,
            ["currency"] = "USD",
            ["user_id"] = userId ?? "",
            // I13 / H1 multi-user take telemetry
            ["key_mode"] = string.IsNullOrWhiteSpace(keyMode) ? "personal" : keyMode.Trim().ToLowerInvariant(),
            ["take_kind"] = resolvedKind,
            ["trigger"] = resolvedKind, // alias for plan / offline aggregators
            ["take_index"] = takeIndex,
            ["had_char_refs"] = hadCharRefs ?? hasRefImage,
            ["had_loc_ref"] = hadLocRef ?? false,
        };
        if (!string.IsNullOrWhiteSpace(stableBeatId))
            evt["stable_beat_id"] = stableBeatId.Trim();
        if (minutesSincePrev is not null)
            evt["minutes_since_prev_take"] = Math.Round(minutesSincePrev.Value, 2);
        if (requestedDurationSec is > 0 &&
            Math.Abs(requestedDurationSec.Value - priced.DurationSec) >= 0.05)
        {
            evt["request_duration_sec"] = Math.Round(requestedDurationSec.Value, 3);
            evt["duration_source"] = "probed";
        }
        else
        {
            evt["duration_source"] = requestedDurationSec is > 0 ? "request" : "probed_or_request";
        }

        await AppendCostEventAsync(projectId, evt, save: true, ct).ConfigureAwait(false);

        // H1/H9 — dual-write take to studio SQLite for aggregates (fail-open).
        if (_userDb is not null)
        {
            var contribute = true;
            try
            {
                // Project opt-out: cost_estimates.contribute_to_studio_averages = false
                if (cfg.TryGetValue("cost_estimates", out var ceOpt) &&
                    ceOpt.ValueKind == JsonValueKind.Object &&
                    ceOpt.TryGetProperty("contribute_to_studio_averages", out var cta) &&
                    cta.ValueKind is JsonValueKind.False)
                    contribute = false;
            }
            catch { /* fail-open contribute */ }

            await _userDb.TryInsertVideoTakeEventAsync(new VideoTakeEventRecord
            {
                ProjectId = projectId,
                UserId = userId,
                Scene = scene,
                Clip = clip,
                TakeIndex = takeIndex,
                TakeKind = resolvedKind,
                Model = model,
                Resolution = resolution,
                ListUsd = listUsd,
                DurationSec = priced.DurationSec,
                KeyMode = string.IsNullOrWhiteSpace(keyMode) ? "personal" : keyMode.Trim().ToLowerInvariant(),
                StableBeatId = stableBeatId,
                HadCharRefs = hadCharRefs ?? hasRefImage,
                HadLocRef = hadLocRef ?? false,
                MinutesSincePrevTake = minutesSincePrev,
                ContributeToStudioAverages = contribute,
            }, ct).ConfigureAwait(false);
        }

        if (_credits is not null && chargeUsd > 0)
        {
            await _credits.TryDebitUsageAsync(
                userId,
                chargeUsd,
                projectId,
                metaKind: "video",
                note: $"S{scene:D2}C{clip} {model} {priced.DurationSec:F1}s ×{mult:0.##}",
                ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// H3 — optional one-click reason after user regen. Updates ledger + studio take table (fail-open).
    /// </summary>
    public async Task<bool> SetTakeReasonAsync(
        string projectId,
        int scene,
        int clip,
        string reason,
        int? takeIndex = null,
        CancellationToken ct = default)
    {
        var r = VideoTakeReasons.NormalizeOptional(reason);
        if (r is null || string.IsNullOrWhiteSpace(projectId)) return false;

        var dbOk = false;
        if (_userDb is not null)
            dbOk = await _userDb.TrySetVideoTakeReasonAsync(projectId, scene, clip, r, takeIndex, ct)
                .ConfigureAwait(false);

        // Also stamp project cost_ledger last matching video event.
        var ledgerOk = false;
        try
        {
            var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    root[p.Name] = p.Value.Deserialize<object>();

                if (doc.RootElement.TryGetProperty("cost_ledger", out var ledger) &&
                    ledger.ValueKind == JsonValueKind.Array)
                {
                    var list = ledger.EnumerateArray().Select(x => x.Clone()).ToList();
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        var e = list[i];
                        if (!string.Equals(GetRawKind(e), "video", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TryGetInt(e, "scene", out var sn) || sn != scene) continue;
                        if (!TryGetInt(e, "clip", out var cn) || cn != clip) continue;
                        if (takeIndex is > 0 && TryGetInt(e, "take_index", out var ti) && ti != takeIndex)
                            continue;
                        var dict = e.Deserialize<Dictionary<string, object?>>()
                                   ?? new Dictionary<string, object?>();
                        dict["reason"] = r;
                        list[i] = JsonSerializer.SerializeToElement(dict);
                        ledgerOk = true;
                        break;
                    }
                    if (ledgerOk)
                    {
                        root["cost_ledger"] = list.Select(x => x.Deserialize<object>()).ToList();
                        var json = JsonSerializer.Serialize(root, JsonDefaults.Indented);
                        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
            // H9 fail-open
        }

        return dbOk || ledgerOk;
    }

    /// <summary>H1 — next take_index and minutes since last take for scene+clip.</summary>
    private async Task<(int TakeIndex, double? MinutesSincePrev)> ComputeTakeIndexAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct)
    {
        var raw = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var prior = 0;
        DateTimeOffset? lastTs = null;
        foreach (var e in raw)
        {
            if (!string.Equals(GetRawKind(e), "video", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetInt(e, "scene", out var sn) || sn != scene) continue;
            if (!TryGetInt(e, "clip", out var cn) || cn != clip) continue;
            prior++;
            if (e.TryGetProperty("ts", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
            {
                if (lastTs is null || parsed > lastTs) lastTs = parsed;
            }
        }

        double? minutes = null;
        if (lastTs is not null)
            minutes = Math.Max(0, (DateTimeOffset.Now - lastTs.Value).TotalMinutes);
        return (prior + 1, minutes);
    }

    public async Task<IReadOnlyList<CostEvent>> GetCostLedgerAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var mult = GetChargeMultiplier();
        var raw = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var events = new List<CostEvent>();
        foreach (var e in raw)
        {
            var evt = ParseEvent(e);
            // Display only: list × current admin multiplier (ledger still holds list rate).
            var listRate = ChargePricing.ResolveListUsd(evt.Usd, evt.ListUsd, evt.ChargeMultiplier);
            evt.ListUsd = listRate;
            evt.Usd = ChargePricing.ToCharge(listRate, mult);
            events.Add(evt);
        }
        return events;
    }

    public async Task RecordImageGenerationAsync(
        string projectId,
        int nImages,
        string model,
        bool quality = true,
        string? character = null,
        string? userId = null,
        CancellationToken ct = default)
    {
        var n = Math.Max(0, nImages);
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Image)
                    ?? SupportedModelCatalog.Find(model);
        double unit;
        bool isEstimated;
        if (entry?.ImageCostPerImage is { } catalogUnit)
        {
            unit = catalogUnit;
            isEstimated = false;
        }
        else if (entry?.LabMode == true)
        {
            unit = 0;
            isEstimated = true;
        }
        else
            throw new InvalidOperationException(
                $"Image model '{entry?.Id ?? "(null)"}' has no imageCostPerImage in models_catalog.json. "
                + "Add the vendor list price — do not invent a default in Engine.");
        var listUsd = Math.Round(unit * n, 4);
        var mult = GetChargeMultiplier();
        var chargeUsd = ChargePricing.ToCharge(listUsd, mult); // credit debit only
        await AppendCostEventAsync(projectId, new Dictionary<string, object?>
        {
            ["kind"] = "image",
            ["category"] = CostCategories.Characters,
            ["model"] = entry?.Id ?? model,
            ["character"] = character ?? "",
            ["n_images"] = n,
            ["unit_usd"] = unit,
            ["list_usd"] = listUsd,
            ["usd"] = listUsd,
            ["currency"] = "USD",
            ["source"] = "list_rate",
            ["pricing_source"] = isEstimated ? "estimated_fallback" : "model_catalog",
            ["provider"] = entry?.ProviderId ?? "",
            ["user_id"] = userId ?? "",
        }, save: true, ct).ConfigureAwait(false);

        if (_credits is not null && chargeUsd > 0)
        {
            await _credits.TryDebitUsageAsync(
                userId,
                chargeUsd,
                projectId,
                metaKind: "image",
                note: string.IsNullOrWhiteSpace(character)
                    ? $"{n}× {model} ×{mult:0.##}"
                    : $"{n}× {model} ({character}) ×{mult:0.##}",
                ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls one already-priced API call's list-rate estimate into the project's <c>cost_ledger</c>,
    /// so chat/vision spend (screenplay, cast, shot-plan classifiers, review) shows up in the same
    /// "actual spend" total as video/image generation instead of always reading $0 for those
    /// categories. Video/image already log their own richer event via
    /// <see cref="RecordVideoGenerationAsync"/>/<see cref="RecordImageGenerationAsync"/> — callers
    /// must not call this for those kinds, or spend double-counts (see
    /// <c>ProjectTelemetryService.LogApiCallAsync</c>'s kind filter).
    /// </summary>
    public async Task RecordApiCallSpendAsync(ApiCallTelemetry rec, CancellationToken ct = default)
    {
        var projectId = rec.ProjectId;
        var listUsd = rec.EstimatedUsd ?? 0;
        if (string.IsNullOrWhiteSpace(projectId) || listUsd <= 0) return;

        var mult = GetChargeMultiplier();
        var chargeUsd = ChargePricing.ToCharge(listUsd, mult); // credit debit only

        var evt = new Dictionary<string, object?>
        {
            ["kind"] = rec.Kind,
            ["category"] = rec.Category ?? CostCategories.Resolve(rec.Kind, rec.Mode),
            ["model"] = rec.Model,
            ["provider"] = rec.Provider,
            ["mode"] = rec.Mode,
            ["request_id"] = rec.RequestId ?? "",
            ["source"] = "list_rate",
            ["list_usd"] = Math.Round(listUsd, 6),
            ["usd"] = Math.Round(listUsd, 6),
            ["currency"] = "USD",
            ["user_id"] = rec.UserId ?? "",
        };
        // Only book-level classifiers with no single scene/clip/character omit these — keep the
        // event dict free of literal nulls rather than writing "scene": null for every such call.
        if (rec.Scene is { } scene) evt["scene"] = scene;
        if (rec.Clip is { } clip) evt["clip"] = clip;
        if (!string.IsNullOrWhiteSpace(rec.CharKey)) evt["char_key"] = rec.CharKey;

        await AppendCostEventAsync(projectId, evt, save: true, ct).ConfigureAwait(false);

        // Debit customer charge for TTS/chat/review/etc. (video/image debit elsewhere).
        if (_credits is not null && chargeUsd > 0 && !string.IsNullOrWhiteSpace(rec.UserId))
        {
            await _credits.TryDebitUsageAsync(
                rec.UserId,
                chargeUsd,
                projectId,
                metaKind: rec.Kind ?? "api",
                note: $"{rec.Kind}/{rec.Mode} ×{mult:0.##}",
                ct: ct).ConfigureAwait(false);
        }
    }

    // ---- internals ----

    private List<CostScenarioRow> BuildScenarios(
        List<BlueprintSceneClips> scenes,
        Dictionary<int, Dictionary<int, bool>> onDisk,
        Dictionary<string, JsonElement> cfg,
        Dictionary<string, object?> baseRates,
        double retries,
        string draftRes,
        string heroRes)
    {
        var model = GetStr(cfg, "model_name", "");
        var rows = new List<CostScenarioRow>();
        foreach (var res in new[] { "480p", "720p", "1080p" })
        {
            var rates = RatesFromConfig(CloneCfg(cfg, res, retries));
            double full = 0, missing = 0, regen = 0;
            foreach (var scene in scenes)
            {
                var disk = onDisk.GetValueOrDefault(scene.SceneNumber) ?? new Dictionary<int, bool>();
                foreach (var clip in scene.Clips)
                {
                    var est = EstimateClip(clip, res, rates, retries);
                    full += est.Usd;
                    if (disk.GetValueOrDefault(clip.ClipNumber))
                        regen += est.Usd;
                    else
                        missing += est.Usd;
                }
            }

            rows.Add(new CostScenarioRow
            {
                Label = $"{model} @ {res}",
                Resolution = res,
                ModelName = model,
                RatePerSec = OutputRate(res, rates),
                FullFilmUsd = Math.Round(full, 2),
                RemainingMissingUsd = Math.Round(missing, 2),
                RegenOnDiskUsd = Math.Round(regen, 2),
                AssumeAvgRetries = retries,
            });
        }

        // highlight draft/hero even if already listed
        _ = draftRes;
        _ = heroRes;
        return rows;
    }

    private static (double Usd, double DurationSec) EstimateClip(
        BlueprintClip clip,
        string resolution,
        Dictionary<string, object?> rates,
        double retries)
    {
        var duration = clip.DurationSec > 0 ? clip.DurationSec : 8;
        var attempts = 1.0 + Math.Max(0, retries);
        var outRate = OutputRate(resolution, rates);
        var baseRate = BaseRate(resolution, rates);
        var videoOut = (duration * outRate + baseRate) * attempts;
        var refImg = 0.0;
        if (GetBool(rates, "assume_ref_image_per_clip", true))
            refImg = RequireRate(rates, "video_input_image") * attempts;
        var extend = 0.0;
        if (string.Equals(clip.Continuation, "extend_previous", StringComparison.OrdinalIgnoreCase))
            extend = duration * RequireRate(rates, "video_input_per_sec") * attempts;
        return (videoOut + refImg + extend, duration);
    }

    internal static (double Usd, double DurationSec, double RatePerSec, double VideoOut, double RefImg, double ExtendIn)
        PriceVideo(
            double durationSec,
            string resolution,
            Dictionary<string, object?> rates,
            bool hasRef,
            bool isExtend,
            double attempts)
    {
        var duration = Math.Max(0, durationSec);
        attempts = Math.Max(1, attempts);
        var outRate = OutputRate(resolution, rates);
        var baseRate = BaseRate(resolution, rates);
        var videoOut = (duration * outRate + baseRate) * attempts;
        var refImg = hasRef ? RequireRate(rates, "video_input_image") * attempts : 0;
        var extend = isExtend
            ? duration * RequireRate(rates, "video_input_per_sec") * attempts
            : 0;
        var usd = Math.Round(videoOut + refImg + extend, 4);
        return (usd, duration, outRate, Math.Round(videoOut, 4), Math.Round(refImg, 4), Math.Round(extend, 4));
    }

    private static CostLedgerSummary SummarizeLedger(IReadOnlyList<CostEvent> events, double currentMultiplier = 1.0)
    {
        double total = 0, listTotal = 0, videoSec = 0;
        var byKind = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byScene = new Dictionary<string, double>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, double>(StringComparer.Ordinal);
        var videoJobs = 0;
        var imageJobs = 0;
        var mult = ChargePricing.ClampMultiplier(currentMultiplier);
        foreach (var e in events)
        {
            var list = ChargePricing.ResolveListUsd(e.Usd, e.ListUsd, e.ChargeMultiplier);
            var charge = ChargePricing.ToCharge(list, mult);

            total += charge;
            listTotal += list;
            var kind = string.IsNullOrEmpty(e.Kind) ? "other" : e.Kind;
            byKind[kind] = byKind.GetValueOrDefault(kind) + charge;
            if (e.Scene is int sn)
            {
                var key = sn.ToString(CultureInfo.InvariantCulture);
                byScene[key] = byScene.GetValueOrDefault(key) + charge;
            }
            if (!string.IsNullOrEmpty(e.Model))
                byModel[e.Model] = byModel.GetValueOrDefault(e.Model) + charge;
            if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                videoJobs++;
                videoSec += e.DurationSec ?? 0;
            }
            else if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                imageJobs++;
            }
        }

        return new CostLedgerSummary
        {
            ActualUsd = Math.Round(total, 2),
            ListRateUsd = Math.Round(listTotal, 2),
            EventCount = events.Count,
            VideoJobs = videoJobs,
            ImageJobs = imageJobs,
            VideoSec = Math.Round(videoSec, 1),
            ByKind = byKind.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            ByScene = byScene.OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            ByModel = byModel.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
        };
    }

    private static CostEvent ParseEvent(JsonElement e)
    {
        return new CostEvent
        {
            Id = e.TryGetProperty("id", out var id) ? id.GetString() : null,
            Ts = e.TryGetProperty("ts", out var ts) ? ts.GetString() : null,
            Kind = e.TryGetProperty("kind", out var k) ? k.GetString() ?? "other" : "other",
            Category = CostCategories.Resolve(
                e.TryGetProperty("kind", out var k2) ? k2.GetString() : null,
                e.TryGetProperty("mode", out var mo) ? mo.GetString() : null,
                e.TryGetProperty("category", out var cat) ? cat.GetString() : null),
            Scene = TryGetInt(e, "scene", out var sn) ? sn : null,
            Clip = TryGetInt(e, "clip", out var cn) ? cn : null,
            Model = e.TryGetProperty("model", out var m) ? m.GetString() : null,
            Resolution = e.TryGetProperty("resolution", out var r) ? r.GetString() : null,
            DurationSec = TryGetDouble(e, "duration_sec", out var d) ? d : null,
            Usd = TryGetDouble(e, "usd", out var u) ? u : 0,
            ListUsd = TryGetDouble(e, "list_usd", out var lu) ? lu : null,
            ChargeMultiplier = TryGetDouble(e, "charge_multiplier", out var cm) ? cm : null,
            Currency = e.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD",
            Source = e.TryGetProperty("source", out var s) ? s.GetString() : null,
            Character = e.TryGetProperty("character", out var ch) ? ch.GetString() : null,
            OutputRatePerSec = TryGetDouble(e, "output_rate_per_sec", out var or) ? or : null,
            HasRefImage = e.TryGetProperty("has_ref_image", out var hr) &&
                          (hr.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? hr.GetBoolean()
                : null,
            IsExtend = e.TryGetProperty("is_extend", out var ie) &&
                       (ie.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? ie.GetBoolean()
                : null,
            UserId = e.TryGetProperty("user_id", out var uid) ? uid.GetString() : null,
            KeyMode = e.TryGetProperty("key_mode", out var km) ? km.GetString() : null,
            TakeKind = e.TryGetProperty("take_kind", out var tk)
                ? tk.GetString()
                : e.TryGetProperty("trigger", out var tr) ? tr.GetString() : null,
            TakeIndex = TryGetInt(e, "take_index", out var ti) ? ti : null,
            StableBeatId = e.TryGetProperty("stable_beat_id", out var sb) ? sb.GetString() : null,
            HadCharRefs = e.TryGetProperty("had_char_refs", out var hcr) &&
                          (hcr.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? hcr.GetBoolean()
                : null,
            HadLocRef = e.TryGetProperty("had_loc_ref", out var hlr) &&
                        (hlr.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? hlr.GetBoolean()
                : null,
            MinutesSincePrevTake = TryGetDouble(e, "minutes_since_prev_take", out var msp) ? msp : null,
            Reason = e.TryGetProperty("reason", out var rr) ? rr.GetString() : null,
        };
    }

    private Task<List<JsonElement>> GetCostLedgerRawAsync(
        string projectId,
        CancellationToken ct = default) =>
        GetCostLedgerRawCoreAsync(projectId, ct);

    private async Task<List<JsonElement>> GetCostLedgerRawCoreAsync(
        string projectId,
        CancellationToken ct)
    {
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path))
            return new List<JsonElement>();
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("cost_ledger", out var ledger) ||
                ledger.ValueKind != JsonValueKind.Array)
                return new List<JsonElement>();
            return ledger.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    private async Task AppendCostEventAsync(
        string projectId,
        Dictionary<string, object?> evt,
        bool save,
        CancellationToken ct = default)
    {
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);

        JsonDocument rawDoc;
        if (File.Exists(path))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                rawDoc = JsonDocument.Parse(bytes);
            }
            catch
            {
                rawDoc = JsonDocument.Parse("{}");
            }
        }
        else
        {
            rawDoc = JsonDocument.Parse("{}");
        }

        using (rawDoc)
        {
            var ledgerList = new List<object?>();
            if (rawDoc.RootElement.TryGetProperty("cost_ledger", out var existing) &&
                existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existing.EnumerateArray())
                    ledgerList.Add(item.Deserialize<object>());
            }

            var ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            evt.TryAdd("id", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{ledgerList.Count:D4}");
            evt.TryAdd("ts", ts);
            evt.TryAdd("currency", "USD");
            ledgerList.Add(evt);
            if (ledgerList.Count > 20000)
                ledgerList = ledgerList.TakeLast(20000).ToList();

            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rawDoc.RootElement.EnumerateObject())
            {
                if (p.Name is "cost_ledger" or "cost_totals")
                    continue;
                merged[p.Name] = p.Value.Deserialize<object>();
            }

            merged["cost_ledger"] = ledgerList;
            var prevUsd = 0.0;
            var prevEvents = 0;
            if (rawDoc.RootElement.TryGetProperty("cost_totals", out var tot) &&
                tot.ValueKind == JsonValueKind.Object)
            {
                if (tot.TryGetProperty("usd", out var u) && u.TryGetDouble(out var ud))
                    prevUsd = ud;
                if (tot.TryGetProperty("events", out var ev) && ev.TryGetInt32(out var en))
                    prevEvents = en;
            }

            var addUsd = 0.0;
            if (evt.TryGetValue("usd", out var usdObj) && usdObj is not null)
                addUsd = Convert.ToDouble(usdObj, CultureInfo.InvariantCulture);

            merged["cost_totals"] = new Dictionary<string, object?>
            {
                ["usd"] = Math.Round(prevUsd + addUsd, 4),
                ["events"] = prevEvents + 1,
                ["updated_at"] = ts,
            };

            if (save)
            {
                var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
                await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> StatePathAsync(string projectId, CancellationToken ct)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var meta = Path.Combine(dir, "project.json");
        var name = "pipeline_state.json";
        if (File.Exists(meta))
        {
            try
            {
                await using var stream = File.OpenRead(meta);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("state_file", out var sf) &&
                    sf.GetString() is { Length: > 0 } n)
                    name = n;
            }
            catch { /* ignore */ }
        }
        return Path.Combine(dir, name);
    }

    private async Task<Dictionary<string, JsonElement>> LoadConfigMapAsync(
        string projectId,
        CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return new Dictionary<string, JsonElement>(cfg, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement> CloneCfg(
        Dictionary<string, JsonElement> cfg,
        string resolution,
        double retries)
    {
        // We don't mutate JsonElements; rates use resolution + retries passed separately.
        _ = cfg;
        _ = resolution;
        _ = retries;
        return cfg;
    }

    private async Task<List<BlueprintSceneClips>> LoadBlueprintClipsAsync(
        string projectId,
        CancellationToken ct)
    {
        var list = new List<BlueprintSceneClips>();
        using var bp = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        if (bp is null ||
            !bp.RootElement.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return list;

        var defaultDur = 8.0;
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        defaultDur = GetDouble(cfg, "duration_seconds", 8);

        foreach (var s in scenes.EnumerateArray())
        {
            var sn = s.TryGetProperty("scene_number", out var sne) && sne.TryGetInt32(out var n) ? n : 0;
            var setting = s.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "";
            var clips = new List<BlueprintClip>();
            if (s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in vc.EnumerateArray())
                {
                    var cn = ClipKeying.ClipNumber(c);
                    if (cn <= 0) continue;
                    var dur = ClipDuration.Resolve(c, defaultDur);

                    clips.Add(new BlueprintClip
                    {
                        ClipNumber = cn,
                        DurationSec = dur,
                        Continuation = c.TryGetProperty("veo_continuation_source", out var cont)
                            ? cont.GetString() ?? "none"
                            : "none",
                        DialogueCharCount = CountClipDialogueChars(c),
                        Speaker = ReadClipSpeaker(c),
                    });
                }
            }

            var chars = ReadStringArray(s, "characters_on_screen");

            var locs = ReadStringArray(s, "location_ids");

            string? primaryLoc = null;
            if (s.TryGetProperty("primary_location_id", out var pl) &&
                pl.GetString() is { Length: > 0 } plId)
            {
                primaryLoc = plId;
                if (!locs.Contains(plId, StringComparer.OrdinalIgnoreCase))
                    locs.Insert(0, plId);
            }

            list.Add(new BlueprintSceneClips
            {
                SceneNumber = sn,
                Setting = setting,
                Clips = clips,
                CharactersOnScreen = chars,
                LocationIds = locs,
                PrimaryLocationId = primaryLoc,
            });
        }

        return list.OrderBy(x => x.SceneNumber).ToList();
    }

    private Dictionary<int, Dictionary<int, bool>> IndexOnDiskClips(string projectId)
    {
        var map = new Dictionary<int, Dictionary<int, bool>>();
        var videoDir = Path.Combine(_projects.GetProjectDir(projectId), "assets", "video");
        if (!Directory.Exists(videoDir))
            return map;
        try
        {
            // DirectoryInfo avoids a second FileInfo stat per path for Length.
            foreach (var fi in new DirectoryInfo(videoDir).EnumerateFiles("scene_*_clip_*.mp4"))
            {
                var name = fi.Name;
                // Exact scene_01_clip_02.mp4 only (not .native.mp4 sidecars)
                if (!ClipFileNaming.IsExactClipFileName(name)) continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                var parts = stem.Split('_');
                if (parts.Length >= 4 &&
                    int.TryParse(parts[1], out var sn) &&
                    int.TryParse(parts[3], out var cn))
                {
                    try
                    {
                        if (fi.Length < 1024) continue;
                    }
                    catch { continue; }

                    if (!map.TryGetValue(sn, out var inner))
                    {
                        inner = new Dictionary<int, bool>();
                        map[sn] = inner;
                    }
                    inner[cn] = true;
                }
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private async Task<Dictionary<int, string>> LoadHeroMapAsync(
        string projectId,
        CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return map;
        try
        {
            var doc = await _projects.ReadCache.GetOrLoadJsonDocumentAsync(path, ct).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("scene_hero", out var hero) ||
                hero.ValueKind != JsonValueKind.Object)
                return map;
            foreach (var p in hero.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var sn)) continue;
                if (p.Value.ValueKind == JsonValueKind.Object &&
                    p.Value.TryGetProperty("resolution", out var r) &&
                    r.GetString() is { Length: > 0 } res)
                    map[sn] = res;
                else if (p.Value.ValueKind is JsonValueKind.True)
                    map[sn] = "720p";
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private async Task<Dictionary<string, Dictionary<string, JsonElement>>> LoadClipJobsAsync(
        string projectId,
        CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return map;
        try
        {
            var doc = await _projects.ReadCache.GetOrLoadJsonDocumentAsync(path, ct).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("clip_jobs", out var jobs) ||
                jobs.ValueKind != JsonValueKind.Object)
                return map;
            foreach (var p in jobs.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var inner = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var q in p.Value.EnumerateObject())
                    inner[q.Name] = q.Value.Clone();
                map[p.Name] = inner;
            }
        }
        catch { /* ignore */ }
        return map;
    }

    /// <summary>
    /// Build planning/ledger rates from the project's selected models (vendor catalog),
    /// then apply non-rate planning assumptions from <c>cost_estimates</c> (retries, etc.).
    /// Manual $/sec overrides in config are ignored so estimates stay vendor-accurate.
    /// </summary>
    private static Dictionary<string, object?> RatesFromConfig(Dictionary<string, JsonElement> cfg)
    {
        var videoModelId = GetStr(cfg, "model_name", "");
        var imageModelId = GetStr(cfg, "image_model_name", "");
        return RatesFromModels(videoModelId, imageModelId, cfg);
    }

    /// <summary>
    /// Vendor list rates for a video + image model pair from <see cref="SupportedModelCatalog"/>.
    /// </summary>
    internal static Dictionary<string, object?> RatesFromModels(
        string? videoModelId,
        string? imageModelId,
        Dictionary<string, JsonElement>? cfgOverrides = null)
    {
        // Catalog only — never invent synthetic models for rates/provider identity.
        var video = SupportedModelCatalog.Find(videoModelId, ModelCapability.Video)
                    ?? SupportedModelCatalog.Find(videoModelId);
        var imagePrimary = SupportedModelCatalog.Find(imageModelId, ModelCapability.Image)
                           ?? SupportedModelCatalog.Find(imageModelId);

        if (video is null && imagePrimary is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["currency"] = "USD",
                ["source"] = "model_catalog",
                ["video_model"] = videoModelId ?? "",
                ["video_provider"] = "",
                ["image_model"] = imageModelId ?? "",
                ["image_provider"] = "",
                ["video_pricing_source"] = "missing_catalog_entry",
                ["image_pricing_source"] = "missing_catalog_entry",
            };
        }

        // If only one side is missing, keep going with empty pricing for that side (no invent of provider).
        video ??= PlaceholderEntry(videoModelId, ModelCapability.Video);
        imagePrimary ??= PlaceholderEntry(imageModelId, ModelCapability.Image);

        // Prefer a cheaper "standard" sibling in the same family when the project uses a quality image model.
        var imageStandard = SupportedModelCatalog.ForCapability(ModelCapability.Image)
            .Where(e => string.Equals(e.ProviderId, imagePrimary.ProviderId, StringComparison.OrdinalIgnoreCase)
                        && e.ImageCostPerImage is not null
                        && !string.Equals(e.Id, imagePrimary.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.ImageCostPerImage)
            .FirstOrDefault()
            ?? imagePrimary;

        var videoTable = BuildVideoRateTable(video);
        var videoBaseTable = BuildVideoBaseRateTable(video);
        // True whenever any part of the price had to fall back to a guess rather than this
        // model's actual catalog data — flows into the recorded cost event below so the ledger
        // itself shows which numbers are verified vendor pricing vs an unverified placeholder.
        // A model priced entirely via a flat base fee (no per-second rate at all, e.g. Hunyuan/Wan)
        // is NOT estimated as long as that base-fee data is real, so check both tables.
        var videoPricingIsEstimated =
            video.VideoCostPerSecondByResolution is not { Count: > 0 } &&
            video.VideoBaseCostByResolution is not { Count: > 0 };
        double qualityUnit;
        bool imagePricingIsEstimated;
        if (imagePrimary.ImageCostPerImage is { } imgCost)
        {
            qualityUnit = imgCost;
            imagePricingIsEstimated = false;
        }
        else if (imagePrimary.LabMode)
        {
            qualityUnit = 0;
            imagePricingIsEstimated = true; // lab: unknown, not invented vendor rate
        }
        else
            throw new InvalidOperationException(
                $"Image model '{imagePrimary.Id}' has no imageCostPerImage in models_catalog.json.");
        var standardUnit = imageStandard.ImageCostPerImage ?? qualityUnit;

        // Reference-image and extend-per-second add-ons: prefer a real per-model catalog value
        // (published by the vendor) and only fall back to the small Grok-era estimate when the
        // catalog has no verified number for this model. As of 2026-08 no enabled video provider
        // publishes either as a distinct line item (checked against docs.x.ai/developers/pricing for
        // xAI, the only model with SupportsVideoContinue=true), so these fields are null everywhere
        // today and the fallback constants apply uniformly — but the catalog is checked first so a
        // future vendor-verified number takes over automatically.
        var refImageCostReal = video.VideoReferenceImageCost;
        var extendCostReal = video.VideoExtendCostPerSecond;
        var refImageSource = refImageCostReal is not null ? "model_catalog" : "missing_catalog";
        var extendSource = extendCostReal is not null
            ? "model_catalog"
            : (video.SupportsVideoContinue ? "missing_catalog" : "not_applicable");

        // Overall video pricing is only "fully real" when the output pricing (per-second table OR
        // a flat base fee — a model priced entirely via base fee with no per-second rate, e.g.
        // Hunyuan/Wan, is real as long as that base-fee data is real, so this isn't per-second-only),
        // the reference-image add-on, and (only if this model can extend) the extend add-on are all
        // sourced from the catalog rather than a fallback estimate.
        var videoOutputIsCatalog = !videoPricingIsEstimated;
        var videoPricingFullyReal = videoOutputIsCatalog
            && refImageCostReal is not null
            && (!video.SupportsVideoContinue || extendCostReal is not null);

        var rates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["currency"] = "USD",
            ["source"] = "model_catalog",
            ["video_model"] = video.Id,
            ["video_provider"] = video.ProviderId,
            ["image_model"] = imagePrimary.Id,
            ["image_provider"] = imagePrimary.ProviderId,
            ["video_output_per_sec"] = videoTable,
            ["video_base_per_video"] = videoBaseTable,
            ["video_input_image"] = refImageCostReal
                ?? (video.LabMode
                    ? 0.0
                    : throw new InvalidOperationException(
                        $"Video model '{video.Id}' has no videoReferenceImageCost in models_catalog.json "
                        + "(use 0 if no separate ref fee; cite pricingNotes).")),
            ["video_input_image_source"] = refImageSource,
            ["video_input_per_sec"] = extendCostReal
                ?? (video.SupportsVideoContinue
                    ? (video.LabMode
                        ? 0.0
                        : throw new InvalidOperationException(
                            $"Video model '{video.Id}' supports continue but has no videoExtendCostPerSecond "
                            + "in models_catalog.json."))
                    : 0.0),
            ["video_input_per_sec_source"] = extendSource,
            ["image_output_quality"] = qualityUnit,
            ["image_output_standard"] = standardUnit,
            ["assume_ref_image_per_clip"] = true,
            ["assume_extend_fraction"] = 0.0,
            ["assume_avg_retries"] = 0.0,
            ["video_pricing_source"] = video.LabMode
                ? "lab_mode"
                : (videoPricingFullyReal ? "model_catalog" : "missing_catalog"),
            ["image_pricing_source"] = imagePrimary.LabMode
                ? "lab_mode"
                : (imagePricingIsEstimated ? "missing_catalog" : "model_catalog"),
            ["video_lab_mode"] = video.LabMode,
            ["image_lab_mode"] = imagePrimary.LabMode,
        };

        // Planning knobs only — do not let old manual $/sec tables override vendor rates.
        if (cfgOverrides is not null &&
            cfgOverrides.TryGetValue("cost_estimates", out var ce) &&
            ce.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ce.EnumerateObject())
            {
                if (p.NameEquals("video_output_per_sec") ||
                    p.NameEquals("image_output_quality") ||
                    p.NameEquals("image_output_standard") ||
                    p.NameEquals("source") ||
                    p.NameEquals("video_model") ||
                    p.NameEquals("video_provider") ||
                    p.NameEquals("image_model") ||
                    p.NameEquals("image_provider") ||
                    p.NameEquals("currency") ||
                    p.NameEquals("notes"))
                    continue;

                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                    rates[p.Name] = d;
                else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    rates[p.Name] = p.Value.GetBoolean();
                else if (p.Value.ValueKind == JsonValueKind.String)
                    rates[p.Name] = p.Value.GetString();
            }
        }

        return rates;
    }

    /// <summary>
    /// Per-resolution $/sec from the video catalog entry; fill missing 480p/720p/1080p from nearest vendor tier.
    /// </summary>
    internal static Dictionary<string, double> BuildVideoRateTable(SupportedModelEntry video)
    {
        var table = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (video.VideoCostPerSecondByResolution is { Count: > 0 } src)
            CopyPositiveRates(table, src);

        FillMissingResolutions(table);

        if (table.Count == 0 && video.VideoBaseCostByResolution is not { Count: > 0 })
        {
            if (video.LabMode)
                return table; // empty → $0/sec for lab estimates; source=lab_mode
            throw new InvalidOperationException(
                $"Video model '{video.Id}' has no videoCostPerSecondByResolution or "
                + "videoBaseCostByResolution in models_catalog.json.");
        }

        return table;
    }

    /// <summary>
    /// Per-resolution flat $/video from the catalog entry — the counterpart to
    /// <see cref="BuildVideoRateTable"/> for providers that bill a fixed fee per generation
    /// regardless of length (Fal's Hunyuan/Wan, which are frame-count-based, not duration-based).
    /// Unlike the per-second table, a model with no base-cost data gets an EMPTY table (base = 0
    /// for every resolution) rather than falling back to some other provider's flat fee — there is
    /// no sensible "generic" flat-fee guess the way there's a generic per-second one.
    /// </summary>
    internal static Dictionary<string, double> BuildVideoBaseRateTable(SupportedModelEntry video)
    {
        var table = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (video.VideoBaseCostByResolution is { Count: > 0 } src)
        {
            CopyPositiveRates(table, src);
            FillMissingResolutions(table);
        }
        return table;
    }

    /// <summary>Read a scene's string array property, dropping null/blank entries.</summary>
    private static List<string> ReadStringArray(JsonElement scene, string propertyName)
    {
        var list = new List<string>();
        if (scene.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in arr.EnumerateArray())
            {
                var name = x.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(name!);
            }
        }
        return list;
    }

    /// <summary>Copy non-blank, non-negative per-resolution rates into <paramref name="table"/> (keys trimmed).</summary>
    private static void CopyPositiveRates(
        Dictionary<string, double> table,
        IEnumerable<KeyValuePair<string, double>> src)
    {
        foreach (var kv in src)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value >= 0)
                table[kv.Key.Trim()] = kv.Value;
        }
    }

    /// <summary>Fill any missing 480p/720p/1080p rate from the nearest present tier.</summary>
    private static void FillMissingResolutions(Dictionary<string, double> table)
    {
        FillMissingRes(table, "720p", "1080p", "480p");
        FillMissingRes(table, "480p", "720p", "1080p");
        FillMissingRes(table, "1080p", "720p", "480p");
    }

    private static void FillMissingRes(
        Dictionary<string, double> table,
        string res,
        params string[] prefer)
    {
        if (table.ContainsKey(res)) return;
        foreach (var p in prefer)
        {
            if (table.TryGetValue(p, out var v))
            {
                table[res] = v;
                return;
            }
        }
        if (table.Count > 0)
            table[res] = table.Values.Min();
    }

    private static double OutputRate(string resolution, Dictionary<string, object?> rates)
    {
        var res = (resolution ?? "720p").ToLowerInvariant().Trim();
        if (rates.TryGetValue("video_output_per_sec", out var t) &&
            t is Dictionary<string, double> table)
        {
            if (table.TryGetValue(res, out var r)) return r;
            if (table.TryGetValue("720p", out var d)) return d;
            if (table.Count > 0) return table.Values.First();
        }
        return 0; // base-fee-only models: no per-second component
    }

    /// <summary>Flat $/video for this resolution — 0 when the model has no base-cost data (a
    /// genuinely per-second-only provider like Grok/Veo), never a nonzero guess.</summary>
    private static double BaseRate(string resolution, Dictionary<string, object?> rates)
    {
        var res = (resolution ?? "720p").ToLowerInvariant().Trim();
        if (rates.TryGetValue("video_base_per_video", out var t) &&
            t is Dictionary<string, double> table)
        {
            if (table.TryGetValue(res, out var r)) return r;
            if (table.TryGetValue("720p", out var d)) return d;
            if (table.Count > 0) return table.Values.First();
        }
        return 0;
    }

    /// <summary>
    /// Empty placeholder when a project references a model id not in the catalog.
    /// ProviderId stays blank — never invents a provider (e.g. "grok").
    /// </summary>
    private static SupportedModelEntry PlaceholderEntry(string? modelId, ModelCapability capability) => new()
    {
        Id = string.IsNullOrWhiteSpace(modelId) ? "" : modelId.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(modelId) ? "" : modelId.Trim(),
        Capability = capability,
        Provider = ModelProviderFamily.Xai,
        ProviderId = "",
        ProviderLabel = "",
        ApiBase = "",
        EndpointPath = "",
        RequiredEnvKeys = Array.Empty<string>(),
        Enabled = false,
        Notes = "Not in models_catalog.json",
    };

    private static string GetStr(Dictionary<string, JsonElement> cfg, string key, string fallback) =>
        cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    /// <summary>
    /// Video provider id for cost reports — catalog only (project cfg may store a stale value).
    /// Never invents "grok" when the model is missing or not in the catalog.
    /// </summary>
    private static string ResolveVideoProvider(Dictionary<string, JsonElement> cfg, string? videoModel)
    {
        var fromModel = SupportedModelCatalog.CatalogProviderId(videoModel, "video");
        if (!string.IsNullOrWhiteSpace(fromModel))
            return fromModel;
        var fromCfg = GetStr(cfg, "video_provider", "");
        if (!string.IsNullOrWhiteSpace(fromCfg) && SupportedModelCatalog.IsKnownProviderId(fromCfg))
            return SupportedModelCatalog.NormalizeProviderId(fromCfg);
        return "";
    }

    private static double GetDouble(Dictionary<string, JsonElement> cfg, string key, double fallback) =>
        cfg.TryGetValue(key, out var el) && el.TryGetDouble(out var v) ? v : fallback;

    private static double RequireRate(Dictionary<string, object?> rates, string key)
    {
        if (rates.TryGetValue(key, out var v) && v is double d)
            return d;
        if (rates.TryGetValue(key, out var v2) && v2 is int i)
            return i;
        throw new InvalidOperationException(
            $"Cost rate '{key}' missing from catalog-derived rate table. "
            + "Add the field to models_catalog.json — Engine does not invent USD.");
    }

    private static double GetDouble(Dictionary<string, object?> rates, string key, double fallback)
    {
        if (!rates.TryGetValue(key, out var v) || v is null) return fallback;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static bool GetBool(Dictionary<string, object?> rates, string key, bool fallback)
    {
        if (!rates.TryGetValue(key, out var v) || v is null) return fallback;
        return v is bool b ? b : Convert.ToBoolean(v, CultureInfo.InvariantCulture);
    }

    private static string GetRawKind(JsonElement e) =>
        e.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";

    private static bool TryGetInt(JsonElement e, string name, out int v)
    {
        v = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        // JsonElement.TryGetInt32 throws (not just returns false) when the token is present but not
        // a Number — e.g. an explicit JSON null (a scene/clip on a book-level, not per-scene, event) —
        // so gate on ValueKind first rather than treating "property exists" as "property is numeric".
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out v)) return true;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out v)) return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement e, string name, out double v)
    {
        v = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out v)) return true;
        if (p.ValueKind == JsonValueKind.String &&
            double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return true;
        return false;
    }


    /// <summary>
    /// When no shot plan yet: invent clip slots from Fountain scene duration targets so
    /// post-import estimates work (model rates × planned seconds).
    /// </summary>
    private async Task<List<BlueprintSceneClips>> LoadScreenplayDerivedClipsAsync(
        string projectId,
        Dictionary<string, JsonElement> cfg,
        CancellationToken ct)
    {
        await Task.Yield();
        var list = new List<BlueprintSceneClips>();
        var model = ScreenplayService.TryBuildModelFromProject(_projects, projectId);
        if (model is null) return list;

        if (!model.TryGetValue("scenes", out var scenesObj) || scenesObj is not List<object?> scenes)
            return list;

        var defaultDur = GetDouble(cfg, "duration_seconds", 8);
        if (defaultDur < 2) defaultDur = 8;

        var sn = 0;
        foreach (var raw in scenes)
        {
            if (raw is not Dictionary<string, object?> s) continue;
            sn++;
            string setting = "";
            if (s.TryGetValue("setting", out var setObj) && setObj is not null)
                setting = setObj.ToString() ?? "";
            else if (s.TryGetValue("heading", out var hObj) && hObj is not null)
                setting = hObj.ToString() ?? "";
            var target = ToPositiveDouble(
                s.TryGetValue("duration_target_seconds", out var d1) ? d1 : null,
                ToPositiveDouble(
                    s.TryGetValue("estimated_duration_seconds", out var d2) ? d2 : null,
                    24));
            target = Math.Clamp(target, defaultDur, 600);

            var nClips = Math.Max(1, (int)Math.Ceiling(target / defaultDur));
            var clips = new List<BlueprintClip>();
            var remaining = target;
            for (var i = 1; i <= nClips; i++)
            {
                var dur = i == nClips ? Math.Max(1, remaining) : Math.Min(defaultDur, remaining);
                clips.Add(new BlueprintClip
                {
                    ClipNumber = i,
                    DurationSec = dur,
                    Continuation = i > 1 ? "prev" : "none",
                });
                remaining -= dur;
            }

            var chars = new List<string>();
            if (s.TryGetValue("characters_on_screen", out var cos) && cos is List<object?> cosList)
            {
                foreach (var x in cosList)
                {
                    var name = x?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        chars.Add(name!);
                }
            }

            list.Add(new BlueprintSceneClips
            {
                SceneNumber = s.TryGetValue("scene_number", out var snObj) && snObj is not null
                    && int.TryParse(snObj.ToString(), out var snParsed) ? snParsed : sn,
                Setting = setting ?? "",
                Clips = clips,
                CharactersOnScreen = chars,
            });
        }

        return list.OrderBy(x => x.SceneNumber).ToList();
    }

    private readonly record struct ScopeEstimate(double Usd, double RemainingUsd, bool Included);

    /// <summary>Character portraits: variants × image-model unit cost (catalog).</summary>
    private ScopeEstimate EstimateCharacterGeneration(
        string projectId,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg)
    {
        var unit = GetDouble(rates, "image_output_quality", 0.05);
        var variants = 3;
        if (cfg.TryGetValue("cost_estimates", out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            if (ce.TryGetProperty("character_variants", out var cv) && cv.TryGetInt32(out var n) && n > 0)
                variants = Math.Clamp(n, 1, 6);
        }

        var chars = _projects.ListCharacters(projectId);
        var onScreen = chars.Where(c => !c.VoiceOnly).ToList();
        if (onScreen.Count == 0)
            return new ScopeEstimate(0, 0, Included: false);

        double total = 0, remaining = 0;
        foreach (var c in onScreen)
        {
            // Plan for a full generate cycle per character; locked looks are already paid for.
            var planned = variants * unit;
            total += planned;
            if (!c.Locked)
                remaining += planned;
        }

        return new ScopeEstimate(Math.Round(total, 4), Math.Round(remaining, 4), Included: true);
    }

    /// <summary>
    /// Voice / re-voice estimate: clone fees + TTS from dialogue volume × catalog
    /// <c>CostPerThousandCharsUsd</c> (aligned with speak-batch actuals).
    /// </summary>
    private ScopeEstimate EstimateVoiceGeneration(
        string projectId,
        List<BlueprintSceneClips> scenes,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg)
    {
        var include = false;
        double cloneUsdOverride = -1;
        double ttsPerCharOverride = -1; // flat $ per speaking character (legacy knob)
        double ttsPerThousandOverride = -1;

        if (cfg.TryGetValue("cost_estimates", out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            if (ce.TryGetProperty("include_voice", out var iv) &&
                iv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                include = iv.GetBoolean();
            if (ce.TryGetProperty("voice_clone_usd", out var vc) && vc.TryGetDouble(out var v1))
                cloneUsdOverride = v1;
            if (ce.TryGetProperty("voice_tts_per_character_usd", out var vt) && vt.TryGetDouble(out var v2))
                ttsPerCharOverride = v2;
            if (ce.TryGetProperty("voice_tts_per_thousand_chars_usd", out var vt2) && vt2.TryGetDouble(out var v3))
                ttsPerThousandOverride = v3;
        }

        var chars = _projects.ListCharacters(projectId);
        var withVoice = chars.Where(c =>
            c.HasVoiceCloneSample ||
            !string.IsNullOrWhiteSpace(c.VoiceProfile) ||
            !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId)).ToList();
        if (withVoice.Count > 0)
            include = true;

        // Shot plan with dialogue ⇒ speak-batch / re-voice is in scope even without a sample yet.
        var dialogueChars = 0;
        var dialogueClips = 0;
        foreach (var s in scenes)
        {
            foreach (var c in s.Clips)
            {
                if (c.DialogueCharCount <= 0) continue;
                dialogueChars += c.DialogueCharCount;
                dialogueClips++;
            }
        }
        if (dialogueChars > 0)
            include = true;

        if (!include || (chars.Count == 0 && dialogueChars == 0))
            return new ScopeEstimate(0, 0, Included: false);

        var voiceId = GetStr(cfg, "voice_model_name", "");
        var voiceEntry = SupportedModelCatalog.Find(voiceId, ModelCapability.Voice)
                         ?? SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                             .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled);

        // Prefer speak model (not clone step) for TTS $/1k chars.
        if (voiceEntry is { IsVoiceCloneStep: true })
        {
            voiceEntry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                    string.Equals(m.ProviderId, voiceEntry.ProviderId, StringComparison.OrdinalIgnoreCase))
                ?? voiceEntry;
        }

        var cloneEntry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => m.IsVoiceCloneStep && m.Enabled &&
                (voiceEntry is null ||
                 string.Equals(m.ProviderId, voiceEntry.ProviderId, StringComparison.OrdinalIgnoreCase)));

        var perThousand = ttsPerThousandOverride >= 0
            ? ttsPerThousandOverride
            : voiceEntry?.CostPerThousandCharsUsd ?? 0.10;
        var cloneUsd = cloneUsdOverride >= 0
            ? cloneUsdOverride
            : cloneEntry?.CostPerCloneUsd ?? 0.0;

        // Clone: once per character that still needs a sample (or one narrator slot if none listed).
        double total = 0, remaining = 0;
        var cloneTargets = withVoice.Count > 0
            ? withVoice
            : chars.Where(c =>
                string.Equals(c.Key, "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
                (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (cloneTargets.Count == 0 && dialogueChars > 0)
        {
            // Narrator re-voice path without seeds loaded: one clone slot.
            total += cloneUsd;
            remaining += cloneUsd;
        }
        else
        {
            foreach (var c in cloneTargets)
            {
                if (c.HasVoiceCloneSample || !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId))
                    continue;
                total += cloneUsd;
                remaining += cloneUsd;
            }
        }

        // TTS: dialogue characters × catalog rate (speak-batch style).
        if (dialogueChars > 0)
        {
            var tts = perThousand * dialogueChars / 1000.0;
            total += tts;
            remaining += tts;
        }
        else if (ttsPerCharOverride >= 0)
        {
            // Legacy flat per-character when no blueprint dialogue yet.
            var targets = withVoice.Count > 0 ? withVoice : chars.ToList();
            foreach (var _ in targets)
            {
                total += ttsPerCharOverride;
                remaining += ttsPerCharOverride;
            }
        }
        else if (scenes.Count > 0)
        {
            // Rough: ~12 chars/sec of clip duration for clips without dialogue text.
            var sec = scenes.Sum(s => s.Clips.Sum(c => c.DurationSec));
            var approxChars = (int)Math.Round(sec * 12);
            if (approxChars > 0)
            {
                var tts = perThousand * approxChars / 1000.0;
                total += tts;
                remaining += tts;
            }
        }

        _ = rates;
        _ = dialogueClips;
        return new ScopeEstimate(Math.Round(total, 4), Math.Round(remaining, 4), Included: total > 0 || include);
    }

    /// <summary>Background music: one track per scene with media plan, using audio model if priced.</summary>
    private static ScopeEstimate EstimateMusicGeneration(
        List<BlueprintSceneClips> scenes,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg)
    {
        if (scenes.Count == 0)
            return new ScopeEstimate(0, 0, Included: false);

        var perScene = 0.08;
        if (cfg.TryGetValue("cost_estimates", out var ce) && ce.ValueKind == JsonValueKind.Object &&
            ce.TryGetProperty("music_per_scene_usd", out var m) && m.TryGetDouble(out var mv) && mv >= 0)
            perScene = mv;

        // Disable when audio model is "none"
        var audioModel = GetStr(cfg, "audio_model_name", "");
        if (string.Equals(audioModel, "none", StringComparison.OrdinalIgnoreCase))
            return new ScopeEstimate(0, 0, Included: false);

        var n = scenes.Count(s => s.Clips.Count > 0);
        if (n == 0) n = scenes.Count;
        var total = n * perScene;
        _ = rates;
        return new ScopeEstimate(Math.Round(total, 4), Math.Round(total, 4), Included: total > 0);
    }

    /// <summary>
    /// Screenplay / shot-plan LLM work. Uses chat model token rates when available;
    /// after import the screenplay pass is treated as done (remaining ≈ shot plan only).
    /// </summary>

    /// <summary>
    /// Post-clip automated review (Gemini dialogue check / optional clip auto-review).
    /// When quality-gate retry is on, count re-review after expected failed regens.
    /// Extra <em>video</em> cost for regens is folded into clip attempts via retries.
    /// </summary>

    private ApiCostHistoryStats? _historyApiStats;

    private async Task<CostEstimateRefinement> BuildHistoryRefinementAsync(
        string projectId,
        double priorVideoMultiplier,
        bool qaRetryOnFail,
        int qaMaxRetries,
        CostLedgerSummary projectActual,
        CancellationToken ct)
    {
        var refn = new CostEstimateRefinement
        {
            PriorVideoMultiplier = priorVideoMultiplier,
            AppliedVideoMultiplier = qaRetryOnFail ? priorVideoMultiplier : 1.0,
            ProjectLedgerEvents = projectActual.EventCount,
        };

        double? failRate = null;
        var timingSamples = 0;
        if (_timingDb is not null)
        {
            var global = await _timingDb.GetDialogueFailRateAsync(MinTimingSamples, projectId: null, ct)
                .ConfigureAwait(false);
            var project = await _timingDb.GetDialogueFailRateAsync(MinTimingSamples, projectId, ct)
                .ConfigureAwait(false);
            if (project is { } p)
            {
                failRate = p.FailRate;
                timingSamples = p.Samples;
            }
            else if (global is { } g)
            {
                failRate = g.FailRate;
                timingSamples = g.Samples;
            }
        }
        refn.TimingSamples = timingSamples;
        refn.LearnedFailRate = failRate;

        ApiCostHistoryStats? apiStats = null;
        if (_userDb is not null)
        {
            var projectStats = await _userDb.GetApiCostHistoryStatsAsync(userId: null, projectId, ct)
                .ConfigureAwait(false);
            var globalStats = await _userDb.GetApiCostHistoryStatsAsync(userId: null, projectId: null, ct)
                .ConfigureAwait(false);
            apiStats = projectStats.TotalCalls >= MinApiSamples ? projectStats : globalStats;
            if (apiStats.ByCategory.TryGetValue(CostCategories.Video, out var v))
                refn.VideoApiSamples = v.Count;
            if (apiStats.ByCategory.TryGetValue(CostCategories.Review, out var r))
                refn.ReviewApiSamples = r.Count;
        }

        var learnedMult = priorVideoMultiplier;
        if (qaRetryOnFail && failRate is double fr)
        {
            learnedMult = 1.0 + Math.Clamp(fr, 0, 0.9) * Math.Max(1, qaMaxRetries);
            learnedMult = Math.Clamp(learnedMult, 1.0, 2.5);
        }

        var w = timingSamples <= 0 ? 0.0 : Math.Min(1.0, timingSamples / 30.0);
        refn.HistoryWeight = w;
        if (qaRetryOnFail)
            refn.AppliedVideoMultiplier = Math.Round(priorVideoMultiplier * (1 - w) + learnedMult * w, 3);

        refn.UsedHistory = timingSamples >= MinTimingSamples || refn.VideoApiSamples >= MinApiSamples
            || projectActual.EventCount >= MinApiSamples;

        var bits = new List<string>();
        if (timingSamples >= MinTimingSamples && failRate is double fr2)
            bits.Add($"timing QA fail ~{fr2:P0} (n={timingSamples})");
        if (refn.VideoApiSamples > 0)
            bits.Add($"{refn.VideoApiSamples} video API samples");
        if (refn.ReviewApiSamples > 0)
            bits.Add($"{refn.ReviewApiSamples} review API samples");
        if (projectActual.EventCount > 0)
            bits.Add($"{projectActual.EventCount} project ledger events");

        // H4/H5 — blend learned takes-per-clip (p50) into expected video multiplier.
        refn.ExpectedTakes = Math.Max(1.0, refn.AppliedVideoMultiplier);
        try
        {
            if (_userDb is not null)
            {
                var globalTakes = await _userDb.GetTakesTelemetryStatsAsync(projectId: null, ct)
                    .ConfigureAwait(false);
                var projectTakes = await _userDb.GetTakesTelemetryStatsAsync(projectId, ct)
                    .ConfigureAwait(false);
                // Prefer project when it has enough samples; else global contribute=1 pool.
                var takesSrc = projectTakes.SufficientForBlend ? projectTakes
                    : globalTakes.SufficientForBlend ? globalTakes
                    : null;
                if (takesSrc is not null && takesSrc.P50TakesPerClip >= 1.0)
                {
                    refn.TakesClipSamples = takesSrc.ClipSampleCount;
                    refn.LearnedTakesP50 = takesSrc.P50TakesPerClip;
                    var tw = Math.Min(1.0, takesSrc.ClipSampleCount / 30.0);
                    var priorTakes = Math.Max(1.0, refn.AppliedVideoMultiplier);
                    var learned = Math.Clamp(takesSrc.P50TakesPerClip, 1.0, 4.0);
                    refn.ExpectedTakes = Math.Round(priorTakes * (1 - tw) + learned * tw, 3);
                    refn.HistoryWeight = Math.Max(refn.HistoryWeight, tw);
                    refn.UsedHistory = true;
                    // Keep AppliedVideoMultiplier in sync so video estimates scale with expected takes.
                    if (qaRetryOnFail || takesSrc.SufficientForBlend)
                        refn.AppliedVideoMultiplier = Math.Max(refn.AppliedVideoMultiplier, refn.ExpectedTakes);
                    bits.Add(
                        $"takes p50={takesSrc.P50TakesPerClip:0.##} (n={takesSrc.ClipSampleCount}, {takesSrc.Scope}) " +
                        $"→ expected {refn.ExpectedTakes:0.##}");
                }
            }
        }
        catch
        {
            // H9 fail-open — keep prior expected takes
        }

        if (bits.Count == 0)
            bits.Add("no history yet — using catalog priors");
        else if (w > 0 && qaRetryOnFail)
            bits.Add($"video mult {priorVideoMultiplier:0.##}→{refn.AppliedVideoMultiplier:0.##} (weight {w:0.00})");
        refn.Notes = "History: " + string.Join("; ", bits) + ".";

        _historyApiStats = apiStats;
        return refn;
    }

    private void ApplyHistoryUnitCosts(
        Dictionary<string, double> estimateByCategory,
        CostEstimateRefinement refinement,
        int clipsTotal)
    {
        var stats = _historyApiStats;
        if (stats is null || stats.TotalCalls < MinApiSamples)
            return;

        void Blend(string cat, double plannedUnits, double catalogTotal)
        {
            if (!stats.ByCategory.TryGetValue(cat, out var row) || row.Count < MinApiSamples)
                return;
            if (plannedUnits <= 0 || catalogTotal <= 0)
                return;
            var empiric = row.AvgUsd * plannedUnits;
            var w = Math.Min(1.0, row.Count / 40.0);
            var blended = catalogTotal * (1 - w) + empiric * w;
            blended = Math.Clamp(blended, catalogTotal * 0.4, catalogTotal * 2.5);
            estimateByCategory[cat] = Math.Round(blended, 2);
            refinement.UsedHistory = true;
            refinement.HistoryWeight = Math.Max(refinement.HistoryWeight, w);
        }

        Blend(CostCategories.Video, Math.Max(1, clipsTotal), estimateByCategory.GetValueOrDefault(CostCategories.Video));
        Blend(CostCategories.Review, Math.Max(1, clipsTotal), estimateByCategory.GetValueOrDefault(CostCategories.Review));
        Blend(CostCategories.Characters, 1, estimateByCategory.GetValueOrDefault(CostCategories.Characters));
        Blend(CostCategories.Screenplay, 1, estimateByCategory.GetValueOrDefault(CostCategories.Screenplay));
        Blend(CostCategories.Voice, 1, estimateByCategory.GetValueOrDefault(CostCategories.Voice));
        Blend(CostCategories.Music, Math.Max(1, clipsTotal > 0 ? clipsTotal / 4.0 : 1),
            estimateByCategory.GetValueOrDefault(CostCategories.Music));
    }

    private static ScopeEstimate EstimateAutomatedReview(
        int clipsTotal,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg,
        bool qaRetryOnFail,
        double qaVideoMultiplier)
    {
        if (clipsTotal <= 0)
            return new ScopeEstimate(0, 0, Included: false);

        // Prefer quality/vision model for Gemini-style native video dialogue QA.
        var reviewModelId = GetStr(cfg, "quality_model_name",
            GetStr(cfg, "vision_model_name",
                GetStr(cfg, "planning_model_name", "")));
        var entry = SupportedModelCatalog.Find(reviewModelId, ModelCapability.Vision)
                    ?? SupportedModelCatalog.Find(reviewModelId, ModelCapability.Chat)
                    ?? SupportedModelCatalog.ResolveOrDefault(reviewModelId, ModelCapability.Chat);

        if (entry.InputCostPerMillionTokens is not { } inPerM || entry.OutputCostPerMillionTokens is not { } outPerM)
            throw new InvalidOperationException(
                $"Chat/Vision model '{entry.Id}' missing token costs in models_catalog.json.");
        var inRate = inPerM / 1_000_000.0;
        var outRate = outPerM / 1_000_000.0;

        // One dialogue/speaker verification pass per clip (video+audio multimodal prior).
        // Token priors are stand-ins until we average from telemetry.
        var inTok = 28_000.0;
        var outTok = 1_200.0;
        // Review passes track video multiplier: ~1.3× checks when QA retry is on (re-check after regen).
        var reviewPasses = qaRetryOnFail ? Math.Max(1.0, qaVideoMultiplier) : 1.0;
        if (cfg.TryGetValue("cost_estimates", out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            if (ce.TryGetProperty("review_input_tokens_per_clip", out var it) && it.TryGetDouble(out var itv) && itv > 0)
                inTok = itv;
            if (ce.TryGetProperty("review_output_tokens_per_clip", out var ot) && ot.TryGetDouble(out var otv) && otv > 0)
                outTok = otv;
            if (ce.TryGetProperty("review_usd_per_clip", out var fixedUsd) && fixedUsd.TryGetDouble(out var fu) && fu > 0)
            {
                var totalFixed = clipsTotal * fu * reviewPasses;
                return new ScopeEstimate(Math.Round(totalFixed, 4), Math.Round(totalFixed, 4), Included: true);
            }
        }

        var perClip = inTok * inRate + outTok * outRate;
        var total = clipsTotal * perClip * reviewPasses;
        _ = rates;
        return new ScopeEstimate(Math.Round(total, 4), Math.Round(total, 4), Included: true);
    }

    private static bool GetCfgBool(Dictionary<string, JsonElement> cfg, string key, bool defaultValue)
    {
        if (!cfg.TryGetValue(key, out var el)) return defaultValue;
        if (el.ValueKind is JsonValueKind.True) return true;
        if (el.ValueKind is JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
        return defaultValue;
    }

    private static int GetCfgInt(Dictionary<string, JsonElement> cfg, string key, int defaultValue)
    {
        if (!cfg.TryGetValue(key, out var el)) return defaultValue;
        if (el.TryGetInt32(out var i)) return i;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var j)) return j;
        if (el.TryGetDouble(out var d)) return (int)Math.Round(d);
        return defaultValue;
    }

    private static ScopeEstimate EstimatePlanningWork(
        List<BlueprintSceneClips> scenes,
        string estimateBasis,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg)
    {
        if (scenes.Count == 0 && estimateBasis == "none")
            return new ScopeEstimate(0, 0, Included: false);

        var planningId = GetStr(cfg, "planning_model_name",
            GetStr(cfg, "chat_model_name", ""));
        var entry = SupportedModelCatalog.Find(planningId, ModelCapability.Chat)
                    ?? SupportedModelCatalog.ResolveOrDefault(planningId, ModelCapability.Chat);

        // Rough token budgets: import screenplay ~80k in / 20k out; shot plan ~40k / 25k.
        if (entry.InputCostPerMillionTokens is not { } inPerM || entry.OutputCostPerMillionTokens is not { } outPerM)
            throw new InvalidOperationException(
                $"Chat/Vision model '{entry.Id}' missing token costs in models_catalog.json.");
        var inRate = inPerM / 1_000_000.0;
        var outRate = outPerM / 1_000_000.0;

        var sceneN = Math.Max(1, scenes.Count);
        var importUsd = (80_000 * inRate) + (20_000 * outRate);
        // Scale mild with scene count
        importUsd *= Math.Clamp(sceneN / 12.0, 0.6, 2.5);
        var shotPlanUsd = (40_000 * inRate) + (25_000 * outRate);
        shotPlanUsd *= Math.Clamp(sceneN / 12.0, 0.6, 2.5);

        double total, remaining;
        if (estimateBasis == "shot_plan")
        {
            // Both passes done for planning purposes
            total = importUsd + shotPlanUsd;
            remaining = 0;
        }
        else if (estimateBasis == "screenplay")
        {
            total = importUsd + shotPlanUsd;
            remaining = shotPlanUsd; // shot plan still ahead
        }
        else
        {
            total = importUsd + shotPlanUsd;
            remaining = total;
        }

        _ = rates;
        return new ScopeEstimate(Math.Round(total, 4), Math.Round(remaining, 4), Included: true);
    }

    private static double ToPositiveDouble(object? v, double fallback)
    {
        if (v is null) return fallback;
        try
        {
            var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return d > 0 ? d : fallback;
        }
        catch { return fallback; }
    }

    private sealed class BlueprintSceneClips
    {
        public int SceneNumber { get; set; }
        public string Setting { get; set; } = "";
        public List<BlueprintClip> Clips { get; set; } = new();
        public List<string> CharactersOnScreen { get; set; } = new();
        public List<string> LocationIds { get; set; } = new();
        public string? PrimaryLocationId { get; set; }
    }

    private sealed class BlueprintClip
    {
        public int ClipNumber { get; set; }
        public double DurationSec { get; set; }
        public string Continuation { get; set; } = "none";
        public int DialogueCharCount { get; set; }
        public string? Speaker { get; set; }
    }

    private static int CountClipDialogueChars(JsonElement c)
    {
        var dialogue = "";
        if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("dialogue", out var d))
            dialogue = d.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty("dialogue", out var rootD))
            dialogue = rootD.GetString() ?? "";
        dialogue = (dialogue ?? "").Trim();
        return dialogue.Length;
    }

    private static string? ReadClipSpeaker(JsonElement c)
    {
        if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("speaker", out var sp))
        {
            var s = sp.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (c.TryGetProperty("speaker", out var rootSp))
            return rootSp.GetString();
        return null;
    }
}
