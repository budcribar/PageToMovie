using System.Text;
using System.Text.Json;
using System.Diagnostics;
using PageToMovie.Engine.Abstractions;

namespace ScreenplayBenchmark;

/// <summary>
/// Requests narrow prompt-improvement proposals from models that have just been benchmarked.
/// This is deliberately advisory: it never edits the production prompt or runs a benchmark.
/// </summary>
internal static class PromptImprovementReview
{
    private const string BookSlug = "nick_and_me";

    public static async Task<int> RunAsync(
        string workspaceRoot,
        IChatClient chat,
        IReadOnlyCollection<string> modelIds,
        CancellationToken cancellationToken = default)
    {
        if (!chat.IsConfigured)
        {
            await Console.Error.WriteLineAsync("No configured chat provider is available for prompt review.");
            return 1;
        }

        var promptPath = Path.Combine(workspaceRoot, "prompts", "book_to_fountain.txt");
        if (!File.Exists(promptPath))
            throw new FileNotFoundException("Committed screenplay prompt was not found.", promptPath);

        var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);
        var historyPath = Path.Combine(workspaceRoot, "evals", "benchmark_history.json");
        var history = BenchmarkHistoryStore.LoadHistory(historyPath);
        var revision = await GetHeadPromptRevisionAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        var outputDir = Path.Combine(workspaceRoot, "evals", "prompt_reviews", $"prompt_review_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(outputDir);

        foreach (var modelId in modelIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var run = history.Runs
                .Where(r => !r.IsMockRun
                            && r.BookSlug.Equals(BookSlug, StringComparison.OrdinalIgnoreCase)
                            && MatchesRevision(r.PromptVersion, revision)
                            && r.ModelScores.Any(s => s.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) && !s.IsGenerationFallback))
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault();

            if (run is null)
            {
                Console.WriteLine($"Skipping {modelId}: no live {BookSlug} run for prompt {revision}.");
                continue;
            }

            var screenplayPath = Path.Combine(workspaceRoot, "evals", "cache", BookSlug, $"{SanitizeFileName(modelId)}_{run.PromptVersion}.fountain");
            if (!File.Exists(screenplayPath))
            {
                Console.WriteLine($"Skipping {modelId}: screenplay cache is missing ({screenplayPath}).");
                continue;
            }

            var screenplay = await File.ReadAllTextAsync(screenplayPath, cancellationToken);
            var score = run.ModelScores.First(s => s.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
            var evaluation = BuildEvaluationSummary(run, score);
            var request = BuildRequest(prompt, screenplay, evaluation);

            Console.Write($"  [Prompt review] {modelId}... ");
            var response = await chat.CompleteAsync(
                systemPrompt: "You are a screenplay-adaptation prompt engineer. Diagnose the supplied evidence rigorously. Recommend only minimal, testable prompt changes; do not rewrite the screenplay or claim that a change is proven before it is benchmarked.",
                userPrompt: request,
                model: modelId,
                temperature: 0.2,
                ct: cancellationToken,
                mode: ChatCallModes.PromptImprovementReview);

            var report = $"# {modelId} prompt-improvement recommendation\n\n" +
                         $"- Prompt commit: `{revision}`\n" +
                         $"- Source benchmark: {run.Timestamp} / {BookSlug}\n\n" +
                         "## Evidence supplied\n\n" + evaluation +
                         "\n\n## Recommendation\n\n" + response.Trim() + "\n";
            var outputPath = Path.Combine(outputDir, $"{SanitizeFileName(modelId)}.md");
            await File.WriteAllTextAsync(outputPath, report, cancellationToken);
            Console.WriteLine($"saved {outputPath}");
        }

        Console.WriteLine($"Prompt-review recommendations saved to: {outputDir}");
        return 0;
    }

    private static string BuildRequest(string prompt, string screenplay, string evaluation) => $"""
        We are improving a screenplay-adaptation product. Review the evidence below for your own
        generated screenplay. The objective is to improve the next prompt version while preserving
        successful compliance behavior.

        Return exactly these sections:
        1. Root causes — at most three, grounded in the evaluation and draft.
        2. Minimal prompt patch — exact additions/replacements, no more than 12 lines total.
        3. Expected trade-offs — which rubric dimensions may improve or regress.
        4. Benchmark hypothesis — a falsifiable expected change versus this result.

        Do not recommend changing the scorer, inventing source facts, adding title-specific rules,
        or adding broad redundant checklists.

        ## Current committed generation prompt
        {prompt}

        ## Your generated screenplay
        {screenplay}

        ## Structured benchmark evaluation
        {evaluation}
        """;

    private static string BuildEvaluationSummary(HistoricalBenchmarkRun run, ModelScoreSummary score)
    {
        var syntax = score.SyntaxAudit;
        var builder = new StringBuilder();
        builder.AppendLine($"Composite: {score.CompositeScore:F1}; syntax: {syntax.OverallSyntaxScore:F1}; LLM consensus: {score.AvgOverallQualitative * 10:F1}.");
        builder.AppendLine($"Quality dimensions (0-10): fidelity {score.AvgAdaptationFidelity:F1}, character continuity {score.AvgCharacterDisambiguation:F1}, directibility {score.AvgAiVideoDirectibility:F1}, pacing {score.AvgDramaticPacing:F1}, dialogue {score.AvgDialogueAuthenticity:F1}, sound/music {score.AvgSoundDesignMusic:F1}.");
        builder.AppendLine($"Deterministic audit: {syntax.TotalSceneHeadings} scenes; {syntax.TotalDialogueBlocks} dialogue blocks; max dialogue {syntax.MaxWordsInSingleDialogue} words.");

        if (syntax.DiagnosticWarnings.Count > 0)
            builder.AppendLine("Diagnostics: " + string.Join(" | ", syntax.DiagnosticWarnings));
        if (score.DisqualifyingFlags.Count > 0)
            builder.AppendLine("Judge flags: " + string.Join(" | ", score.DisqualifyingFlags));
        if (run.SelfBiasNotes.Count > 0)
            builder.AppendLine("Self-bias notes: " + string.Join(" | ", run.SelfBiasNotes));

        return builder.ToString();
    }

    private static async Task<string> GetHeadPromptRevisionAsync(string workspaceRoot, CancellationToken ct = default)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "log -1 --format=%h -- prompts/book_to_fountain.txt",
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
            throw new InvalidOperationException("Could not start Git to determine the prompt revision.");

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Could not determine the committed prompt revision.");
        return output.Trim();
    }

    private static bool MatchesRevision(string recordedRevision, string headRevision) =>
        recordedRevision.StartsWith(headRevision, StringComparison.OrdinalIgnoreCase)
        || headRevision.StartsWith(recordedRevision, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string value) => PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName(value);
}
