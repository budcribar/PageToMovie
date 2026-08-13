using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Billing;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

using PageToMovie.Core.Utils;
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

    private static class Keys
    {
        public const string ModelName = "model_name";
        public const string Resolution = "resolution";
        public const string CostEstimates = "cost_estimates";
        public const string ShotPlan = "shot_plan";
        public const string Screenplay = "screenplay";
        public const string Remaining = "remaining";
        public const string ImageModelName = "image_model_name";
        public const string Video = "video";
        public const string Scene = "scene";
        public const string DurationSec = "duration_sec";
        public const string Model = "model";
        public const string Category = "category";
        public const string VideoProvider = "video_provider";
        public const string Provider = "provider";
        public const string VideoPricingSource = "video_pricing_source";
        public const string RequestId = "request_id";
        public const string Source = "source";
        public const string ListUsd = "list_usd";
        public const string Currency = "currency";
        public const string UserId = "user_id";
        public const string CostLedger = "cost_ledger";
        public const string ModelCatalog = "model_catalog";
        public const string Res1080p = "1080p";
        public const string MissingCatalog = "missing_catalog";
    }

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
        if (string.IsNullOrWhiteSpace(GetStr(cfg, Keys.ModelName, "")))
            throw new InvalidOperationException(
                "No models are set for this project yet. Choose a video and image model on the "
                + "Configuration page to see a cost estimate.");
        var rates = RatesFromConfig(cfg);
        var draftRes = draftResolution
            ?? GetStr(cfg, Keys.Resolution, "480p");
        var heroRes = heroResolution ?? "720p";
        var retries = assumeAvgRetries
            ?? GetDouble(rates, "assume_avg_retries", 0);

        // Quality Gate Retry (config) + history-refined video multiplier.
        var qaRetryOnFail = GetCfgBool(cfg, "qa_retry_on_fail", defaultValue: true);
        var qaMaxRetries = GetCfgInt(cfg, "qa_max_retries", defaultValue: 1);
        qaMaxRetries = Math.Clamp(qaMaxRetries, 0, 5);
        var priorVideoMultiplier = ResolvePriorVideoMultiplier(cfg, qaMaxRetries);

        var ledger = await GetCostLedgerAsync(projectId, ct).ConfigureAwait(false);
        var multEarly = GetChargeMultiplier();
        var actual = SummarizeLedger(ledger, multEarly);

        var refinement = await BuildHistoryRefinementAsync(
            projectId, priorVideoMultiplier, qaRetryOnFail, qaMaxRetries, actual, ct)
            .ConfigureAwait(false);
        // H5: scale video estimate with blended expected takes (QA mult and/or learned p50).
        var (qaVideoMultiplier, retriesScaled) = ApplyQaRetryScaling(qaRetryOnFail, refinement, retries);
        retries = retriesScaled;

        var (blueprintClips, estimateBasis) = await ResolveBlueprintAndEstimateBasisAsync(projectId, cfg, ct)
            .ConfigureAwait(false);

        var onDisk = IndexOnDiskClips(projectId);
        var heroes = await LoadHeroMapAsync(projectId, ct).ConfigureAwait(false);

        var draftCfg = CloneCfg(cfg, draftRes, retries);
        var heroCfg = CloneCfg(cfg, heroRes, retries);
        var draftRates = RatesFromConfig(draftCfg);
        var heroRates = RatesFromConfig(heroCfg);

        var sceneTotals = AccumulateSceneCostRows(
            blueprintClips, onDisk, heroes, actual, draftRes, heroRes, rates, draftRates, heroRates, retries);
        sceneTotals.Rows.Sort((a, b) => a.Scene.CompareTo(b.Scene));

        // A1: when any media is on disk, upgrade basis to remaining (spent + missing operational).
        estimateBasis = UpgradeEstimateBasisIfMediaOnDisk(estimateBasis, sceneTotals.ClipsOnDisk);

        var scenarios = BuildScenarios(blueprintClips, onDisk, cfg, retries, draftRes, heroRes);

        // Non-video scope (model-dependent): cast portraits, optional voice, music, planning.
        var videoModel = GetStr(cfg, Keys.ModelName, "");
        var imageModel = GetStr(cfg, Keys.ImageModelName, "");
        var planningModel = GetStr(cfg, "planning_model_name",
            GetStr(cfg, "chat_model_name", ""));
        var voiceModel = GetStr(cfg, "voice_model_name", "");

        var plans = EstimateNonVideoScopes(
            projectId, cfg, rates, blueprintClips, estimateBasis,
            sceneTotals.ClipsTotal, qaRetryOnFail, qaVideoMultiplier);
        var estimateByCategory = BuildEstimateByCategory(plans, sceneTotals.AllDraft);

        // Blend unit costs with portfolio averages when sample sizes allow.
        ApplyHistoryUnitCosts(estimateByCategory, refinement, sceneTotals.ClipsTotal);

        var filmTotals = CombineFilmTotals(estimateByCategory, plans, sceneTotals);

        var basisNote = EstimateBasisNote(estimateBasis);
        var clipSource = EstimateClipSource(estimateBasis);
        var estimateConfidence = EstimateConfidence(estimateBasis);

        var mult = multEarly;
        // Snapshot list-rate category totals before applying charge multiplier for customer display.
        var estimateList = estimateByCategory.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        ApplyChargeToReport(estimateByCategory, sceneTotals, scenarios, filmTotals, mult);

        var listFullDraft = estimateList.Values.Sum();
        var listFullHero = ChargePricing.RoundMoney(
            filmTotals.CatalogVideoHero * filmTotals.VideoScale +
            estimateList.GetValueOrDefault(CostCategories.Screenplay) +
            estimateList.GetValueOrDefault(CostCategories.Characters) +
            estimateList.GetValueOrDefault(CostCategories.Voice) +
            estimateList.GetValueOrDefault(CostCategories.Music) +
            estimateList.GetValueOrDefault(CostCategories.Review) +
            estimateList.GetValueOrDefault(CostCategories.Other));

        // A1/A3/H5/H6 decision-facing $ band and duration labels.
        // Prefer learned expected takes (p50 blend); fall back to QA video multiplier.
        var expectedTakes = ResolveExpectedTakes(refinement, qaVideoMultiplier);
        refinement.ExpectedTakes = expectedTakes;

        var (takesLearning, takesP25, takesP75) = await LoadTakesLearningAsync(
            projectId, expectedTakes, refinement, ct).ConfigureAwait(false);

        var (costLow, costHigh, costPoint) = ComputeCostBand(
            filmTotals.FullDraft, expectedTakes, takesP25, takesP75);
        var (durationMinutes, durationLabel) = ComputeDurationLabels(sceneTotals.SecOnDisk, sceneTotals.SecMissing);
        var showRange = ShouldShowCostRange(
            estimateBasis, costPoint, takesLearning, estimateConfidence, costHigh, costLow);
        var costLabel = BuildCostLabel(estimateBasis, costPoint, showRange, costLow, costHigh);

        // A5 — remaining strip when any media exists (spent + missing operational).
        var remainingUsd = Math.Round(filmTotals.RemainingDraft, 2);
        var spentUsd = Math.Round(actual.ActualUsd, 2);
        var finishUsd = Math.Round(spentUsd + remainingUsd, 2);
        var remainingLabel = BuildRemainingLabel(
            sceneTotals.ClipsOnDisk, spentUsd, remainingUsd, finishUsd, sceneTotals.ClipsMissing);

        var productionMode = ProductionModes.FromConfig(cfg);

        return AssembleCostReport(new CostReportParts
        {
            ProjectId = projectId,
            DraftRes = draftRes,
            HeroRes = heroRes,
            VideoModel = videoModel,
            Cfg = cfg,
            ImageModel = imageModel,
            PlanningModel = planningModel,
            VoiceModel = voiceModel,
            EstimateBasis = estimateBasis,
            ClipSource = clipSource,
            EstimateConfidence = estimateConfidence,
            CostLow = costLow,
            CostPoint = costPoint,
            CostHigh = costHigh,
            DurationMinutes = durationMinutes,
            DurationLabel = durationLabel,
            CostLabel = costLabel,
            RemainingLabel = remainingLabel,
            ProductionMode = productionMode,
            TakesLearning = takesLearning,
            VoicePlan = plans.Voice,
            Mult = mult,
            DraftRates = draftRates,
            HeroRates = heroRates,
            Retries = retries,
            SceneTotals = sceneTotals,
            Actual = actual,
            FilmTotals = filmTotals,
            EstimateByCategory = estimateByCategory,
            EstimateList = estimateList,
            ListFullDraft = listFullDraft,
            ListFullHero = listFullHero,
            Refinement = refinement,
            Scenarios = scenarios,
            Ledger = ledger,
            RecentLimit = recentLimit,
            BasisNote = basisNote,
            QaRetryOnFail = qaRetryOnFail,
            QaVideoMultiplier = qaVideoMultiplier,
        });
    }

    private static void ApplyChargeMultiplierInPlace(Dictionary<string, double> byCategory, double mult)
    {
        foreach (var key in byCategory.Keys.ToList())
            byCategory[key] = ChargePricing.RoundMoney(ChargePricing.ToCharge(byCategory[key], mult));
    }

    private static double ResolvePriorVideoMultiplier(Dictionary<string, JsonElement> cfg, int qaMaxRetries)
    {
        var priorVideoMultiplier = 1.3;
        if (cfg.TryGetValue(Keys.CostEstimates, out var ceQa) && ceQa.ValueKind == JsonValueKind.Object)
        {
            if (ceQa.TryGetProperty("qa_retry_video_multiplier", out var qm) &&
                qm.TryGetDouble(out var qmv) && qmv >= 1.0)
                priorVideoMultiplier = Math.Clamp(qmv, 1.0, 3.0);
            else if (ceQa.TryGetProperty("qa_fail_rate", out var fr) && fr.TryGetDouble(out var frv) && frv >= 0)
                priorVideoMultiplier = 1.0 + Math.Clamp(frv, 0, 1) * Math.Max(1, qaMaxRetries);
        }
        return priorVideoMultiplier;
    }

    private static (double QaVideoMultiplier, double Retries) ApplyQaRetryScaling(
        bool qaRetryOnFail,
        CostEstimateRefinement refinement,
        double retries)
    {
        var qaVideoMultiplier = Math.Max(
            qaRetryOnFail ? refinement.AppliedVideoMultiplier : 1.0,
            refinement.ExpectedTakes > 0 ? refinement.ExpectedTakes : 1.0);
        var qaExpectedExtraGens = Math.Max(0, qaVideoMultiplier - 1.0);
        if (qaExpectedExtraGens > retries)
            retries = qaExpectedExtraGens;
        return (qaVideoMultiplier, retries);
    }

    private async Task<(List<BlueprintSceneClips> Clips, string EstimateBasis)> ResolveBlueprintAndEstimateBasisAsync(
        string projectId,
        Dictionary<string, JsonElement> cfg,
        CancellationToken ct)
    {
        var blueprintClips = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var estimateBasis = blueprintClips.Any(s => s.Clips.Count > 0) ? Keys.ShotPlan : "none";
        if (estimateBasis == "none")
        {
            // A2: post-import / fountain shortcut (before shot plan) — always estimate from screenplay.
            blueprintClips = await LoadScreenplayDerivedClipsAsync(projectId, cfg).ConfigureAwait(false);
            if (blueprintClips.Any(s => s.Clips.Count > 0))
                estimateBasis = Keys.Screenplay;
        }
        return (blueprintClips, estimateBasis);
    }

    private static string UpgradeEstimateBasisIfMediaOnDisk(string estimateBasis, int clipsOnDisk)
    {
        if ((estimateBasis is Keys.ShotPlan or Keys.Screenplay) && clipsOnDisk > 0)
            return Keys.Remaining;
        return estimateBasis;
    }

    private sealed class SceneCostTotals
    {
        public double Spent;
        public double RemainingDraft;
        public double RemainingHero;
        public double AllDraft;
        public double AllHero;
        public int ClipsOnDisk;
        public int ClipsMissing;
        public int ClipsTotal;
        public double SecOnDisk;
        public double SecMissing;
        public List<CostSceneRow> Rows { get; } = new();
    }

    private sealed class SceneClipCostAccum
    {
        public double SSpent, SMiss, SHero, SAllD, SAllH, DOn, DMiss;
        public int NDisk, NMiss, NAll;
    }

    private static SceneCostTotals AccumulateSceneCostRows(
        List<BlueprintSceneClips> blueprintClips,
        Dictionary<int, Dictionary<int, bool>> onDisk,
        Dictionary<int, string> heroes,
        CostLedgerSummary actual,
        string draftRes,
        string heroRes,
        Dictionary<string, object?> rates,
        Dictionary<string, object?> draftRates,
        Dictionary<string, object?> heroRates,
        double retries)
    {
        var totals = new SceneCostTotals();
        foreach (var scene in blueprintClips)
            AccumulateOneScene(
                scene, onDisk, heroes, actual, draftRes, heroRes, rates, draftRates, heroRates, retries, totals);
        return totals;
    }

    private static void AccumulateOneScene(
        BlueprintSceneClips scene,
        Dictionary<int, Dictionary<int, bool>> onDisk,
        Dictionary<int, string> heroes,
        CostLedgerSummary actual,
        string draftRes,
        string heroRes,
        Dictionary<string, object?> rates,
        Dictionary<string, object?> draftRates,
        Dictionary<string, object?> heroRates,
        double retries,
        SceneCostTotals totals)
    {
        var sn = scene.SceneNumber;
        var diskMap = onDisk.GetValueOrDefault(sn) ?? new Dictionary<int, bool>();
        heroes.TryGetValue(sn, out var heroResForScene);
        var isHero = !string.IsNullOrEmpty(heroResForScene);
        var acc = new SceneClipCostAccum();
        foreach (var clip in scene.Clips)
            AccumulateOneClip(clip, diskMap, isHero, heroResForScene, draftRes, heroRes, rates, draftRates, heroRates, retries, acc);

        totals.Spent += acc.SSpent;
        totals.RemainingDraft += acc.SMiss;
        totals.RemainingHero += acc.SHero;
        totals.AllDraft += acc.SAllD;
        totals.AllHero += acc.SAllH;
        totals.ClipsOnDisk += acc.NDisk;
        totals.ClipsMissing += acc.NMiss;
        totals.ClipsTotal += acc.NAll;
        totals.SecOnDisk += acc.DOn;
        totals.SecMissing += acc.DMiss;

        actual.ByScene.TryGetValue(sn.ToString(CultureInfo.InvariantCulture), out var actualScene);
        totals.Rows.Add(BuildCostSceneRow(scene, acc, isHero, heroResForScene, actualScene));
    }

    private static void AccumulateOneClip(
        BlueprintClip clip,
        Dictionary<int, bool> diskMap,
        bool isHero,
        string? heroResForScene,
        string draftRes,
        string heroRes,
        Dictionary<string, object?> rates,
        Dictionary<string, object?> draftRates,
        Dictionary<string, object?> heroRates,
        double retries,
        SceneClipCostAccum acc)
    {
        acc.NAll++;
        var on = diskMap.GetValueOrDefault(clip.ClipNumber);
        if (on) acc.NDisk++;
        else acc.NMiss++;

        var spentRes = isHero ? (heroResForScene ?? heroRes) : draftRes;
        var spentEst = EstimateClip(clip, spentRes, rates, retries);
        var missEst = EstimateClip(clip, draftRes, draftRates, retries);
        var heroEst = EstimateClip(clip, heroRes, heroRates, retries);
        var allD = EstimateClip(clip, draftRes, draftRates, retries);
        var allH = EstimateClip(clip, heroRes, heroRates, retries);

        acc.SAllD += allD.Usd;
        acc.SAllH += allH.Usd;
        if (on)
        {
            acc.SSpent += spentEst.Usd;
            acc.DOn += spentEst.DurationSec;
            if (!isHero)
                acc.SHero += heroEst.Usd;
        }
        else
        {
            acc.SMiss += missEst.Usd;
            acc.DMiss += missEst.DurationSec;
        }
    }

    private static CostSceneRow BuildCostSceneRow(
        BlueprintSceneClips scene,
        SceneClipCostAccum acc,
        bool isHero,
        string? heroResForScene,
        double actualScene) =>
        new()
        {
            Scene = scene.SceneNumber,
            Setting = scene.Setting.Length > 60 ? scene.Setting[..60] : scene.Setting,
            ClipsTotal = acc.NAll,
            ClipsOnDisk = acc.NDisk,
            ClipsMissing = acc.NMiss,
            IsHero = isHero,
            HeroResolution = heroResForScene,
            CharactersOnScreen = scene.CharactersOnScreen,
            LocationIds = scene.LocationIds,
            PrimaryLocationId = scene.PrimaryLocationId,
            SpentUsd = Math.Round(acc.SSpent, 2),
            ActualUsd = Math.Round(actualScene, 2),
            RemainingDraftUsd = Math.Round(acc.SMiss, 2),
            HeroUpgradeUsd = Math.Round(acc.SHero, 2),
            AllDraftUsd = Math.Round(acc.SAllD, 2),
            AllHeroUsd = Math.Round(acc.SAllH, 2),
            DurationOnDiskSec = Math.Round(acc.DOn, 1),
            DurationMissingSec = Math.Round(acc.DMiss, 1),
        };

    private sealed class NonVideoPlans
    {
        public ScopeEstimate Cast;
        public ScopeEstimate Voice;
        public ScopeEstimate Music;
        public ScopeEstimate Planning;
        public ScopeEstimate Review;
    }

    private NonVideoPlans EstimateNonVideoScopes(
        string projectId,
        Dictionary<string, JsonElement> cfg,
        Dictionary<string, object?> rates,
        List<BlueprintSceneClips> blueprintClips,
        string estimateBasis,
        int clipsTotal,
        bool qaRetryOnFail,
        double qaVideoMultiplier) =>
        new()
        {
            Cast = EstimateCharacterGeneration(projectId, rates, cfg),
            Voice = EstimateVoiceGeneration(projectId, blueprintClips, rates, cfg),
            Music = EstimateMusicGeneration(blueprintClips, rates, cfg),
            Planning = EstimatePlanningWork(blueprintClips, estimateBasis, rates, cfg),
            Review = EstimateAutomatedReview(clipsTotal, rates, cfg, qaRetryOnFail, qaVideoMultiplier),
        };

    private static Dictionary<string, double> BuildEstimateByCategory(NonVideoPlans plans, double allDraft) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CostCategories.Screenplay] = Math.Round(plans.Planning.Usd, 2),
            [CostCategories.Characters] = Math.Round(plans.Cast.Usd, 2),
            [CostCategories.Video] = Math.Round(allDraft, 2),
            [CostCategories.Voice] = Math.Round(plans.Voice.Usd, 2),
            [CostCategories.Music] = Math.Round(plans.Music.Usd, 2),
            [CostCategories.Review] = Math.Round(plans.Review.Usd, 2),
            [CostCategories.Other] = 0,
        };

    private sealed class FilmTotals
    {
        public double Spent;
        public double RemainingDraft;
        public double RemainingHero;
        public double FullDraft;
        public double FullHero;
        public double CatalogVideoHero;
        public double VideoScale;
    }

    private static FilmTotals CombineFilmTotals(
        Dictionary<string, double> estimateByCategory,
        NonVideoPlans plans,
        SceneCostTotals sceneTotals)
    {
        var allDraft = estimateByCategory.GetValueOrDefault(CostCategories.Video);
        var nonVideo =
            estimateByCategory.GetValueOrDefault(CostCategories.Screenplay) +
            estimateByCategory.GetValueOrDefault(CostCategories.Characters) +
            estimateByCategory.GetValueOrDefault(CostCategories.Voice) +
            estimateByCategory.GetValueOrDefault(CostCategories.Music) +
            estimateByCategory.GetValueOrDefault(CostCategories.Review) +
            estimateByCategory.GetValueOrDefault(CostCategories.Other);
        var fullDraft = allDraft + nonVideo;
        var catalogVideoDraft = sceneTotals.AllDraft;
        var catalogVideoHero = sceneTotals.AllHero;
        var videoScale = catalogVideoDraft > 0.01 ? allDraft / catalogVideoDraft : 1.0;
        var fullHero = catalogVideoHero * videoScale + nonVideo;
        // Remaining first-pass: missing video + unfinished cast/voice/music (planning mostly already spent).
        var remainingExtras = plans.Cast.RemainingUsd + plans.Voice.RemainingUsd + plans.Music.RemainingUsd
            + plans.Review.RemainingUsd;
        return new FilmTotals
        {
            Spent = sceneTotals.Spent,
            RemainingDraft = sceneTotals.RemainingDraft + remainingExtras,
            RemainingHero = sceneTotals.RemainingHero,
            FullDraft = fullDraft,
            FullHero = fullHero,
            CatalogVideoHero = catalogVideoHero,
            VideoScale = videoScale,
        };
    }

    private static string EstimateBasisNote(string estimateBasis) => estimateBasis switch
    {
        Keys.ShotPlan => "Clip count from the shot plan.",
        Keys.Screenplay => "Clip count estimated from screenplay scene lengths (before shot plan).",
        Keys.Remaining => "Operational estimate: spent ledger + remaining planned clips.",
        _ => "Import a book or fountain screenplay to unlock a film estimate.",
    };

    private static string EstimateClipSource(string estimateBasis) => estimateBasis switch
    {
        Keys.ShotPlan => "blueprint",
        Keys.Screenplay => "synthetic_screenplay",
        Keys.Remaining => Keys.Remaining,
        _ => "none",
    };

    private static string EstimateConfidence(string estimateBasis) => estimateBasis switch
    {
        Keys.Remaining => "best",
        Keys.ShotPlan => "good",
        Keys.Screenplay => "rough",
        _ => "very_low",
    };

    private static void ApplyChargeToReport(
        Dictionary<string, double> estimateByCategory,
        SceneCostTotals sceneTotals,
        List<CostScenarioRow> scenarios,
        FilmTotals filmTotals,
        double mult)
    {
        ApplyChargeMultiplierInPlace(estimateByCategory, mult);
        filmTotals.Spent = ChargePricing.ToCharge(filmTotals.Spent, mult);
        filmTotals.RemainingDraft = ChargePricing.ToCharge(filmTotals.RemainingDraft, mult);
        filmTotals.RemainingHero = ChargePricing.ToCharge(filmTotals.RemainingHero, mult);
        filmTotals.FullDraft = ChargePricing.ToCharge(filmTotals.FullDraft, mult);
        filmTotals.FullHero = ChargePricing.ToCharge(filmTotals.FullHero, mult);
        foreach (var row in sceneTotals.Rows)
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
    }

    private static double ResolveExpectedTakes(CostEstimateRefinement refinement, double qaVideoMultiplier) =>
        Math.Max(1.0, refinement.ExpectedTakes > 0
            ? refinement.ExpectedTakes
            : qaVideoMultiplier);

    private async Task<(CostTakesLearning Learning, double TakesP25, double TakesP75)> LoadTakesLearningAsync(
        string projectId,
        double expectedTakes,
        CostEstimateRefinement refinement,
        CancellationToken ct)
    {
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
                ApplyTakesTelemetryToLearning(takesLearning, g, p, expectedTakes, ref takesP25, ref takesP75);
            }
        }
        catch
        {
            // H9 fail-open
        }
        return (takesLearning, takesP25, takesP75);
    }

    private static void ApplyTakesTelemetryToLearning(
        CostTakesLearning takesLearning,
        TakesTelemetryStats g,
        TakesTelemetryStats p,
        double expectedTakes,
        ref double takesP25,
        ref double takesP75)
    {
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
        ApplyTakesRange(takesLearning, g, p, ref takesP25, ref takesP75);
        ApplyTakesCalibration(takesLearning, p, expectedTakes);
    }

    private static TakesTelemetryStats? PickTakesBlendSource(TakesTelemetryStats projectTakes, TakesTelemetryStats globalTakes) =>
        projectTakes.SufficientForBlend ? projectTakes
            : globalTakes.SufficientForBlend ? globalTakes
            : null;

    private static void ApplyTakesRange(
        CostTakesLearning takesLearning,
        TakesTelemetryStats g,
        TakesTelemetryStats p,
        ref double takesP25,
        ref double takesP75)
    {
        var rangeSrc = PickTakesBlendSource(p, g);
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
    }

    private static void ApplyTakesCalibration(CostTakesLearning takesLearning, TakesTelemetryStats p, double expectedTakes)
    {
        if (p.ClipSampleCount > 0 && expectedTakes > 0)
        {
            var delta = p.MeanTakesPerClip - expectedTakes;
            takesLearning.CalibrationLabel =
                $"This project ~{p.MeanTakesPerClip:0.##} takes/clip actual vs ~{expectedTakes:0.##} in estimate" +
                (Math.Abs(delta) < 0.05 ? " (on track)." : delta > 0 ? " (running hotter)." : " (running cooler).");
        }
    }

    private static (double CostLow, double CostHigh, double CostPoint) ComputeCostBand(
        double fullDraft,
        double expectedTakes,
        double takesP25,
        double takesP75)
    {
        // first-pass unit cost ≈ point / expectedTakes (point already includes expected takes via retries)
        var costPoint = Math.Round(fullDraft, 2);
        var firstPassUnit = expectedTakes > 0.01 ? costPoint / expectedTakes : costPoint;
        var costLow = Math.Round(firstPassUnit * takesP25, 2);
        var costHigh = Math.Round(firstPassUnit * takesP75, 2);
        if (costLow > costPoint) costLow = costPoint;
        if (costHigh < costPoint) costHigh = costPoint;
        return (costLow, costHigh, costPoint);
    }

    private static (double? DurationMinutes, string DurationLabel) ComputeDurationLabels(double secOnDisk, double secMissing)
    {
        var durationSec = secOnDisk + secMissing;
        double? durationMinutes = durationSec > 0.5
            ? Math.Round(durationSec / 60.0, 1)
            : null;
        var durationLabel = durationMinutes is > 0
            ? $"~{durationMinutes:0.#} min"
            : "duration TBD";
        return (durationMinutes, durationLabel);
    }

    private static bool ShouldShowCostRange(
        string estimateBasis,
        double costPoint,
        CostTakesLearning takesLearning,
        string estimateConfidence,
        double costHigh,
        double costLow) =>
        estimateBasis != "none" && costPoint > 0 &&
        (takesLearning.SufficientForRange || estimateConfidence is "rough" or "very_low") &&
        Math.Abs(costHigh - costLow) >= 0.5;

    private static string BuildCostLabel(
        string estimateBasis,
        double costPoint,
        bool showRange,
        double costLow,
        double costHigh) =>
        estimateBasis == "none" || costPoint <= 0
            ? "—"
            : showRange
                ? $"~${costLow:0.##}–${costHigh:0.##}"
                : $"~${costPoint:0.##}";

    private static string BuildRemainingLabel(
        int clipsOnDisk,
        double spentUsd,
        double remainingUsd,
        double finishUsd,
        int clipsMissing)
    {
        string remainingLabel = "";
        if (clipsOnDisk > 0 || spentUsd > 0.005)
        {
            var parts = new List<string>
            {
                $"Spent ${spentUsd:0.##}",
                $"remaining ${remainingUsd:0.##}",
                $"finish ~${finishUsd:0.##}",
            };
            if (clipsMissing > 0)
                parts.Add($"{clipsMissing} clip{(clipsMissing == 1 ? "" : "s")} missing");
            else if (clipsOnDisk > 0 && clipsMissing == 0)
                parts.Add("all clips on disk");
            remainingLabel = string.Join(" · ", parts);
        }
        return remainingLabel;
    }

    private static double? CostBandOrNull(string estimateBasis, double value) =>
        estimateBasis == "none" ? null : value;

    private static string BuildCostReportNotes(CostReportParts p) =>
        p.BasisNote + " " +
        $"Rates from selected models (video={p.VideoModel}, image={p.ImageModel}" +
        (p.VoicePlan.Included ? $", voice={p.VoiceModel}" : "") + "). " +
        $"Charge multiplier ×{p.Mult:0.##} on estimates and new actual charges. " +
        (p.QaRetryOnFail
            ? $"Quality gate retry ON (admin auto-regen; video ×{p.QaVideoMultiplier:0.##}). "
            : "Quality gate retry OFF. ") +
        (string.IsNullOrWhiteSpace(p.Refinement.Notes) ? "" : p.Refinement.Notes + " ") +
        "Actual display = list rates in cost_ledger × admin charge multiplier (list rates only in storage).";

    private sealed class CostReportParts
    {
        public string ProjectId = "";
        public string DraftRes = "";
        public string HeroRes = "";
        public string VideoModel = "";
        public Dictionary<string, JsonElement> Cfg = null!;
        public string ImageModel = "";
        public string PlanningModel = "";
        public string VoiceModel = "";
        public string EstimateBasis = "";
        public string ClipSource = "";
        public string EstimateConfidence = "";
        public double CostLow;
        public double CostPoint;
        public double CostHigh;
        public double? DurationMinutes;
        public string DurationLabel = "";
        public string CostLabel = "";
        public string RemainingLabel = "";
        public string ProductionMode = "";
        public CostTakesLearning TakesLearning = null!;
        public ScopeEstimate VoicePlan;
        public double Mult;
        public Dictionary<string, object?> DraftRates = null!;
        public Dictionary<string, object?> HeroRates = null!;
        public double Retries;
        public SceneCostTotals SceneTotals = null!;
        public CostLedgerSummary Actual = null!;
        public FilmTotals FilmTotals = null!;
        public Dictionary<string, double> EstimateByCategory = null!;
        public Dictionary<string, double> EstimateList = null!;
        public double ListFullDraft;
        public double ListFullHero;
        public CostEstimateRefinement Refinement = null!;
        public List<CostScenarioRow> Scenarios = null!;
        public IReadOnlyList<CostEvent> Ledger = null!;
        public int RecentLimit;
        public string BasisNote = "";
        public bool QaRetryOnFail;
        public double QaVideoMultiplier;
    }

    private static CostReport AssembleCostReport(CostReportParts p)
    {
        var rows = p.SceneTotals.Rows;
        var film = p.FilmTotals;
        return new CostReport
        {
            ProjectId = p.ProjectId,
            DraftResolution = p.DraftRes,
            HeroResolution = p.HeroRes,
            ModelName = p.VideoModel,
            VideoProvider = ResolveVideoProvider(p.Cfg, p.VideoModel),
            ImageModelName = p.ImageModel,

            PlanningModelName = p.PlanningModel,
            VoiceModelName = p.VoicePlan.Included ? p.VoiceModel : null,
            EstimateBasis = p.EstimateBasis,
            ClipSource = p.ClipSource,
            EstimateConfidence = p.EstimateConfidence,
            CostLowUsd = CostBandOrNull(p.EstimateBasis, p.CostLow),
            CostPointUsd = CostBandOrNull(p.EstimateBasis, p.CostPoint),
            CostHighUsd = CostBandOrNull(p.EstimateBasis, p.CostHigh),
            DurationMinutes = p.DurationMinutes,
            DurationLabel = p.DurationLabel,
            CostLabel = p.CostLabel,
            RemainingLabel = p.RemainingLabel,
            ProductionMode = p.ProductionMode,
            TakesLearning = p.TakesLearning,
            VoiceIncludedInEstimate = p.VoicePlan.Included,
            ChargeMultiplier = p.Mult,
            OutputRateDraft = OutputRate(p.DraftRes, p.DraftRates),
            OutputRateHero = OutputRate(p.HeroRes, p.HeroRates),
            AssumeAvgRetries = p.Retries,
            Summary = new CostReportSummary
            {
                ClipsTotal = p.SceneTotals.ClipsTotal,
                ClipsOnDisk = p.SceneTotals.ClipsOnDisk,
                ClipsMissing = p.SceneTotals.ClipsMissing,
                SecOnDisk = Math.Round(p.SceneTotals.SecOnDisk, 1),
                SecMissing = Math.Round(p.SceneTotals.SecMissing, 1),
                SpentUsd = Math.Round(film.Spent, 2),
                ActualUsd = p.Actual.ActualUsd,
                ActualEvents = p.Actual.EventCount,
                ActualVideoJobs = p.Actual.VideoJobs,
                ActualVideoSec = p.Actual.VideoSec,
                RemainingFirstPassUsd = Math.Round(film.RemainingDraft, 2),
                RemainingHeroUpgradeUsd = Math.Round(film.RemainingHero, 2),
                FinishDraftUsd = Math.Round(film.Spent + film.RemainingDraft, 2),
                FinishDraftPlusHeroUsd = Math.Round(film.Spent + film.RemainingDraft + film.RemainingHero, 2),
                FinishFromActualUsd = Math.Round(p.Actual.ActualUsd + film.RemainingDraft, 2),
                FullFilmAllDraftUsd = Math.Round(film.FullDraft, 2),
                FullFilmAllHeroUsd = Math.Round(film.FullHero, 2),
                FullFilmAllDraftListUsd = Math.Round(p.ListFullDraft, 2),
                FullFilmAllHeroListUsd = p.ListFullHero,
                ScenesWithMedia = rows.Count(r => r.ClipsOnDisk > 0),
                ScenesHero = rows.Count(r => r.IsHero),
                ScenesTotal = rows.Count,
            },
            Actual = p.Actual,
            EstimateByCategory = p.EstimateByCategory,
            EstimateByCategoryListRate = p.EstimateList,
            Refinement = p.Refinement,
            Scenes = rows,
            Scenarios = p.Scenarios,
            RecentEvents = p.Ledger
                .OrderByDescending(e => e.Ts ?? "")
                .Take(Math.Clamp(p.RecentLimit, 1, 200))
                .ToList(),
            Notes = BuildCostReportNotes(p),
        };
    }

    public async Task<CostBackfillResult> BackfillFromDiskAsync(
        string projectId,
        bool onlyMissing = true,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var defaultRates = RatesFromConfig(cfg);
        var ledger = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var seen = IndexSeenVideoClips(ledger);

        var blueprint = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var onDisk = IndexOnDiskClips(projectId);
        var clipJobs = await LoadClipJobsAsync(projectId, ct).ConfigureAwait(false);
        var defaults = new BackfillDefaults(
            GetStr(cfg, Keys.Resolution, "480p"),
            GetStr(cfg, Keys.ModelName, ""),
            GetStr(cfg, Keys.ImageModelName, ""),
            GetDouble(cfg, "duration_seconds", 8),
            GetBool(defaultRates, "assume_ref_image_per_clip", true));

        var added = 0;
        var skipped = 0;
        foreach (var scene in blueprint)
        {
            await BackfillSceneClipsAsync(
                projectId, scene, onDisk, clipJobs, seen, cfg, defaults, onlyMissing, ct,
                () => added++, () => skipped++).ConfigureAwait(false);
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

    private readonly record struct BackfillDefaults(
        string DefaultRes,
        string DefaultModel,
        string ImageModel,
        double DefaultDur,
        bool AssumeRef);

    private static HashSet<(int, int)> IndexSeenVideoClips(List<JsonElement> ledger)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var e in ledger)
        {
            if (!string.Equals(GetRawKind(e), Keys.Video, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetInt(e, Keys.Scene, out var sn) && TryGetInt(e, "clip", out var cn))
                seen.Add((sn, cn));
        }
        return seen;
    }

    private async Task BackfillSceneClipsAsync(
        string projectId,
        BlueprintSceneClips scene,
        Dictionary<int, Dictionary<int, bool>> onDisk,
        Dictionary<string, Dictionary<string, JsonElement>> clipJobs,
        HashSet<(int, int)> seen,
        Dictionary<string, JsonElement> cfg,
        BackfillDefaults defaults,
        bool onlyMissing,
        CancellationToken ct,
        Action onAdded,
        Action onSkipped)
    {
        var diskMap = onDisk.GetValueOrDefault(scene.SceneNumber) ?? new Dictionary<int, bool>();
        foreach (var clip in scene.Clips)
        {
            if (!diskMap.GetValueOrDefault(clip.ClipNumber))
            {
                onSkipped();
                continue;
            }

            if (onlyMissing && seen.Contains((scene.SceneNumber, clip.ClipNumber)))
            {
                onSkipped();
                continue;
            }

            await AppendBackfillClipEventAsync(projectId, scene, clip, clipJobs, cfg, defaults, ct)
                .ConfigureAwait(false);
            seen.Add((scene.SceneNumber, clip.ClipNumber));
            onAdded();
        }
    }

    private async Task AppendBackfillClipEventAsync(
        string projectId,
        BlueprintSceneClips scene,
        BlueprintClip clip,
        Dictionary<string, Dictionary<string, JsonElement>> clipJobs,
        Dictionary<string, JsonElement> cfg,
        BackfillDefaults defaults,
        CancellationToken ct)
    {
        clipJobs.TryGetValue($"{scene.SceneNumber}_{clip.ClipNumber}", out var job);
        var duration = ResolveBackfillDuration(clip, job, defaults.DefaultDur);
        var res = ResolveBackfillJobString(job, Keys.Resolution, defaults.DefaultRes);
        var model = ResolveBackfillJobString(job, Keys.Model, defaults.DefaultModel);

        var isExtend = string.Equals(
            clip.Continuation, "extend_previous", StringComparison.OrdinalIgnoreCase);
        var rates = RatesFromModels(model, defaults.ImageModel, cfg);
        var priced = PriceVideo(duration, res, rates, defaults.AssumeRef, isExtend, attempts: 1);
        var listUsd = priced.Usd;

        var evt = BuildBackfillCostEvent(scene, clip, job, model, res, rates, priced, listUsd, defaults.AssumeRef, isExtend);
        await AppendCostEventAsync(projectId, evt, save: true, ct).ConfigureAwait(false);
    }

    private static double ResolveBackfillDuration(
        BlueprintClip clip,
        Dictionary<string, JsonElement>? job,
        double defaultDur)
    {
        var duration = clip.DurationSec > 0 ? clip.DurationSec : defaultDur;
        if (job is not null && job.TryGetValue(Keys.DurationSec, out var ds) &&
            ds.TryGetDouble(out var jdur) && jdur > 0)
            duration = jdur;
        return duration;
    }

    private static string ResolveBackfillJobString(
        Dictionary<string, JsonElement>? job,
        string key,
        string fallback)
    {
        if (job is not null && job.TryGetValue(key, out var el) &&
            el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
            return s;
        return fallback;
    }

    private static Dictionary<string, object?> BuildBackfillCostEvent(
        BlueprintSceneClips scene,
        BlueprintClip clip,
        Dictionary<string, JsonElement>? job,
        string model,
        string res,
        Dictionary<string, object?> rates,
        (double Usd, double DurationSec, double RatePerSec, double VideoOut, double RefImg, double ExtendIn) priced,
        double listUsd,
        bool assumeRef,
        bool isExtend) =>
        new()
        {
            ["kind"] = Keys.Video,
            [Keys.Category] = CostCategories.Video,
            [Keys.Scene] = scene.SceneNumber,
            ["clip"] = clip.ClipNumber,
            [Keys.Model] = model,
            [Keys.Provider] = rates.TryGetValue(Keys.VideoProvider, out var vp) ? vp : null,
            ["pricing_source"] = rates.TryGetValue(Keys.VideoPricingSource, out var vps) ? vps : null,
            [Keys.RequestId] = job is not null && job.TryGetValue(Keys.RequestId, out var rid)
                ? rid.GetString() ?? ""
                : "",
            ["has_ref_image"] = assumeRef,
            ["is_extend"] = isExtend,
            [Keys.Source] = "backfill",
            [Keys.DurationSec] = priced.DurationSec,
            ["attempts"] = 1.0,
            [Keys.Resolution] = res,
            ["output_rate_per_sec"] = priced.RatePerSec,
            ["video_output_usd"] = priced.VideoOut,
            ["ref_image_usd"] = priced.RefImg,
            ["extend_input_usd"] = priced.ExtendIn,
            [Keys.ListUsd] = listUsd,
            ["usd"] = listUsd,
            [Keys.Currency] = "USD",
            ["extra"] = new Dictionary<string, object?> { ["backfill"] = true },
        };

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
            imageModelId: GetStr(cfg, Keys.ImageModelName, ""),
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
            ["kind"] = Keys.Video,
            [Keys.Category] = CostCategories.Video,
            [Keys.Scene] = scene,
            ["clip"] = clip,
            [Keys.Model] = model,
            [Keys.Provider] = rates.TryGetValue(Keys.VideoProvider, out var vp) ? vp : null,
            ["pricing_source"] = rates.TryGetValue(Keys.VideoPricingSource, out var vps) ? vps : null,
            [Keys.RequestId] = requestId ?? "",
            ["has_ref_image"] = hasRefImage,
            ["is_extend"] = isExtend,
            [Keys.Source] = "list_rate",
            // Primary duration used for pricing (probed when available)
            [Keys.DurationSec] = priced.DurationSec,
            ["attempts"] = 1.0,
            [Keys.Resolution] = resolution,
            ["output_rate_per_sec"] = priced.RatePerSec,
            ["video_output_usd"] = priced.VideoOut,
            ["ref_image_usd"] = priced.RefImg,
            ["extend_input_usd"] = priced.ExtendIn,
            // List rate only in ledger — multiplier applied at display / credit debit time.
            [Keys.ListUsd] = listUsd,
            ["usd"] = listUsd,
            [Keys.Currency] = "USD",
            [Keys.UserId] = userId ?? "",
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
                if (cfg.TryGetValue(Keys.CostEstimates, out var ceOpt) &&
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
                metaKind: Keys.Video,
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
        var ledgerOk = await TryStampLedgerTakeReasonAsync(projectId, scene, clip, r, takeIndex, ct)
            .ConfigureAwait(false);

        return dbOk || ledgerOk;
    }

    private async Task<bool> TryStampLedgerTakeReasonAsync(
        string projectId,
        int scene,
        int clip,
        string r,
        int? takeIndex,
        CancellationToken ct)
    {
        try
        {
            var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
            if (!File.Exists(path))
                return false;

            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                root[p.Name] = p.Value.Deserialize<object>();

            if (!doc.RootElement.TryGetProperty(Keys.CostLedger, out var ledger) ||
                ledger.ValueKind != JsonValueKind.Array)
                return false;

            var list = ledger.EnumerateArray().Select(x => x.Clone()).ToList();
            var ledgerOk = TryStampMatchingVideoEvent(list, scene, clip, takeIndex, r);
            if (!ledgerOk)
                return false;

            root[Keys.CostLedger] = list.Select(x => x.Deserialize<object>()).ToList();
            var json = JsonSerializer.Serialize(root, JsonDefaults.Indented);
            await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // H9 fail-open
            return false;
        }
    }

    private static bool TryStampMatchingVideoEvent(
        List<JsonElement> list,
        int scene,
        int clip,
        int? takeIndex,
        string r)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var e = list[i];
            if (!string.Equals(GetRawKind(e), Keys.Video, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetInt(e, Keys.Scene, out var sn) || sn != scene) continue;
            if (!TryGetInt(e, "clip", out var cn) || cn != clip) continue;
            if (takeIndex is > 0 && TryGetInt(e, "take_index", out var ti) && ti != takeIndex)
                continue;
            var dict = e.Deserialize<Dictionary<string, object?>>()
                       ?? new Dictionary<string, object?>();
            dict["reason"] = r;
            list[i] = JsonSerializer.SerializeToElement(dict);
            return true;
        }
        return false;
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
            if (!string.Equals(GetRawKind(e), Keys.Video, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetInt(e, Keys.Scene, out var sn) || sn != scene) continue;
            if (!TryGetInt(e, "clip", out var cn) || cn != clip) continue;
            prior++;
            if (e.TryGetProperty("ts", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    tsEl.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed) &&
                (lastTs is null || parsed > lastTs))
            {
                lastTs = parsed;
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
            [Keys.Category] = CostCategories.Characters,
            [Keys.Model] = entry.Id,
            ["character"] = character ?? "",
            ["n_images"] = n,
            ["unit_usd"] = unit,
            [Keys.ListUsd] = listUsd,
            ["usd"] = listUsd,
            [Keys.Currency] = "USD",
            [Keys.Source] = "list_rate",
            ["pricing_source"] = isEstimated ? "estimated_fallback" : Keys.ModelCatalog,
            [Keys.Provider] = entry.ProviderId,
            [Keys.UserId] = userId ?? "",
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
            [Keys.Category] = rec.Category ?? CostCategories.Resolve(rec.Kind, rec.Mode),
            [Keys.Model] = rec.Model,
            [Keys.Provider] = rec.Provider,
            ["mode"] = rec.Mode,
            [Keys.RequestId] = rec.RequestId ?? "",
            [Keys.Source] = "list_rate",
            [Keys.ListUsd] = Math.Round(listUsd, 6),
            ["usd"] = Math.Round(listUsd, 6),
            [Keys.Currency] = "USD",
            [Keys.UserId] = rec.UserId ?? "",
        };
        // Only book-level classifiers with no single scene/clip/character omit these — keep the
        // event dict free of literal nulls rather than writing "scene": null for every such call.
        if (rec.Scene is { } scene) evt[Keys.Scene] = scene;
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
        double retries,
        string draftRes,
        string heroRes)
    {
        var model = GetStr(cfg, Keys.ModelName, "");
        var rows = new List<CostScenarioRow>();
        foreach (var res in new[] { "480p", "720p", Keys.Res1080p })
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
            if (string.Equals(kind, Keys.Video, StringComparison.OrdinalIgnoreCase))
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
            Id = GetJsonStrOrNull(e, "id"),
            Ts = GetJsonStrOrNull(e, "ts"),
            Kind = GetJsonStrOrDefault(e, "kind", "other"),
            Category = CostCategories.Resolve(
                GetJsonStrOrNull(e, "kind"),
                GetJsonStrOrNull(e, "mode"),
                GetJsonStrOrNull(e, Keys.Category)),
            Scene = GetJsonIntOrNull(e, Keys.Scene),
            Clip = GetJsonIntOrNull(e, "clip"),
            Model = GetJsonStrOrNull(e, Keys.Model),
            Resolution = GetJsonStrOrNull(e, Keys.Resolution),
            DurationSec = GetJsonDoubleOrNull(e, Keys.DurationSec),
            Usd = GetJsonDoubleOrZero(e, "usd"),
            ListUsd = GetJsonDoubleOrNull(e, Keys.ListUsd),
            ChargeMultiplier = GetJsonDoubleOrNull(e, "charge_multiplier"),
            Currency = GetJsonStrOrDefault(e, Keys.Currency, "USD"),
            Source = GetJsonStrOrNull(e, Keys.Source),
            Character = GetJsonStrOrNull(e, "character"),
            OutputRatePerSec = GetJsonDoubleOrNull(e, "output_rate_per_sec"),
            HasRefImage = GetJsonBoolOrNull(e, "has_ref_image"),
            IsExtend = GetJsonBoolOrNull(e, "is_extend"),
            UserId = GetJsonStrOrNull(e, Keys.UserId),
            KeyMode = GetJsonStrOrNull(e, "key_mode"),
            TakeKind = GetTakeKindOrTrigger(e),
            TakeIndex = GetJsonIntOrNull(e, "take_index"),
            StableBeatId = GetJsonStrOrNull(e, "stable_beat_id"),
            HadCharRefs = GetJsonBoolOrNull(e, "had_char_refs"),
            HadLocRef = GetJsonBoolOrNull(e, "had_loc_ref"),
            MinutesSincePrevTake = GetJsonDoubleOrNull(e, "minutes_since_prev_take"),
            Reason = GetJsonStrOrNull(e, "reason"),
        };
    }

    private static string? GetJsonStrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var el) ? el.GetString() : null;

    private static string GetJsonStrOrDefault(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var el) ? el.GetString() ?? fallback : fallback;

    private static int? GetJsonIntOrNull(JsonElement e, string name) =>
        TryGetInt(e, name, out var v) ? v : null;

    private static double? GetJsonDoubleOrNull(JsonElement e, string name) =>
        TryGetDouble(e, name, out var v) ? v : null;

    private static double GetJsonDoubleOrZero(JsonElement e, string name) =>
        TryGetDouble(e, name, out var v) ? v : 0;

    private static bool? GetJsonBoolOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var el) &&
        (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? el.GetBoolean()
            : null;

    private static string? GetTakeKindOrTrigger(JsonElement e) =>
        e.TryGetProperty("take_kind", out var tk)
            ? tk.GetString()
            : e.TryGetProperty("trigger", out var tr) ? tr.GetString() : null;

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
            if (!doc.RootElement.TryGetProperty(Keys.CostLedger, out var ledger) ||
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
            if (rawDoc.RootElement.TryGetProperty(Keys.CostLedger, out var existing) &&
                existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existing.EnumerateArray())
                    ledgerList.Add(item.Deserialize<object>());
            }

            var ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            evt.TryAdd("id", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{ledgerList.Count:D4}");
            evt.TryAdd("ts", ts);
            evt.TryAdd(Keys.Currency, "USD");
            ledgerList.Add(evt);
            if (ledgerList.Count > 20000)
                ledgerList = ledgerList.TakeLast(20000).ToList();

            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rawDoc.RootElement.EnumerateObject())
            {
                if (p.Name is Keys.CostLedger or "cost_totals")
                    continue;
                merged[p.Name] = p.Value.Deserialize<object>();
            }

            merged[Keys.CostLedger] = ledgerList;
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

        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var defaultDur = GetDouble(cfg, "duration_seconds", 8);

        foreach (var s in scenes.EnumerateArray())
        {
            var sn = s.TryGetProperty(JsonKeys.SceneNumber, out var sne) && sne.TryGetInt32(out var n) ? n : 0;
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
        var videoDir = Path.Combine(_projects.GetProjectDir(projectId), "assets", Keys.Video);
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
                    p.Value.TryGetProperty(Keys.Resolution, out var r) &&
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
        var videoModelId = GetStr(cfg, Keys.ModelName, "");
        var imageModelId = GetStr(cfg, Keys.ImageModelName, "");
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
            return MissingCatalogRates(videoModelId, imageModelId);

        // If only one side is missing, keep going with empty pricing for that side (no invent of provider).
        video ??= PlaceholderEntry(videoModelId, ModelCapability.Video);
        imagePrimary ??= PlaceholderEntry(imageModelId, ModelCapability.Image);

        // Prefer a cheaper "standard" sibling in the same family when the project uses a quality image model.
        var imageStandard = ResolveStandardImageSibling(imagePrimary);

        var videoTable = BuildVideoRateTable(video);
        var videoBaseTable = BuildVideoBaseRateTable(video);
        // True whenever any part of the price had to fall back to a guess rather than this
        // model's actual catalog data — flows into the recorded cost event below so the ledger
        // itself shows which numbers are verified vendor pricing vs an unverified placeholder.
        // A model priced entirely via a flat base fee (no per-second rate at all, e.g. Hunyuan/Wan)
        // is NOT estimated as long as that base-fee data is real, so check both tables.
        var videoPricingIsEstimated = IsVideoPricingEstimated(video);
        var (qualityUnit, imagePricingIsEstimated) = ResolveImageQualityUnit(imagePrimary);
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
        var refImageSource = refImageCostReal is not null ? Keys.ModelCatalog : Keys.MissingCatalog;
        var extendSource = ResolveExtendCostSource(video, extendCostReal);

        // Overall video pricing is only "fully real" when the output pricing (per-second table OR
        // a flat base fee — a model priced entirely via base fee with no per-second rate, e.g.
        // Hunyuan/Wan, is real as long as that base-fee data is real, so this isn't per-second-only),
        // the reference-image add-on, and (only if this model can extend) the extend add-on are all
        // sourced from the catalog rather than a fallback estimate.
        var videoOutputIsCatalog = !videoPricingIsEstimated;
        var videoPricingFullyReal = IsVideoPricingFullyReal(
            video, videoOutputIsCatalog, refImageCostReal, extendCostReal);

        var rates = BuildCatalogRateTable(
            video, imagePrimary, videoTable, videoBaseTable,
            refImageCostReal, refImageSource, extendCostReal, extendSource,
            qualityUnit, standardUnit, videoPricingFullyReal, imagePricingIsEstimated);

        // Planning knobs only — do not let old manual $/sec tables override vendor rates.
        ApplyCostEstimateOverrides(rates, cfgOverrides);

        return rates;
    }

    private static Dictionary<string, object?> MissingCatalogRates(string? videoModelId, string? imageModelId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Keys.Currency] = "USD",
            [Keys.Source] = Keys.ModelCatalog,
            ["video_model"] = videoModelId ?? "",
            [Keys.VideoProvider] = "",
            ["image_model"] = imageModelId ?? "",
            ["image_provider"] = "",
            [Keys.VideoPricingSource] = "missing_catalog_entry",
            ["image_pricing_source"] = "missing_catalog_entry",
        };

    private static SupportedModelEntry ResolveStandardImageSibling(SupportedModelEntry imagePrimary) =>
        SupportedModelCatalog.ForCapability(ModelCapability.Image)
            .Where(IsCheaperStandardImageSibling(imagePrimary))
            .OrderBy(e => e.ImageCostPerImage)
            .FirstOrDefault()
            ?? imagePrimary;

    private static Func<SupportedModelEntry, bool> IsCheaperStandardImageSibling(SupportedModelEntry imagePrimary) =>
        e => string.Equals(e.ProviderId, imagePrimary.ProviderId, StringComparison.OrdinalIgnoreCase)
             && e.ImageCostPerImage is not null
             && !string.Equals(e.Id, imagePrimary.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoPricingEstimated(SupportedModelEntry video) =>
        video.VideoCostPerSecondByResolution is not { Count: > 0 } &&
        video.VideoBaseCostByResolution is not { Count: > 0 };

    private static (double QualityUnit, bool ImagePricingIsEstimated) ResolveImageQualityUnit(SupportedModelEntry imagePrimary)
    {
        if (imagePrimary.ImageCostPerImage is { } imgCost)
            return (imgCost, false);
        if (imagePrimary.LabMode)
            return (0, true); // lab: unknown, not invented vendor rate
        throw new InvalidOperationException(
            $"Image model '{imagePrimary.Id}' has no imageCostPerImage in models_catalog.json.");
    }

    private static string ResolveExtendCostSource(SupportedModelEntry video, double? extendCostReal) =>
        extendCostReal is not null
            ? Keys.ModelCatalog
            : (video.SupportsVideoContinue ? Keys.MissingCatalog : "not_applicable");

    private static bool IsVideoPricingFullyReal(
        SupportedModelEntry video,
        bool videoOutputIsCatalog,
        double? refImageCostReal,
        double? extendCostReal) =>
        videoOutputIsCatalog
        && refImageCostReal is not null
        && (!video.SupportsVideoContinue || extendCostReal is not null);

    private static double ResolveVideoInputImageCost(SupportedModelEntry video, double? refImageCostReal) =>
        refImageCostReal
        ?? (video.LabMode
            ? 0.0
            : throw new InvalidOperationException(
                $"Video model '{video.Id}' has no videoReferenceImageCost in models_catalog.json "
                + "(use 0 if no separate ref fee; cite pricingNotes)."));

    private static double ResolveVideoExtendCost(SupportedModelEntry video, double? extendCostReal) =>
        extendCostReal
        ?? (video.SupportsVideoContinue
            ? (video.LabMode
                ? 0.0
                : throw new InvalidOperationException(
                    $"Video model '{video.Id}' supports continue but has no videoExtendCostPerSecond "
                    + "in models_catalog.json."))
            : 0.0);

    private static string ResolveVideoPricingSourceLabel(SupportedModelEntry video, bool videoPricingFullyReal) =>
        video.LabMode
            ? "lab_mode"
            : (videoPricingFullyReal ? Keys.ModelCatalog : Keys.MissingCatalog);

    private static string ResolveImagePricingSourceLabel(SupportedModelEntry imagePrimary, bool imagePricingIsEstimated) =>
        imagePrimary.LabMode
            ? "lab_mode"
            : (imagePricingIsEstimated ? Keys.MissingCatalog : Keys.ModelCatalog);

    private static Dictionary<string, object?> BuildCatalogRateTable(
        SupportedModelEntry video,
        SupportedModelEntry imagePrimary,
        Dictionary<string, double> videoTable,
        Dictionary<string, double> videoBaseTable,
        double? refImageCostReal,
        string refImageSource,
        double? extendCostReal,
        string extendSource,
        double qualityUnit,
        double standardUnit,
        bool videoPricingFullyReal,
        bool imagePricingIsEstimated) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Keys.Currency] = "USD",
            [Keys.Source] = Keys.ModelCatalog,
            ["video_model"] = video.Id,
            [Keys.VideoProvider] = video.ProviderId,
            ["image_model"] = imagePrimary.Id,
            ["image_provider"] = imagePrimary.ProviderId,
            ["video_output_per_sec"] = videoTable,
            ["video_base_per_video"] = videoBaseTable,
            ["video_input_image"] = ResolveVideoInputImageCost(video, refImageCostReal),
            ["video_input_image_source"] = refImageSource,
            ["video_input_per_sec"] = ResolveVideoExtendCost(video, extendCostReal),
            ["video_input_per_sec_source"] = extendSource,
            ["image_output_quality"] = qualityUnit,
            ["image_output_standard"] = standardUnit,
            ["assume_ref_image_per_clip"] = true,
            ["assume_extend_fraction"] = 0.0,
            ["assume_avg_retries"] = 0.0,
            [Keys.VideoPricingSource] = ResolveVideoPricingSourceLabel(video, videoPricingFullyReal),
            ["image_pricing_source"] = ResolveImagePricingSourceLabel(imagePrimary, imagePricingIsEstimated),
            ["video_lab_mode"] = video.LabMode,
            ["image_lab_mode"] = imagePrimary.LabMode,
        };

    private static void ApplyCostEstimateOverrides(
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement>? cfgOverrides)
    {
        if (cfgOverrides is not null &&
            cfgOverrides.TryGetValue(Keys.CostEstimates, out var ce) &&
            ce.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ce.EnumerateObject())
            {
                if (IsProtectedRateOverrideKey(p))
                    continue;
                ApplyOneCostEstimateOverride(rates, p);
            }
        }
    }

    private static bool IsProtectedRateOverrideKey(JsonProperty p) =>
        p.NameEquals("video_output_per_sec") ||
        p.NameEquals("image_output_quality") ||
        p.NameEquals("image_output_standard") ||
        p.NameEquals(Keys.Source) ||
        p.NameEquals("video_model") ||
        p.NameEquals(Keys.VideoProvider) ||
        p.NameEquals("image_model") ||
        p.NameEquals("image_provider") ||
        p.NameEquals(Keys.Currency) ||
        p.NameEquals("notes");

    private static void ApplyOneCostEstimateOverride(Dictionary<string, object?> rates, JsonProperty p)
    {
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
            rates[p.Name] = d;
        else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            rates[p.Name] = p.Value.GetBoolean();
        else if (p.Value.ValueKind == JsonValueKind.String)
            rates[p.Name] = p.Value.GetString();
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
                    list.Add(name);
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
        FillMissingRes(table, "720p", Keys.Res1080p, "480p");
        FillMissingRes(table, "480p", "720p", Keys.Res1080p);
        FillMissingRes(table, Keys.Res1080p, "720p", "480p");
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
        var fromModel = SupportedModelCatalog.CatalogProviderId(videoModel, Keys.Video);
        if (!string.IsNullOrWhiteSpace(fromModel))
            return fromModel;
        var fromCfg = GetStr(cfg, Keys.VideoProvider, "");
        if (!string.IsNullOrWhiteSpace(fromCfg) && SupportedModelCatalog.IsKnownProviderId(fromCfg))
            return SupportedModelCatalog.NormalizeProviderId(fromCfg);
        return "";
    }

    private static double GetDouble(Dictionary<string, JsonElement> cfg, string key, double fallback) =>
        cfg.TryGetValue(key, out var el) && el.TryGetDouble(out var v) ? v : fallback;

    private static double GetDouble(Dictionary<string, object?> rates, string key, double fallback)
    {
        if (!rates.TryGetValue(key, out var v) || v is null) return fallback;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

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
        Dictionary<string, JsonElement> cfg)
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
            list.Add(BuildScreenplayDerivedScene(s, sn, defaultDur));
        }

        return list.OrderBy(x => x.SceneNumber).ToList();
    }

    private static BlueprintSceneClips BuildScreenplayDerivedScene(
        Dictionary<string, object?> s,
        int sn,
        double defaultDur)
    {
        var setting = ReadSceneSetting(s);
        var target = ReadSceneDurationTarget(s, defaultDur);
        var nClips = Math.Max(1, (int)Math.Ceiling(target / defaultDur));
        var clips = BuildSyntheticClips(nClips, target, defaultDur);
        var chars = ReadSceneCharactersOnScreen(s);
        return new BlueprintSceneClips
        {
            SceneNumber = ResolveDerivedSceneNumber(s, sn),
            Setting = setting ?? "",
            Clips = clips,
            CharactersOnScreen = chars,
        };
    }

    private static string ReadSceneSetting(Dictionary<string, object?> s)
    {
        string setting = "";
        if (s.TryGetValue("setting", out var setObj) && setObj is not null)
            setting = setObj.ToString() ?? "";
        else if (s.TryGetValue("heading", out var hObj) && hObj is not null)
            setting = hObj.ToString() ?? "";
        return setting;
    }

    private static double ReadSceneDurationTarget(Dictionary<string, object?> s, double defaultDur)
    {
        var target = ToPositiveDouble(
            s.TryGetValue("duration_target_seconds", out var d1) ? d1 : null,
            ToPositiveDouble(
                s.TryGetValue("estimated_duration_seconds", out var d2) ? d2 : null,
                24));
        return Math.Clamp(target, defaultDur, 600);
    }

    private static List<BlueprintClip> BuildSyntheticClips(int nClips, double target, double defaultDur)
    {
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
        return clips;
    }

    private static List<string> ReadSceneCharactersOnScreen(Dictionary<string, object?> s)
    {
        var chars = new List<string>();
        if (s.TryGetValue("characters_on_screen", out var cos) && cos is List<object?> cosList)
        {
            foreach (var x in cosList)
            {
                var name = x?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    chars.Add(name);
            }
        }
        return chars;
    }

    private static int ResolveDerivedSceneNumber(Dictionary<string, object?> s, int sn) =>
        s.TryGetValue(JsonKeys.SceneNumber, out var snObj) && snObj is not null
            && int.TryParse(snObj.ToString(), out var snParsed) ? snParsed : sn;

    private readonly record struct ScopeEstimate(double Usd, double RemainingUsd, bool Included);

    /// <summary>Character portraits: variants × image-model unit cost (catalog).</summary>
    private ScopeEstimate EstimateCharacterGeneration(
        string projectId,
        Dictionary<string, object?> rates,
        Dictionary<string, JsonElement> cfg)
    {
        var unit = GetDouble(rates, "image_output_quality", 0.05);
        var variants = 3;
        if (cfg.TryGetValue(Keys.CostEstimates, out var ce) && ce.ValueKind == JsonValueKind.Object &&
            ce.TryGetProperty("character_variants", out var cv) && cv.TryGetInt32(out var n) && n > 0)
            variants = Math.Clamp(n, 1, 6);

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
        var ov = ReadVoiceEstimateOverrides(cfg);
        var include = ov.Include;

        var chars = _projects.ListCharacters(projectId);
        var withVoice = chars.Where(CharacterHasVoiceSetup).ToList();
        if (withVoice.Count > 0)
            include = true;

        // Shot plan with dialogue ⇒ speak-batch / re-voice is in scope even without a sample yet.
        var dialogueCharCounts = scenes
            .SelectMany(s => s.Clips)
            .Where(c => c.DialogueCharCount > 0)
            .Select(c => c.DialogueCharCount)
            .ToList();
        var dialogueChars = dialogueCharCounts.Sum();
        var dialogueClips = dialogueCharCounts.Count;
        if (dialogueChars > 0)
            include = true;

        if (!include || (chars.Count == 0 && dialogueChars == 0))
            return new ScopeEstimate(0, 0, Included: false);

        var (voiceEntry, cloneEntry) = ResolveVoiceCatalogEntries(cfg);
        var perThousand = ov.TtsPerThousandOverride >= 0
            ? ov.TtsPerThousandOverride
            : voiceEntry?.CostPerThousandCharsUsd ?? 0.10;
        var cloneUsd = ov.CloneUsdOverride >= 0
            ? ov.CloneUsdOverride
            : cloneEntry?.CostPerCloneUsd ?? 0.0;

        double total = 0, remaining = 0;
        AddVoiceCloneEstimate(chars, withVoice, dialogueChars, cloneUsd, ref total, ref remaining);
        AddVoiceTtsEstimate(
            scenes, withVoice, chars, dialogueChars, perThousand, ov.TtsPerCharOverride, ref total, ref remaining);

        _ = rates;
        _ = dialogueClips;
        return new ScopeEstimate(Math.Round(total, 4), Math.Round(remaining, 4), Included: total > 0 || include);
    }

    private readonly record struct VoiceEstimateOverrides(
        bool Include,
        double CloneUsdOverride,
        double TtsPerCharOverride,
        double TtsPerThousandOverride);

    private static VoiceEstimateOverrides ReadVoiceEstimateOverrides(Dictionary<string, JsonElement> cfg)
    {
        var include = false;
        double cloneUsdOverride = -1;
        double ttsPerCharOverride = -1; // flat $ per speaking character (legacy knob)
        double ttsPerThousandOverride = -1;

        if (cfg.TryGetValue(Keys.CostEstimates, out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            TryReadIncludeVoice(ce, ref include);
            TryReadDoubleProp(ce, "voice_clone_usd", ref cloneUsdOverride);
            TryReadDoubleProp(ce, "voice_tts_per_character_usd", ref ttsPerCharOverride);
            TryReadDoubleProp(ce, "voice_tts_per_thousand_chars_usd", ref ttsPerThousandOverride);
        }

        return new VoiceEstimateOverrides(include, cloneUsdOverride, ttsPerCharOverride, ttsPerThousandOverride);
    }

    private static void TryReadIncludeVoice(JsonElement ce, ref bool include)
    {
        if (ce.TryGetProperty("include_voice", out var iv) &&
            iv.ValueKind is JsonValueKind.True or JsonValueKind.False)
            include = iv.GetBoolean();
    }

    private static void TryReadDoubleProp(JsonElement ce, string name, ref double target)
    {
        if (ce.TryGetProperty(name, out var el) && el.TryGetDouble(out var v))
            target = v;
    }

    private static bool CharacterHasVoiceSetup(CharacterSummary c) =>
        c.HasVoiceCloneSample ||
        !string.IsNullOrWhiteSpace(c.VoiceProfile) ||
        !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId);

    private static (SupportedModelEntry? Voice, SupportedModelEntry? Clone) ResolveVoiceCatalogEntries(
        Dictionary<string, JsonElement> cfg)
    {
        var voiceId = GetStr(cfg, "voice_model_name", "");
        var voiceEntry = SupportedModelCatalog.Find(voiceId, ModelCapability.Voice)
                         ?? FindEnabledSpeakVoiceModel(matchProviderId: false, providerId: null);

        // Prefer speak model (not clone step) for TTS $/1k chars.
        if (voiceEntry is { IsVoiceCloneStep: true })
        {
            voiceEntry = FindEnabledSpeakVoiceModel(matchProviderId: true, voiceEntry.ProviderId)
                ?? voiceEntry;
        }

        var cloneEntry = FindEnabledCloneVoiceModel(voiceEntry);
        return (voiceEntry, cloneEntry);
    }

    private static SupportedModelEntry? FindEnabledSpeakVoiceModel(bool matchProviderId, string? providerId) =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => IsEnabledSpeakVoice(m, matchProviderId, providerId));

    private static bool IsEnabledSpeakVoice(SupportedModelEntry m, bool matchProviderId, string? providerId) =>
        !m.IsVoiceCloneStep && m.Enabled &&
        (!matchProviderId || string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    private static SupportedModelEntry? FindEnabledCloneVoiceModel(SupportedModelEntry? voiceEntry) =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => IsEnabledCloneVoice(m, voiceEntry));

    private static bool IsEnabledCloneVoice(SupportedModelEntry m, SupportedModelEntry? voiceEntry) =>
        m.IsVoiceCloneStep && m.Enabled &&
        (voiceEntry is null ||
         string.Equals(m.ProviderId, voiceEntry.ProviderId, StringComparison.OrdinalIgnoreCase));

    private static bool IsNarratorCharacter(CharacterSummary c) =>
        string.Equals(c.Key, "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
        (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false);

    private static void AddVoiceCloneEstimate(
        IReadOnlyList<CharacterSummary> chars,
        List<CharacterSummary> withVoice,
        int dialogueChars,
        double cloneUsd,
        ref double total,
        ref double remaining)
    {
        // Clone: once per character that still needs a sample (or one narrator slot if none listed).
        var cloneTargets = withVoice.Count > 0
            ? withVoice
            : chars.Where(IsNarratorCharacter).ToList();
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
    }

    private static void AddVoiceTtsEstimate(
        List<BlueprintSceneClips> scenes,
        List<CharacterSummary> withVoice,
        IReadOnlyList<CharacterSummary> chars,
        int dialogueChars,
        double perThousand,
        double ttsPerCharOverride,
        ref double total,
        ref double remaining)
    {
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
        if (cfg.TryGetValue(Keys.CostEstimates, out var ce) && ce.ValueKind == JsonValueKind.Object &&
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
        var refn = CreateHistoryRefinement(priorVideoMultiplier, qaRetryOnFail, projectActual);

        await ApplyTimingFailRateAsync(projectId, refn, ct).ConfigureAwait(false);
        var apiStats = await LoadApiCostHistoryStatsAsync(projectId, refn, ct).ConfigureAwait(false);

        ApplyLearnedVideoMultiplier(refn, priorVideoMultiplier, qaRetryOnFail, qaMaxRetries);
        var timingWeight = refn.HistoryWeight;

        var bits = BuildRefinementHistoryBits(refn, projectActual);

        // H4/H5 — blend learned takes-per-clip (p50) into expected video multiplier.
        refn.ExpectedTakes = Math.Max(1.0, refn.AppliedVideoMultiplier);
        await ApplyTakesBlendAsync(projectId, refn, qaRetryOnFail, bits, ct).ConfigureAwait(false);

        ApplyRefinementNotes(refn, bits, timingWeight, qaRetryOnFail, priorVideoMultiplier);

        _historyApiStats = apiStats;
        return refn;
    }

    private static CostEstimateRefinement CreateHistoryRefinement(
        double priorVideoMultiplier,
        bool qaRetryOnFail,
        CostLedgerSummary projectActual) =>
        new()
        {
            PriorVideoMultiplier = priorVideoMultiplier,
            AppliedVideoMultiplier = qaRetryOnFail ? priorVideoMultiplier : 1.0,
            ProjectLedgerEvents = projectActual.EventCount,
        };

    private async Task ApplyTimingFailRateAsync(
        string projectId,
        CostEstimateRefinement refn,
        CancellationToken ct)
    {
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
    }

    private async Task<ApiCostHistoryStats?> LoadApiCostHistoryStatsAsync(
        string projectId,
        CostEstimateRefinement refn,
        CancellationToken ct)
    {
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
        return apiStats;
    }

    private static void ApplyLearnedVideoMultiplier(
        CostEstimateRefinement refn,
        double priorVideoMultiplier,
        bool qaRetryOnFail,
        int qaMaxRetries)
    {
        var learnedMult = priorVideoMultiplier;
        var failRate = refn.LearnedFailRate;
        if (qaRetryOnFail && failRate is double fr)
        {
            learnedMult = 1.0 + Math.Clamp(fr, 0, 0.9) * Math.Max(1, qaMaxRetries);
            learnedMult = Math.Clamp(learnedMult, 1.0, 2.5);
        }

        var timingSamples = refn.TimingSamples;
        var w = timingSamples <= 0 ? 0.0 : Math.Min(1.0, timingSamples / 30.0);
        refn.HistoryWeight = w;
        if (qaRetryOnFail)
            refn.AppliedVideoMultiplier = Math.Round(priorVideoMultiplier * (1 - w) + learnedMult * w, 3);

        refn.UsedHistory = timingSamples >= MinTimingSamples || refn.VideoApiSamples >= MinApiSamples
            || refn.ProjectLedgerEvents >= MinApiSamples;
    }

    private static List<string> BuildRefinementHistoryBits(
        CostEstimateRefinement refn,
        CostLedgerSummary projectActual)
    {
        var bits = new List<string>();
        if (refn.TimingSamples >= MinTimingSamples && refn.LearnedFailRate is double fr2)
            bits.Add($"timing QA fail ~{fr2:P0} (n={refn.TimingSamples})");
        if (refn.VideoApiSamples > 0)
            bits.Add($"{refn.VideoApiSamples} video API samples");
        if (refn.ReviewApiSamples > 0)
            bits.Add($"{refn.ReviewApiSamples} review API samples");
        if (projectActual.EventCount > 0)
            bits.Add($"{projectActual.EventCount} project ledger events");
        return bits;
    }

    private async Task ApplyTakesBlendAsync(
        string projectId,
        CostEstimateRefinement refn,
        bool qaRetryOnFail,
        List<string> bits,
        CancellationToken ct)
    {
        try
        {
            if (_userDb is not null)
            {
                var globalTakes = await _userDb.GetTakesTelemetryStatsAsync(projectId: null, ct)
                    .ConfigureAwait(false);
                var projectTakes = await _userDb.GetTakesTelemetryStatsAsync(projectId, ct)
                    .ConfigureAwait(false);
                ApplyTakesBlendFromStats(refn, qaRetryOnFail, bits, globalTakes, projectTakes);
            }
        }
        catch
        {
            // H9 fail-open — keep prior expected takes
        }
    }

    private static void ApplyTakesBlendFromStats(
        CostEstimateRefinement refn,
        bool qaRetryOnFail,
        List<string> bits,
        TakesTelemetryStats globalTakes,
        TakesTelemetryStats projectTakes)
    {
        // Prefer project when it has enough samples; else global contribute=1 pool.
        var takesSrc = PickTakesBlendSource(projectTakes, globalTakes);
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

    private static void ApplyRefinementNotes(
        CostEstimateRefinement refn,
        List<string> bits,
        double w,
        bool qaRetryOnFail,
        double priorVideoMultiplier)
    {
        if (bits.Count == 0)
            bits.Add("no history yet — using catalog priors");
        else if (w > 0 && qaRetryOnFail)
            bits.Add($"video mult {priorVideoMultiplier:0.##}→{refn.AppliedVideoMultiplier:0.##} (weight {w:0.00})");
        refn.Notes = "History: " + string.Join("; ", bits) + ".";
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
        if (cfg.TryGetValue(Keys.CostEstimates, out var ce) && ce.ValueKind == JsonValueKind.Object)
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
        if (estimateBasis == Keys.ShotPlan)
        {
            // Both passes done for planning purposes
            total = importUsd + shotPlanUsd;
            remaining = 0;
        }
        else if (estimateBasis == Keys.Screenplay)
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
        if (c.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("dialogue", out var d))
            dialogue = d.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) && c.TryGetProperty("dialogue", out var rootD))
            dialogue = rootD.GetString() ?? "";
        dialogue = dialogue.Trim();
        return dialogue.Length;
    }

    private static string? ReadClipSpeaker(JsonElement c)
    {
        if (c.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
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
