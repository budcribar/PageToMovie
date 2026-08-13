using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScreenplayBenchmark;

public sealed class HistoricalBenchmarkRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
    public string BookSlug { get; set; } = "default";
    public string BookTitle { get; set; } = "";
    public string BookPath { get; set; } = "";
    public bool IsMockRun { get; set; }

    /// <summary>
    /// The --reasoning-effort value this run was invoked with ("low"/"medium"/"high"/"max"),
    /// or "" for the provider's default (no reasoningEffort passed). Applies uniformly to every
    /// candidate and judge in the run — it's a single CLI flag, not per-model. Runs at different
    /// effort levels are not directly comparable, so this must stay visible in history/dashboard
    /// rather than silently blending a "default effort" score with a "max effort" one.
    /// </summary>
    public string ReasoningEffort { get; set; } = "";

    /// <summary>Sampling temperature used for screenplay generation.</summary>
    public double SamplingTemperature { get; set; } = 0.2;

    /// <summary>
    /// Sampling temperature used for peer judging — deliberately independent of
    /// <see cref="SamplingTemperature"/>. Judge repeatability (temp 0 recommended) is a separate
    /// question from what temperature best generates a screenplay; a v5 comparison found generation
    /// results at temp 0 mixed (helped one model, hurt another on the same book), so the two must
    /// never be forced to the same value.
    /// </summary>
    public double JudgeTemperature { get; set; } = 0.0;

    /// <summary>
    /// Short Git revision of the commit that last changed prompts/book_to_fountain.txt. Benchmark
    /// startup rejects an uncommitted prompt, so every newly-recorded run is reproducible from the
    /// repository history rather than from an untracked content checksum.
    /// </summary>
    public string PromptVersion { get; set; } = "";

    /// <summary>
    /// Short stable id of the <c>PageToMovie.Adaptation</c> module surface (assembly informational
    /// version + embedded Stage‑1 prompt content). See <c>PageToMovie.Adaptation.AdaptationVersion</c>.
    /// Paired with <see cref="PromptVersion"/> so converter/heuristic changes bust cache/history
    /// buckets even when the prompt git revision is unchanged.
    /// </summary>
    public string AdaptationVersion { get; set; } = "";

    /// <summary>True when a legacy untagged run was mapped to the prompt revision inferred from
    /// its timestamp, rather than recorded directly by the benchmark runner.</summary>
    public bool PromptVersionInferred { get; set; }

    public List<ModelScoreSummary> ModelScores { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> JudgeMatrix { get; set; } = new();
    public List<string> SelfBiasNotes { get; set; } = new();
}

public sealed class HistoricalStoreContainer
{
    public List<HistoricalBenchmarkRun> Runs { get; set; } = new();
}

public sealed class CompositeModelSummary
{
    public string ModelId { get; set; } = "";
    public double MultiBookCompositeScore { get; set; }
    public double AvgSyntaxScore { get; set; }
    public double AvgFormatCompliance { get; set; }
    public double AvgSceneBudget { get; set; }
    public double AvgDialoguePacing { get; set; }
    public double AvgCharDisambiguationSyntax { get; set; }
    public double AvgMusicSpec { get; set; }
    public double AvgQualitativeScore { get; set; }
    public double AvgFidelity { get; set; }
    public double AvgCharSplit { get; set; }
    public double AvgVideoDirect { get; set; }
    public double AvgPacing { get; set; }
    public double AvgDialogue { get; set; }
    public double AvgMusic { get; set; }
    public int TotalBooksEvaluated { get; set; }
    public List<string> EvaluatedBookTitles { get; set; } = new();
    public int FirstPlaceWins { get; set; }
}

public sealed class JudgeQualitySummary
{
    public string ModelId { get; set; } = "";

    /// <summary>Books where this model appeared as a judge (regardless of outcome).</summary>
    public int BooksJudged { get; set; }

    /// <summary>Books where it actually completed judging (not fully mock/failed).</summary>
    public int BooksReliable { get; set; }
    public double ReliabilityRate { get; set; }

    /// <summary>Books where both a self-score and at least one peer score were available.</summary>
    public int SelfBiasSampleCount { get; set; }

    /// <summary>Average (selfScore - peerAverage) across sampled books — positive means the judge
    /// rates its own screenplay above what other judges give it; near zero across several books is
    /// the best signal of an objective judge (a single large swing either direction is noisier).</summary>
    public double AvgNetSelfBias { get; set; }

    /// <summary>Average |selfScore - peerAverage| — how far off self-scoring runs on average,
    /// regardless of direction. A judge can have a low net bias but still be noisy/inconsistent.</summary>
    public double AvgAbsSelfBias { get; set; }
}

public static class BenchmarkHistoryStore
{
    public static HistoricalStoreContainer LoadHistory(string historyFilePath)
    {
        if (!File.Exists(historyFilePath))
            return new HistoricalStoreContainer();

        try
        {
            var json = File.ReadAllText(historyFilePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<HistoricalStoreContainer>(json, opts) ?? new HistoricalStoreContainer();
        }
        catch
        {
            return new HistoricalStoreContainer();
        }
    }

    public static void SaveHistory(HistoricalStoreContainer container, string historyFilePath)
    {
        var dir = Path.GetDirectoryName(historyFilePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(historyFilePath, JsonSerializer.Serialize(container, opts));
    }

    public static void AppendRun(HistoricalBenchmarkRun newRun, string historyFilePath)
    {
        var container = LoadHistory(historyFilePath);
        container.Runs.Add(newRun);
        SaveHistory(container, historyFilePath);
    }

    public static bool IsLiveRun(HistoricalBenchmarkRun run)
    {
        if (run.IsMockRun) return false;
        if (run.ModelScores == null || run.ModelScores.Count == 0) return false;

        // Check if all composite scores are identical mock ties or all negative
        // (fallback-drafted models are excluded — their "score" reflects a shared, model-agnostic
        // heuristic draft, not that model's real generation, so it can't anchor liveness either)
        var validScores = run.ModelScores.Where(m => !m.IsGenerationFallback).Select(m => m.CompositeScore).Where(s => s >= 0).ToList();
        if (validScores.Count == 0 || (validScores.Distinct().Count() <= 1 && validScores.Count > 1))
            return false;

        // Check if judge matrix has at least one real non-mock rating (> 0)
        if (run.JudgeMatrix == null || run.JudgeMatrix.Count == 0) return false;
        var hasRealJudgeRating = run.JudgeMatrix.Values.Any(dict => dict.Values.Any(v => v > 0));
        return hasRealJudgeRating;
    }

    public static List<CompositeModelSummary> ComputeGlobalCompositeLeaderboard(HistoricalStoreContainer container)
    {
        var liveRuns = container.Runs.Where(IsLiveRun).ToList();
        if (liveRuns.Count == 0)
            return new List<CompositeModelSummary>();

        // Group by model across live runs only
        var allModelIds = liveRuns.SelectMany(r => r.ModelScores.Select(m => m.ModelId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<CompositeModelSummary>();

        foreach (var modelId in allModelIds)
        {
            var modelRuns = liveRuns
                .Where(r => r.ModelScores.Any(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (modelRuns.Count == 0) continue;

            var modelScoresList = modelRuns
                .Select(r => r.ModelScores.First(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
                .Where(s => s.CompositeScore >= 0 && !s.IsGenerationFallback)
                .ToList();

            if (modelScoresList.Count == 0) continue;

            result.Add(BuildCompositeSummary(modelId, modelRuns, modelScoresList, liveRuns));
        }

        return result.OrderByDescending(c => c.MultiBookCompositeScore).ToList();
    }

    private static CompositeModelSummary BuildCompositeSummary(
        string modelId,
        List<HistoricalBenchmarkRun> modelRuns,
        List<ModelScoreSummary> modelScoresList,
        List<HistoricalBenchmarkRun> liveRuns)
    {
        return new CompositeModelSummary
        {
            ModelId = modelId,
            MultiBookCompositeScore = Math.Round(modelScoresList.Average(s => s.CompositeScore), 1),
            AvgSyntaxScore = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.OverallSyntaxScore), 1),
            AvgFormatCompliance = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.FormatComplianceScore), 1),
            AvgSceneBudget = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.SceneBudgetScore), 1),
            AvgDialoguePacing = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.DialoguePacingScore), 1),
            AvgCharDisambiguationSyntax = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.CharacterDisambiguationScore), 1),
            AvgMusicSpec = Math.Round(modelScoresList.Average(s => s.SyntaxAudit.MusicSpecScore), 1),
            AvgQualitativeScore = Math.Round(modelScoresList.Average(s => s.AvgOverallQualitative * 10.0), 1),
            AvgFidelity = Math.Round(modelScoresList.Average(s => s.AvgAdaptationFidelity), 1),
            AvgCharSplit = Math.Round(modelScoresList.Average(s => s.AvgCharacterDisambiguation), 1),
            AvgVideoDirect = Math.Round(modelScoresList.Average(s => s.AvgAiVideoDirectibility), 1),
            AvgPacing = Math.Round(modelScoresList.Average(s => s.AvgDramaticPacing), 1),
            AvgDialogue = Math.Round(modelScoresList.Average(s => s.AvgDialogueAuthenticity), 1),
            AvgMusic = Math.Round(modelScoresList.Average(s => s.AvgSoundDesignMusic), 1),
            TotalBooksEvaluated = modelRuns.Select(r => r.BookSlug).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            EvaluatedBookTitles = modelRuns
                .Select(r => !string.IsNullOrWhiteSpace(r.BookTitle) ? r.BookTitle : r.BookSlug)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            FirstPlaceWins = CountFirstPlaceWins(modelId, liveRuns),
        };
    }

    private static int CountFirstPlaceWins(string modelId, List<HistoricalBenchmarkRun> liveRuns)
    {
        var wins = 0;
        foreach (var run in liveRuns)
        {
            if (RunAwardsFirstPlace(run, modelId))
                wins++;
        }
        return wins;
    }

    private static bool RunAwardsFirstPlace(HistoricalBenchmarkRun run, string modelId)
    {
        var validScores = run.ModelScores.Where(m => m.CompositeScore >= 0 && !m.IsGenerationFallback).OrderByDescending(m => m.CompositeScore).ToList();
        if (validScores.Count == 0) return false;
        var topScore = validScores[0].CompositeScore;
        var topTies = validScores.Where(m => Math.Abs(m.CompositeScore - topScore) < 0.01).ToList();
        // Award a win only if topScore > 0 and it's not a universal tie across all candidates
        return topScore > 0
            && topTies.Count < validScores.Count
            && topTies.Any(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ranks models by how trustworthy they are AS JUDGES (not as candidates): did they reliably
    /// complete judging, and were they objective when judging their own screenplay. Recomputed
    /// directly from each run's JudgeMatrix rather than parsing the free-text SelfBiasNotes, so it
    /// stays robust to that text's wording changing over time.
    /// </summary>
    public static List<JudgeQualitySummary> ComputeJudgeLeaderboard(HistoricalStoreContainer container)
    {
        var liveRuns = container.Runs.Where(IsLiveRun).ToList();
        if (liveRuns.Count == 0)
            return new List<JudgeQualitySummary>();

        var allJudgeIds = liveRuns
            .SelectMany(r => r.JudgeMatrix.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<JudgeQualitySummary>();
        foreach (var judgeId in allJudgeIds)
        {
            var summary = SummarizeJudge(judgeId, liveRuns);
            if (summary is not null)
                result.Add(summary);
        }

        // Most reliable first, then least (absolute) self-bias among equally-reliable judges.
        return result
            .OrderByDescending(j => j.ReliabilityRate)
            .ThenBy(j => j.AvgAbsSelfBias)
            .ToList();
    }

    private static JudgeQualitySummary? SummarizeJudge(string judgeId, List<HistoricalBenchmarkRun> liveRuns)
    {
        int booksJudged = 0, booksReliable = 0;
        var netBiasSamples = new List<double>();

        foreach (var run in liveRuns)
        {
            if (!TryScoreJudgeRun(judgeId, run, out var reliable, out var netBias))
                continue;
            booksJudged++;
            if (!reliable) continue;
            booksReliable++;
            if (netBias is { } sample)
                netBiasSamples.Add(sample);
        }

        if (booksJudged == 0) return null;

        return new JudgeQualitySummary
        {
            ModelId = judgeId,
            BooksJudged = booksJudged,
            BooksReliable = booksReliable,
            ReliabilityRate = Math.Round((double)booksReliable / booksJudged, 2),
            SelfBiasSampleCount = netBiasSamples.Count,
            AvgNetSelfBias = netBiasSamples.Count > 0 ? Math.Round(netBiasSamples.Average(), 2) : 0.0,
            AvgAbsSelfBias = netBiasSamples.Count > 0 ? Math.Round(netBiasSamples.Select(Math.Abs).Average(), 2) : 0.0,
        };
    }

    private static bool TryScoreJudgeRun(
        string judgeId, HistoricalBenchmarkRun run, out bool reliable, out double? netBias)
    {
        reliable = false;
        netBias = null;
        if (!run.JudgeMatrix.TryGetValue(judgeId, out var ownRow)) return false;

        // A fully mock/failed judge attempt has every entry in its own row at -1.0.
        if (!ownRow.Values.Any(v => v >= 0.0))
            return true;

        reliable = true;
        if (!ownRow.TryGetValue(judgeId, out var selfScore) || selfScore < 0.0)
            return true;

        var peerScores = run.JudgeMatrix
            .Where(kv => !string.Equals(kv.Key, judgeId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value.TryGetValue(judgeId, out var s) ? s : (double?)null)
            .Where(s => s.HasValue && s.Value >= 0.0)
            .Select(s => s!.Value)
            .ToList();
        if (peerScores.Count == 0) return true;

        netBias = selfScore - peerScores.Average();
        return true;
    }
}
