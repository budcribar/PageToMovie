using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Validation;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using EngineConversionResult = PageToMovie.Engine.ProjectAdaptationConversionResult;
using VisionMetaStatus = PageToMovie.Engine.ProjectVisionMetaStatus;
using CastPackageCrossCheck = PageToMovie.Adaptation.Validation.CastPackageCrossCheck;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;

namespace ScreenplayBenchmark;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();

        TryLoadDotEnv();
        PrintBanner();
        return await RunParsedAsync(CliOptions.Parse(args)).ConfigureAwait(false);
    }

    private static async Task RunSingleBookBenchmarkAsync(
        string bookPath,
        string bookSlug,
        string outDir,
        List<string>? requestedModels,
        List<string>? requestedJudges,
        bool dryRun,
        bool retryFailed,
        string historyFilePath,
        IChatClient chat,
        string workspaceRoot,
        string promptRevision,
        string adaptationVersion,
        string? reasoningEffort = null,
        double samplingTemperature = 0.2,
        bool bypassCache = false,
        double judgeTemperature = 0.0,
        bool useSharedCache = true,
        string sharedCacheUser = "benchmark",
        string sharedCacheVisibility = "Forkable",
        int? targetRuntimeMinutesOverride = null)
    {
        var screenplaysDir = Path.Combine(outDir, bookSlug, "screenplays");
        Directory.CreateDirectory(screenplaysDir);

        Console.WriteLine($"📖 Source Book: {bookPath} (Slug: {bookSlug})");
        Console.WriteLine($"🧪 Mode: {(dryRun ? "DRY-RUN (Mock Data)" : chat.IsConfigured ? "LIVE API CALLS" : "NO API KEY (Mock Data)")}");

        var availableChatModels = SupportedModelCatalog.ForCapability(ModelCapability.Chat, enabledOnly: true);
        var candidateModels = availableChatModels.Select(m => m.Id).ToList();

        if (requestedModels is { Count: > 0 })
        {
            candidateModels = candidateModels.Where(m => requestedModels.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (candidateModels.Count == 0)
        {
            Console.WriteLine("❌ Error: No enabled Chat models found matching criteria.");
            return;
        }

        // Judges default to the candidate set itself (every candidate also judges its peers), but
        // --judges lets the panel be a smaller, independently-vetted set (e.g. the top-2 most
        // reliable/least self-biased judges per BenchmarkHistoryStore.ComputeJudgeLeaderboard)
        // so a new/unproven candidate model doesn't have to also serve as a judge of its peers.
        var judgeModels = requestedJudges is { Count: > 0 }
            ? availableChatModels.Select(m => m.Id).Where(m => requestedJudges.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList()
            : candidateModels;

        if (judgeModels.Count == 0)
        {
            Console.WriteLine("⚠️  No enabled Chat models matched --judges; falling back to the candidate set as judges.");
            judgeModels = candidateModels;
        }

        Console.WriteLine($"🤖 Candidate Models ({candidateModels.Count}): {string.Join(", ", candidateModels)}");
        Console.WriteLine($"⚖️  Judge Models ({judgeModels.Count}): {string.Join(", ", judgeModels)}");
        var bookText = await File.ReadAllTextAsync(bookPath);
        // Same algorithm as production Stage 1 (ScreenplayService / Stage1Service).
        var generationRuntimeMinutes = ResolveTargetRuntimeMinutes(bookText, targetRuntimeMinutesOverride);
        Console.WriteLine(
            $"⏱️  Target runtime {generationRuntimeMinutes} min " +
            DescribeRuntimeSource(bookText, targetRuntimeMinutesOverride));
        BookTextRegistryService? sharedCache = null;
        BookTextIdentity? sharedBook = null;
        string? sharedPromptHash = null;
        if (useSharedCache && !bypassCache && !dryRun)
        {
            sharedCache = new BookTextRegistryService(Microsoft.Extensions.Options.Options.Create(
                new PageToMovieOptions { WorkspaceRoot = workspaceRoot }));
            sharedBook = await sharedCache.RegisterAsync(
                bookText, sharedCacheUser, $"benchmark:{bookSlug}", sharedCacheVisibility);
            var sharedPrompt = await AdaptationService.BuildSystemPromptAsync(generationRuntimeMinutes);
            sharedPromptHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sharedPrompt))).ToLowerInvariant();
        }
        var generatedScreenplays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var generatedVisionMeta = new Dictionary<string, ProjectVisionMeta.Document?>(StringComparer.OrdinalIgnoreCase);
        var deterministicResults = new Dictionary<string, DeterministicSyntaxResult>(StringComparer.OrdinalIgnoreCase);
        var castPackageResults = new Dictionary<string, CastPackageCrossCheck.Report?>(StringComparer.OrdinalIgnoreCase);

        // Canonical output of the non-AI, book-text-only fallback for THIS book. Every model that
        // hits BookToFountainConverter's internal quality-gate fallback produces this exact text —
        // used below to detect (and refuse to trust) both live fallbacks and previously-poisoned
        // disk cache entries, so a real generation failure never gets silently graded as a model's
        // actual output. See ModelScoreSummary.IsGenerationFallback.
        var canonicalFallbackText = AdaptationService.ConvertHeuristic(
            Path.GetFileNameWithoutExtension(bookPath), AdaptationService.NormalizeBookText(bookText), "Author");
        var generationFallbacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Effort suffix keeps a max-effort run from silently reusing (or clobbering) a cached
        // screenplay/judge verdict generated at default effort, and vice versa — they're not
        // interchangeable and must never be compared or averaged as if they were.
        var effortSuffix = string.IsNullOrWhiteSpace(reasoningEffort) ? "" : $"_{SanitizeFileName(reasoningEffort)}";
        var temperatureKey = samplingTemperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
        // Judge temperature is deliberately decoupled from generation temperature — a repeatable
        // (temp 0) judge is desirable even while experimenting with generation temperature, so the
        // judge cache must not collide with or be invalidated by a generation-only temperature change.
        var judgeTemperatureKey = judgeTemperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');

        // Phase 1 & 2: Generation & C# Audits
        foreach (var modelId in candidateModels)
        {
            await GenerateOneCandidateAsync(
                modelId, bookPath, bookSlug, bookText, screenplaysDir, workspaceRoot,
                promptRevision, adaptationVersion, effortSuffix, temperatureKey,
                generationRuntimeMinutes, reasoningEffort, samplingTemperature,
                bypassCache, dryRun, chat, canonicalFallbackText,
                sharedCache, sharedBook, sharedPromptHash, sharedCacheUser, sharedCacheVisibility,
                generatedScreenplays, generatedVisionMeta, deterministicResults,
                castPackageResults, generationFallbacks).ConfigureAwait(false);
        }

        // Phase 3 & 4: Blind Cross-Evaluation
        var judgeEvaluations = new Dictionary<string, JudgeEvaluationPayload>(StringComparer.OrdinalIgnoreCase);
        var random = new Random(42);

        // Same shared prompt every candidate was generated from (workspaceRoot is ignored by
        // PromptFiles.ReadAsync — it always resolves PAGETOMOVIE_PROMPTS_DIR or the embedded
        // prompts/book_to_fountain.txt, so this is the exact text every model above just ran
        // under, not an approximation). Judges need to see this to suggest a REAL prompt fix
        // instead of guessing blind at what the prompt might already say.
        var generationSystemPrompt = await AdaptationService.BuildSystemPromptAsync(generationRuntimeMinutes);

        // Fallback-poisoned screenplays are already excluded from scoring (IsGenerationFallback) —
        // don't also make every judge read them. They're near-duplicates of the raw book text, so
        // including them inflates the judge prompt by a large multiple for no signal (this is what
        // pushed gpt-4o/gpt-4o-mini/o3-mini's judge calls for Nick and Me over their TPM limits:
        // 3 of 7 "candidates" were ~184K-char copies of the same fallback draft).
        var realCandidates = candidateModels.Where(m => !generationFallbacks.ContainsKey(m)).ToList();

        // Hashed over the real candidates only — a fallback draft's error text can vary run to run
        // (e.g. a rate-limit message's exact numbers) even though judges never see it, and that
        // must not spuriously invalidate an otherwise-still-valid judge cache.
        var screenplaysHash = ComputeScreenplaysHash(
            realCandidates.ToDictionary(
                m => m,
                m => BuildJudgeCandidatePackage(generatedScreenplays[m], generatedVisionMeta[m]),
                StringComparer.OrdinalIgnoreCase));

        foreach (var judgeModelId in judgeModels)
        {
            await EvaluateOneJudgeAsync(
                judgeModelId, realCandidates, generatedScreenplays, generatedVisionMeta,
                bookText, generationSystemPrompt, bookSlug, workspaceRoot, promptRevision,
                adaptationVersion, effortSuffix, temperatureKey, judgeTemperatureKey,
                bypassCache, retryFailed, dryRun, chat, judgeTemperature, reasoningEffort,
                screenplaysHash, random, judgeEvaluations).ConfigureAwait(false);
        }

        // Phase 5: Aggregation & History Persistence
        var runData = AggregateBenchmarkData(bookPath, candidateModels, generatedScreenplays, deterministicResults, judgeEvaluations, generationFallbacks, castPackageResults);
        runData.TargetRuntimeMinutes = generationRuntimeMinutes;
        runData.TargetRuntimeSource = targetRuntimeMinutesOverride is > 0
            ? "cli_override"
            : "book_text_analyzer";
        var isMockRun = dryRun || judgeEvaluations.Values.All(v => v.IsMock);
        runData.IsMockRun = isMockRun;

        var historyRun = new HistoricalBenchmarkRun
        {
            BookSlug = bookSlug,
            BookTitle = Path.GetFileNameWithoutExtension(bookPath),
            BookPath = bookPath,
            IsMockRun = isMockRun,
            ReasoningEffort = reasoningEffort ?? "",
            SamplingTemperature = samplingTemperature,
            JudgeTemperature = judgeTemperature,
            PromptVersion = promptRevision,
            AdaptationVersion = adaptationVersion,
            ModelScores = runData.Leaderboard,
            JudgeMatrix = runData.JudgeMatrix,
            SelfBiasNotes = runData.SelfBiasNotes,
        };

        BenchmarkHistoryStore.AppendRun(historyRun, historyFilePath);

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var jsonFile = Path.Combine(outDir, bookSlug, "run_data.json");
        await File.WriteAllTextAsync(jsonFile, JsonSerializer.Serialize(runData, jsonOpts));

        var reportMarkdown = BenchmarkReportGenerator.GenerateMarkdownReport(runData);
        var reportFile = Path.Combine(outDir, bookSlug, "benchmark_report.md");
        await File.WriteAllTextAsync(reportFile, reportMarkdown);

        Console.WriteLine($"✅ Benchmark for '{bookSlug}' completed!");
        Console.WriteLine($"   📄 Report: {reportFile}");
    }

    private static void PrintHistoricalLeaderboard(HistoricalStoreContainer historyStore)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" 🏆 ALL-TIME MULTI-BOOK COMPOSITE MODEL LEADERBOARD ");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();

        var globalLeaderboard = BenchmarkHistoryStore.ComputeGlobalCompositeLeaderboard(historyStore);
        if (globalLeaderboard.Count == 0)
        {
            Console.WriteLine("No benchmark runs recorded in history yet.");
            return;
        }

        Console.WriteLine(string.Format("{0,-6} | {1,-20} | {2,-15} | {3,-12} | {4,-12} | {5,-10}", "Rank", "Model ID", "Multi-Book Score", "C# Syntax", "LLM Peer", "Wins"));
        Console.WriteLine(new string('-', 85));

        for (int i = 0; i < globalLeaderboard.Count; i++)
        {
            var m = globalLeaderboard[i];
            var rank = i switch { 0 => "🥇 1", 1 => "🥈 2", 2 => "🥉 3", _ => $"   {i + 1}" };
            Console.WriteLine(string.Format("{0,-6} | {1,-20} | {2,-15:F1} | {3,-12:F1}% | {4,-12:F1}% | {5,-10}", rank, m.ModelId, m.MultiBookCompositeScore, m.AvgSyntaxScore, m.AvgQualitativeScore, m.FirstPlaceWins));
        }
        Console.WriteLine();
    }

    private static void PrintJudgeLeaderboard(HistoricalStoreContainer historyStore)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" ⚖️  ALL-TIME JUDGE RELIABILITY & SELF-BIAS LEADERBOARD ");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();

        var judgeLeaderboard = BenchmarkHistoryStore.ComputeJudgeLeaderboard(historyStore);
        if (judgeLeaderboard.Count == 0)
        {
            Console.WriteLine("No benchmark runs recorded in history yet.");
            return;
        }

        Console.WriteLine(string.Format("{0,-6} | {1,-20} | {2,-14} | {3,-14} | {4,-14} | {5,-10}", "Rank", "Model ID", "Reliability", "Net SelfBias", "Abs SelfBias", "Books"));
        Console.WriteLine(new string('-', 90));

        for (int i = 0; i < judgeLeaderboard.Count; i++)
        {
            var j = judgeLeaderboard[i];
            var rank = i switch { 0 => "🥇 1", 1 => "🥈 2", 2 => "🥉 3", _ => $"   {i + 1}" };
            Console.WriteLine(string.Format("{0,-6} | {1,-20} | {2,-14:P0} | {3,-14:+0.00;-0.00;0.00} | {4,-14:F2} | {5,-10}", rank, j.ModelId, j.ReliabilityRate, j.AvgNetSelfBias, j.AvgAbsSelfBias, j.BooksJudged));
        }
        Console.WriteLine();
        Console.WriteLine("Reliability = fraction of books where judging completed without falling back to mock data.");
        Console.WriteLine("Net SelfBias = avg(selfScore - peerAverage); near zero is best. Abs SelfBias = avg magnitude of that gap.");
        Console.WriteLine();
    }

    private static List<string> LocateDefaultSuiteBooks(string workspaceRoot)
    {
        var suite = new List<string>();

        // 1. Nick and Me (Contemporary memoir / coastal setting)
        var nickFile = new[] { Path.Combine(workspaceRoot, "books", "Nick_and_Me.txt"), Path.Combine(workspaceRoot, "projects", "NickAndMe", "book_full.txt") }.FirstOrDefault(File.Exists);
        if (nickFile != null) suite.Add(nickFile);

        // 2. The Tell-Tale Heart (Gothic suspense monologue)
        var heartFile = new[] { Path.Combine(workspaceRoot, "books", "The_Tell-Tale_Heart.txt"), Path.Combine(workspaceRoot, "projects", "TellTaleHeartV7", "book_full.txt") }.FirstOrDefault(File.Exists);
        if (heartFile != null) suite.Add(heartFile);

        // 3. Buster (Children's picture book / hero animal)
        var busterFile = new[] { Path.Combine(workspaceRoot, "projects", "Buster", "book_full.txt"), Path.Combine(workspaceRoot, "books", "The_Velveteen_Rabbit.txt") }.FirstOrDefault(File.Exists);
        if (busterFile != null) suite.Add(busterFile);

        // 4. A Christmas Carol (Time-jumps & multi-age character age-splits)
        var carolFile = Path.Combine(workspaceRoot, "books", "A_Christmas_Carol.txt");
        if (File.Exists(carolFile)) suite.Add(carolFile);

        // 5. The Call of the Wild (Hero animal wilderness action directibility)
        var callFile = Path.Combine(workspaceRoot, "books", "The_Call_of_the_Wild.txt");
        if (File.Exists(callFile)) suite.Add(callFile);

        if (suite.Count == 0)
        {
            suite.Add(LocateSampleBookFile(workspaceRoot));
        }

        return suite;
    }

    private static string LocateSampleBookFile(string workspaceRoot)
    {
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "books", "book.txt"),
            Path.Combine(workspaceRoot, "books", "sample_story.txt"),
            Path.Combine(workspaceRoot, "projects", "Buster", "book_full.txt"),
            Path.Combine(workspaceRoot, "projects", "TellTaleHeartV7", "book_full.txt"),
            Path.Combine(workspaceRoot, "evals", "sample_book.txt"),
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(found)) return found;

        var sampleDir = Path.Combine(workspaceRoot, "evals");
        Directory.CreateDirectory(sampleDir);
        var samplePath = Path.Combine(sampleDir, "sample_book.txt");
        if (!File.Exists(samplePath))
        {
            var sampleText = @"Chapter 1: The Lighthouse Keeper

Young Nick, barely eight years old, sat on the cold stone floor of the lighthouse parlor, stringing glass beads onto a piece of twine. The wind outside howled against the cliffside, rattling the heavy timber window shutters.

Across the room, Uncle Nick—his hands calloused from decades at sea and his beard silvered by salt—stared out into the darkening storm. He checked his brass pocket watch, his knuckles whitening as he closed the latch.

""The tide turns early tonight, lad,"" Uncle Nick said, his voice deep and steady despite the gale. ""Keep the lamp oil topped.""

Young Nick looked up from his beads. ""Will the cutter hold if the reef swells, Uncle?""

Uncle Nick turned, offering a small, reassuring nod. ""She always holds when the beacon is bright.""";
            File.WriteAllText(samplePath, sampleText);
        }
        return samplePath;
    }

    private static (IChatClient Chat, string WorkspaceRoot) BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        var workspaceRoot = ResolveWorkspaceRoot();
        services.Configure<PageToMovieOptions>(opts => opts.WorkspaceRoot = workspaceRoot);
        services.AddSingleton<ProjectReadCache>();
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<ProjectTelemetryService>();
        // Mirror PageToMovie.Api's chat-client HttpClient timeouts (20 min) — full-book single-shot
        // adaptations can legitimately run past the bare 100s HttpClient default, which otherwise
        // cancels a real, still-in-progress generation and wastes the API spend for nothing.
        services.AddHttpClient<GrokChatClient>(c => c.Timeout = TimeSpan.FromMinutes(20));
        services.AddHttpClient<AnthropicChatClient>(c => c.Timeout = TimeSpan.FromMinutes(20));
        services.AddHttpClient<GeminiChatClient>(c => c.Timeout = TimeSpan.FromMinutes(20));
        services.AddSingleton<MultiProviderChatClient>();

        var provider = services.BuildServiceProvider();
        var chat = provider.GetRequiredService<MultiProviderChatClient>();
        return (chat, workspaceRoot);
    }

    /// <summary>
    /// Resolves a fixed PageToMovie checkout root so every path this tool writes (evals/cache,
    /// evals/results, benchmark_history.json, benchmark_dashboard.html, and the default books/
    /// projects/ suite lookups) lands in the same place regardless of which directory `dotnet run`
    /// was invoked from. Deliberately does NOT reuse <c>ProjectStore.ResolveWorkspaceRoot</c>'s
    /// Docker/Railway "/data" volume shortcut — on Windows, .NET resolves a leading "/" against the
    /// current drive root, so an unrelated local "C:\data" folder can silently hijack it. Instead
    /// this walks up from the executing assembly looking for the one unambiguous marker this repo
    /// actually has: <c>host/PageToMovie.slnx</c>.
    /// </summary>
    private static string ResolveWorkspaceRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("PageToMovie__WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
            return Path.GetFullPath(envRoot);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "host", "PageToMovie.slnx")))
                return dir.FullName;
        }

        Console.WriteLine("⚠️  Could not locate the PageToMovie checkout root (no host/PageToMovie.slnx found above this executable) — falling back to the current directory.");
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Parses a judge model's raw completion into a <see cref="JudgeEvaluationPayload"/>, tolerating markdown code fences.</summary>
    private static JudgeEvaluationPayload ParseJudgePayload(string raw, IEnumerable<string> expectedLabels)
    {
        var stripped = ClassifierJsonParser.StripFences(raw);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var payload = JsonSerializer.Deserialize<JudgeEvaluationPayload>(stripped, opts);

        if (payload is null || payload.Evaluations.Count == 0 || payload.ForcedRanking.Count == 0)
            throw new InvalidOperationException("Judge response was missing evaluations or a forced ranking.");

        var labelSet = new HashSet<string>(expectedLabels, StringComparer.OrdinalIgnoreCase);
        if (!payload.ForcedRanking.All(labelSet.Contains))
            throw new InvalidOperationException("Judge response ranked labels outside the anonymized candidate set.");

        return payload;
    }

    /// <summary>
    /// Deterministic hash of the full candidate screenplay set, used to invalidate a cached judge
    /// verdict the moment any candidate's screenplay text changes (regenerated after a fix, a
    /// retry, etc.) — a cached judge JSON otherwise has no way to know it describes text that no
    /// longer exists on disk.
    /// </summary>
    private static string ComputeScreenplaysHash(Dictionary<string, string> generatedScreenplays)
    {
        var sb = new StringBuilder();
        foreach (var modelId in generatedScreenplays.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(modelId.ToLowerInvariant());
            sb.Append('\n');
            sb.Append(generatedScreenplays[modelId]);
            sb.Append('\n');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// A benchmark is only comparable when its generation prompt is a committed revision. The
    /// revision returned is the commit that last changed the prompt, rather than HEAD, so unrelated
    /// application commits do not create artificial prompt-version buckets in benchmark history.
    /// </summary>
    /// <summary>
    /// Stage‑1 surface must be clean: <c>prompts/book_to_fountain.txt</c>, related Stage‑1 prompts,
    /// and <c>host/PageToMovie.Adaptation/</c> sources. Returns the committed prompt short hash as
    /// <paramref name="revision"/>. Pass <paramref name="allowDirty"/> only for local experiments.
    /// <summary>
    /// Screenplay generation benchmark surface gate. Verifies that prompts and Adaptation module sources
    /// are clean and committed before starting a benchmark.
    /// </summary>
    private static async Task<(bool Success, string Revision, string Error)> TryGetCommittedStage1SurfaceAsync(
        string workspaceRoot,
        bool allowDirty = false,
        CancellationToken ct = default)
    {
        try
        {
            static async Task<string> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct = default)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) throw new InvalidOperationException("Could not start git.");
                var outputTask = process.StandardOutput.ReadToEndAsync(ct);
                var errorTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var standardError = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException(standardError.Trim());
                return output.Trim();
            }

            // Paths that change Stage‑1 behavior (prompt + Adaptation module sources).
            string[] watched =
            {
                "prompts/book_to_fountain.txt",
                "prompts/fountain_to_cast.txt",
                "prompts/cast_visual_literalize.txt",
                "host/PageToMovie.Adaptation",
            };

            if (!allowDirty)
            {
                var dirty = new List<string>();
                foreach (var path in watched)
                {
                    var porcelain = await RunGitAsync(workspaceRoot, $"status --porcelain -- {path}", ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(porcelain))
                        dirty.Add(path);
                }

                if (dirty.Count > 0)
                {
                    return (false, "", "uncommitted Stage‑1 surface: " + string.Join(", ", dirty));
                }
            }
            else
            {
                Console.WriteLine("⚠️  --allow-dirty: skipping Stage‑1 clean-tree gate (results not comparable).");
            }

            const string promptPath = "prompts/book_to_fountain.txt";
            var commit = await RunGitAsync(workspaceRoot, $"log -1 --format=%H -- {promptPath}", ct).ConfigureAwait(false);
            if (commit.Length < 10)
            {
                return (false, "", "prompts/book_to_fountain.txt has no committed revision.");
            }
            return (true, commit[..10], "");
        }
        catch (Exception ex)
        {
            return (false, "", $"could not verify the committed Stage‑1 surface ({ex.Message})");
        }
    }

    /// <summary>Backward-compatible alias used by dashboard backfill helpers.</summary>
    private static Task<(bool Success, string Revision, string Error)> TryGetCommittedPromptRevisionAsync(
        string workspaceRoot,
        CancellationToken ct = default) =>
        TryGetCommittedStage1SurfaceAsync(workspaceRoot, allowDirty: false, ct);

    /// <summary>
    /// Legacy runs predate prompt revision tracking. Their timestamp can still identify the most
    /// recent prompt commit that existed when the run was made, so preserve that provenance rather
    /// than leaving the dashboard's revision column unknown.
    /// </summary>
    private static async Task<bool> BackfillLegacyPromptRevisionsAsync(HistoricalStoreContainer history, string workspaceRoot, CancellationToken ct = default)
    {
        var changed = false;
        foreach (var run in history.Runs.Where(r => string.IsNullOrWhiteSpace(r.PromptVersion)))
        {
            if (string.IsNullOrWhiteSpace(run.Timestamp))
                continue;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"rev-list -1 --before=\"{run.Timestamp}\" HEAD -- prompts/book_to_fountain.txt",
                    WorkingDirectory = workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) continue;
                var outputTask = process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var commit = (await outputTask.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0 || commit.Length < 10) continue;
                run.PromptVersion = commit[..10];
                run.PromptVersionInferred = true;
                changed = true;
            }
            catch { /* legacy history remains safely untagged if Git metadata is unavailable */ }
        }
        return changed;
    }

    /// <summary>Same as production Stage 1: <see cref="BookTextAnalyzer.ResolveStage1RuntimeMinutes"/>.</summary>
    internal static int ResolveTargetRuntimeMinutes(string bookText, int? overrideMinutes) =>
        BookTextAnalyzer.ResolveStage1RuntimeMinutes(bookText, overrideMinutes);

    private static string DescribeRuntimeSource(string bookText, int? overrideMinutes)
    {
        if (overrideMinutes is > 0)
            return $"(CLI override --target-runtime-minutes={overrideMinutes.Value})";
        var a = BookTextAnalyzer.Analyze(bookText ?? "");
        return $"(BookTextAnalyzer · kind={a.BookKind.ToApiString()} · words={a.TextWords} · pages={a.Pages} · same as production)";
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_').Replace('/', '_');

    private static async Task<ProjectVisionMeta.Document?> ReadVisionMetaAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ProjectVisionMeta.Document>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static Task WriteVisionMetaAsync(string path, ProjectVisionMeta.Document? visionMeta)
    {
        var json = JsonSerializer.Serialize(
            visionMeta,
            new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(path, json + Environment.NewLine);
    }

    internal static string BuildJudgeCandidatePackage(
        string fountain,
        ProjectVisionMeta.Document? visionMeta)
    {
        var metadata = visionMeta is null
            ? "(missing)"
            : JsonSerializer.Serialize(visionMeta, new JsonSerializerOptions { WriteIndented = true });
        return $"=== FOUNTAIN SCREENPLAY ===\n{fountain.TrimEnd()}\n\n" +
               $"=== VISION METADATA SIDECAR ===\n{metadata}\n";
    }

    /// <summary>
    /// Per-model TPM caps confirmed live (e.g. gpt-4o: HTTP 429 "Limit 30000... tokens per min" on
    /// this account/org). These are account-tier rate limits, not the model's real context window —
    /// deliberately kept out of <c>models_catalog.json</c> (which drives the real product's book
    /// adaptation for all users) and scoped to this benchmark only. Forces
    /// <see cref="AdaptationService.ConvertAsync"/> onto the multi-chunk path so each
    /// individual adapt call stays comfortably under the cap instead of one big one-shot request
    /// that blows through it regardless of what the model can actually hold.
    /// </summary>
    private static AdaptationFountain.PromptBudget? ResolveRateLimitSafeBudgetOverride(string modelId)
    {
        if (!string.Equals(modelId, "gpt-4o", StringComparison.OrdinalIgnoreCase))
            return null;

        return new AdaptationFountain.PromptBudget
        {
            ModelId = modelId,
            SingleShotBookMaxChars = 50_000,
            ChunkSoftMaxChars = 25_000,
            MaxChunks = AdaptationFountain.MaxAdaptChunks,
            ReservedOverheadChars = AdaptationFountain.DefaultReservedOverheadChars,
        };
    }

    private static JudgeEvaluationPayload DeAnonymizePayload(JudgeEvaluationPayload raw, Dictionary<string, string> anonMapping)
    {
        var result = new JudgeEvaluationPayload
        {
            JudgeSummaryNotes = raw.JudgeSummaryNotes,
            // Must carry through: AggregateBenchmarkData relies on this to exclude a failed judge's
            // fabricated (alphabetical-label-order) ForcedRanking from Borda points / rank sums.
            IsMock = raw.IsMock,
        };

        foreach (var rankLabel in raw.ForcedRanking)
        {
            if (anonMapping.TryGetValue(rankLabel, out var realModelId))
                result.ForcedRanking.Add(realModelId);
            else
                result.ForcedRanking.Add(rankLabel);
        }

        foreach (var entry in raw.Evaluations)
        {
            var realId = anonMapping.TryGetValue(entry.ScreenplayId, out var mapped) ? mapped : entry.ScreenplayId;
            result.Evaluations.Add(new ScreenplayEvaluationEntry
            {
                ScreenplayId = realId,
                AdaptationFidelity = entry.AdaptationFidelity,
                CharacterDisambiguation = entry.CharacterDisambiguation,
                AiVideoDirectibility = entry.AiVideoDirectibility,
                DramaticPacing = entry.DramaticPacing,
                DialogueAuthenticity = entry.DialogueAuthenticity,
                SoundDesignMusic = entry.SoundDesignMusic,
                OverallQualitativeScore = entry.OverallQualitativeScore,
                ProductionReady = entry.ProductionReady,
                DisqualifyingIssues = entry.DisqualifyingIssues,
                Rationale = entry.Rationale,
                PromptImprovementSuggestion = entry.PromptImprovementSuggestion,
            });
        }
        return result;
    }

    private static BenchmarkRunData AggregateBenchmarkData(
        string bookPath,
        List<string> candidateModels,
        Dictionary<string, string> screenplays,
        Dictionary<string, DeterministicSyntaxResult> deterministicResults,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        Dictionary<string, string> generationFallbacks,
        Dictionary<string, CastPackageCrossCheck.Report?>? castPackageResults = null)
    {
        var runData = new BenchmarkRunData { BookPath = bookPath };
        var borda = CreateBordaAccumulators(candidateModels);
        IngestJudgeEvaluations(runData, borda, candidateModels, judgeEvaluations);
        AppendSelfBiasNotes(runData, candidateModels, judgeEvaluations);
        FillLeaderboard(runData, borda, candidateModels, deterministicResults, judgeEvaluations, generationFallbacks, castPackageResults);
        runData.Leaderboard = runData.Leaderboard.OrderByDescending(l => l.CompositeScore).ToList();
        return runData;
    }

    private readonly record struct BordaAccumulators(
        Dictionary<string, int> Scores,
        Dictionary<string, double> RankSums,
        Dictionary<string, int> RankCounts);

    private static BordaAccumulators CreateBordaAccumulators(List<string> candidateModels) => new(
        candidateModels.ToDictionary(m => m, _ => 0),
        candidateModels.ToDictionary(m => m, _ => 0.0),
        candidateModels.ToDictionary(m => m, _ => 0));

    private static void IngestJudgeEvaluations(
        BenchmarkRunData runData,
        BordaAccumulators borda,
        List<string> candidateModels,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations)
    {
        foreach (var (judgeId, payload) in judgeEvaluations)
        {
            InitJudgeSlots(runData, judgeId, payload);
            RecordJudgeNotes(runData, judgeId, payload);
            if (payload.IsMock)
            {
                RecordMockJudgeRanks(runData, judgeId, payload);
                continue; // Do NOT count points or ranks for mock judges
            }

            RecordLiveJudgeRanks(runData, borda, candidateModels, judgeId, payload);
            RecordJudgeScores(runData, judgeId, payload);
        }
    }

    private static void InitJudgeSlots(BenchmarkRunData runData, string judgeId, JudgeEvaluationPayload payload)
    {
        runData.JudgeMatrix[judgeId] = new Dictionary<string, double>();
        runData.JudgeRankMatrix[judgeId] = new Dictionary<string, int>();
        runData.JudgeSummaries[judgeId] = payload.JudgeSummaryNotes;
        runData.JudgeRationale[judgeId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        runData.JudgePromptSuggestions[judgeId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void RecordJudgeNotes(BenchmarkRunData runData, string judgeId, JudgeEvaluationPayload payload)
    {
        foreach (var eval in payload.Evaluations)
        {
            if (!string.IsNullOrWhiteSpace(eval.Rationale))
                runData.JudgeRationale[judgeId][eval.ScreenplayId] = eval.Rationale; // last-wins if a malformed judge response repeats a screenplayId
            if (!string.IsNullOrWhiteSpace(eval.PromptImprovementSuggestion))
                runData.JudgePromptSuggestions[judgeId][eval.ScreenplayId] = eval.PromptImprovementSuggestion;
        }
    }

    private static void RecordMockJudgeRanks(BenchmarkRunData runData, string judgeId, JudgeEvaluationPayload payload)
    {
        foreach (var key in payload.ForcedRanking)
        {
            runData.JudgeRankMatrix[judgeId][key] = -1;
            runData.JudgeMatrix[judgeId][key] = -1.0;
        }
    }

    private static void RecordLiveJudgeRanks(
        BenchmarkRunData runData,
        BordaAccumulators borda,
        List<string> candidateModels,
        string judgeId,
        JudgeEvaluationPayload payload)
    {
        for (int r = 0; r < payload.ForcedRanking.Count; r++)
        {
            var authorId = payload.ForcedRanking[r];
            var rank = r + 1;
            var points = candidateModels.Count - r;
            if (borda.Scores.ContainsKey(authorId))
            {
                borda.Scores[authorId] += points;
                borda.RankSums[authorId] += rank;
                borda.RankCounts[authorId]++;
            }

            runData.JudgeRankMatrix[judgeId][authorId] = rank;
        }
    }

    private static void RecordJudgeScores(BenchmarkRunData runData, string judgeId, JudgeEvaluationPayload payload)
    {
        foreach (var eval in payload.Evaluations)
            runData.JudgeMatrix[judgeId][eval.ScreenplayId] = eval.OverallQualitativeScore >= 0.0 ? eval.OverallQualitativeScore : -1.0;
    }

    private static void AppendSelfBiasNotes(
        BenchmarkRunData runData,
        List<string> candidateModels,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations)
    {
        const double SelfBiasThreshold = 1.0;
        foreach (var judgeId in candidateModels)
        {
            if (!TryGetLiveSelfEval(judgeEvaluations, judgeId, out var selfEval, out var peerScores))
                continue;
            var peerAvg = peerScores.Average();
            var delta = selfEval.OverallQualitativeScore - peerAvg;
            AppendOneSelfBiasNote(runData, judgeId, selfEval.OverallQualitativeScore, peerAvg, peerScores.Count, delta, SelfBiasThreshold);
        }
    }

    private static bool TryGetLiveSelfEval(
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        string judgeId,
        out ScreenplayEvaluationEntry selfEval,
        out List<double> peerScores)
    {
        selfEval = null!;
        peerScores = new List<double>();
        if (!judgeEvaluations.TryGetValue(judgeId, out var judgePayload) || judgePayload.IsMock)
            return false;

        var found = judgePayload.Evaluations.FirstOrDefault(e =>
            string.Equals(e.ScreenplayId, judgeId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0);
        if (found is null) return false;
        selfEval = found;

        peerScores = judgeEvaluations
            .Where(kv => !string.Equals(kv.Key, judgeId, StringComparison.OrdinalIgnoreCase) && !kv.Value.IsMock)
            .SelectMany(kv => kv.Value.Evaluations)
            .Where(e => string.Equals(e.ScreenplayId, judgeId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0)
            .Select(e => e.OverallQualitativeScore)
            .ToList();
        return peerScores.Count > 0;
    }

    private static void AppendOneSelfBiasNote(
        BenchmarkRunData runData,
        string judgeId,
        double selfScore,
        double peerAvg,
        int peerCount,
        double delta,
        double threshold)
    {
        if (delta >= threshold)
        {
            runData.SelfBiasNotes.Add(
                $"⚠️ {judgeId} rated its own screenplay {selfScore:F1}/10 vs. a {peerAvg:F1}/10 average from {peerCount} other judge(s) (+{delta:F1}) — possible self-preference bias.");
        }
        else if (delta <= -threshold)
        {
            runData.SelfBiasNotes.Add(
                $"ℹ️ {judgeId} rated its own screenplay {selfScore:F1}/10 vs. a {peerAvg:F1}/10 average from {peerCount} other judge(s) ({delta:F1}) — notably harsher on itself than peers were.");
        }
    }

    private static void FillLeaderboard(
        BenchmarkRunData runData,
        BordaAccumulators borda,
        List<string> candidateModels,
        Dictionary<string, DeterministicSyntaxResult> deterministicResults,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        Dictionary<string, string> generationFallbacks,
        Dictionary<string, CastPackageCrossCheck.Report?>? castPackageResults)
    {
        foreach (var modelId in candidateModels)
        {
            runData.Leaderboard.Add(BuildModelScoreSummary(
                modelId, borda, candidateModels.Count, deterministicResults, judgeEvaluations, generationFallbacks, castPackageResults));
        }
    }

    private static ModelScoreSummary BuildModelScoreSummary(
        string modelId,
        BordaAccumulators borda,
        int candidateCount,
        Dictionary<string, DeterministicSyntaxResult> deterministicResults,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        Dictionary<string, string> generationFallbacks,
        Dictionary<string, CastPackageCrossCheck.Report?>? castPackageResults)
    {
        var syntax = deterministicResults[modelId];
        var modelEvals = CollectLiveEvalsForModel(judgeEvaluations, modelId);
        var avgQual = AverageOrZero(modelEvals, e => e.OverallQualitativeScore);
        var avgRank = borda.RankCounts[modelId] > 0
            ? borda.RankSums[modelId] / borda.RankCounts[modelId]
            : candidateCount / 2.0;
        var isFallback = generationFallbacks.TryGetValue(modelId, out var fallbackReason);
        CastPackageCrossCheck.Report? castReport = null;
        castPackageResults?.TryGetValue(modelId, out castReport);
        return new ModelScoreSummary
        {
            ModelId = modelId,
            CompositeScore = Math.Round((syntax.OverallSyntaxScore * 0.40) + (avgQual * 10.0 * 0.60), 1),
            BordaPoints = borda.Scores[modelId],
            AvgJudgeRank = Math.Round(avgRank, 1),
            SyntaxAudit = syntax,
            AvgAdaptationFidelity = Math.Round(AverageOrZero(modelEvals, e => e.AdaptationFidelity), 1),
            AvgCharacterDisambiguation = Math.Round(AverageOrZero(modelEvals, e => e.CharacterDisambiguation), 1),
            AvgAiVideoDirectibility = Math.Round(AverageOrZero(modelEvals, e => e.AiVideoDirectibility), 1),
            AvgDramaticPacing = Math.Round(AverageOrZero(modelEvals, e => e.DramaticPacing), 1),
            AvgDialogueAuthenticity = Math.Round(AverageOrZero(modelEvals, e => e.DialogueAuthenticity), 1),
            AvgSoundDesignMusic = Math.Round(AverageOrZero(modelEvals, e => e.SoundDesignMusic), 1),
            AvgOverallQualitative = Math.Round(avgQual, 1),
            IsGenerationFallback = isFallback,
            GenerationFallbackReason = fallbackReason,
            DisqualifyingFlags = CollectDisqualifyingFlags(judgeEvaluations, modelId),
            CastPackageScore = castReport?.Score,
            CastPackageMembershipScore = castReport?.MembershipScore,
            CastPackageDescriptionScore = castReport?.DescriptionScore,
            SpeakersMissingFromCast = castReport?.SpeakersMissingFromCast?.ToList() ?? new List<string>(),
            CastPackageOk = castReport?.Ok,
            CastPackageFailures = castReport?.Failures?.ToList() ?? new List<string>(),
            CastPackageWarnings = castReport?.Warnings?.ToList() ?? new List<string>(),
        };
    }

    private static List<ScreenplayEvaluationEntry> CollectLiveEvalsForModel(
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        string modelId) =>
        judgeEvaluations.Values
            .Where(p => !p.IsMock)
            .SelectMany(p => p.Evaluations)
            .Where(e => string.Equals(e.ScreenplayId, modelId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0)
            .ToList();

    private static List<string> CollectDisqualifyingFlags(
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations,
        string modelId)
    {
        var flags = new List<string>();
        foreach (var kv in judgeEvaluations)
        {
            if (kv.Value.IsMock) continue;
            foreach (var e in kv.Value.Evaluations)
            {
                if (!string.Equals(e.ScreenplayId, modelId, StringComparison.OrdinalIgnoreCase) || e.ProductionReady)
                    continue;
                flags.AddRange(FormatDisqualifyingIssues(kv.Key, e));
            }
        }
        return flags;
    }

    private static IEnumerable<string> FormatDisqualifyingIssues(string judgeId, ScreenplayEvaluationEntry e) =>
        e.DisqualifyingIssues.Count > 0
            ? e.DisqualifyingIssues.Select(issue => $"{judgeId}: {issue}")
            : new[] { $"{judgeId}: flagged not production-ready (no specific issue given)" };

    private static double AverageOrZero(
        List<ScreenplayEvaluationEntry> evals,
        Func<ScreenplayEvaluationEntry, double> selector) =>
        evals.Count > 0 ? evals.Average(selector) : 0.0;

    private static string GenerateMockScreenplay(string modelId)
    {
        return $@"Title: Mock Screenplay by {modelId}
Draft date: 2026-07-30

FADE IN:

INT. CABIN - DAY

YOUNG NICK (AGE 8), a curious boy in a wool sweater, sits near the hearth.

ADULT NICK (30s), weathered with salt-and-pepper beard, gazes out the rain-slick window.

ADULT NICK
(quietly)
The tide turns early today.

YOUNG NICK
Will the boat hold, Uncle?

~ Gentle acoustic guitar melody with quiet ambient drone

ADULT NICK
It always holds.

FADE OUT.";
    }

    private static JudgeEvaluationPayload GenerateMockJudgePayload(Dictionary<string, string> anonMapping, string judgeId)
    {
        var payload = new JudgeEvaluationPayload
        {
            IsMock = true
        };
        var keys = anonMapping.Keys.ToList();
        payload.ForcedRanking = keys;

        foreach (var key in keys)
        {
            payload.Evaluations.Add(new ScreenplayEvaluationEntry
            {
                ScreenplayId = key,
                AdaptationFidelity = -1.0,
                CharacterDisambiguation = -1.0,
                AiVideoDirectibility = -1.0,
                DramaticPacing = -1.0,
                DialogueAuthenticity = -1.0,
                SoundDesignMusic = -1.0,
                OverallQualitativeScore = -1.0,
                ProductionReady = false,
                DisqualifyingIssues = new List<string> { "Not a real assessment — judge call failed or was skipped." },
                Rationale = $"[MOCK / FAILED JUDGE] Model '{judgeId}' failed or was skipped for candidate '{key}'.",
            });
        }
        payload.JudgeSummaryNotes = $"⚠️ Mock judge evaluation returned for {judgeId}.";
        return payload;
    }

    private static void TryLoadDotEnv()
    {
        var dirs = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))
        };

        foreach (var dir in dirs.Distinct())
        {
            ApplyEnvFile(Path.Combine(dir, ".env"));
            ApplyEnvFile(Path.Combine(dir, ".env.local"));
        }
    }

    private static void ApplyEnvFile(string envPath)
    {
        if (!File.Exists(envPath)) return;
        foreach (var line in File.ReadAllLines(envPath))
            ApplyEnvLine(line);
    }

    private static void ApplyEnvLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) return;
        var idx = trimmed.IndexOf('=');
        if (idx <= 0) return;
        var k = trimmed.Substring(0, idx).Trim();
        var v = trimmed.Substring(idx + 1).Trim(' ', '"', '\'', '\r', '\n', '\t');
        if (!string.IsNullOrWhiteSpace(k) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)))
            Environment.SetEnvironmentVariable(k, v);
    }

    private static async Task RegradeSyntaxOnlyAsync(string historyFilePath, string workspaceRoot)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" 🎬 Film Studio — Syntax-Only Re-Grading (0 API Calls)");
        Console.WriteLine("==========================================================================");

        var historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        if (historyStore.Runs.Count == 0)
        {
            Console.WriteLine("No history runs found to re-grade.");
            return;
        }

        var archivedScreenplayDirectories = FindArchivedScreenplayDirectories(workspaceRoot);

        foreach (var run in historyStore.Runs)
            await RegradeOneHistoryRunAsync(run, archivedScreenplayDirectories).ConfigureAwait(false);

        BenchmarkHistoryStore.SaveHistory(historyStore, historyFilePath);

        var (currentRevOk, revision, _) = await TryGetCommittedPromptRevisionAsync(workspaceRoot).ConfigureAwait(false);
        var currentPromptCommit = currentRevOk
            ? revision
            : null;
        var dashboardHtml = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, currentPromptCommit);
        var dashboardFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
        await File.WriteAllTextAsync(dashboardFile, dashboardHtml);

        Console.WriteLine("\n✅ Syntax re-grading completed! Global Dashboard updated at:");
        Console.WriteLine($"   🌐 {Path.GetFullPath(dashboardFile)}");
    }

    private static async Task RegradeOneHistoryRunAsync(
        HistoricalBenchmarkRun run, Dictionary<string, string> archivedScreenplayDirectories)
    {
        var bookSlug = run.BookSlug;
        Console.WriteLine($"\n📖 Story: '{run.BookTitle}' ({bookSlug}) — Date: {run.Timestamp}");

        if (!archivedScreenplayDirectories.TryGetValue(MakeArchivedRunKey(run.Timestamp, bookSlug), out var screenplaysDirectory))
        {
            Console.WriteLine("  Archived screenplay files not found for this historical run; left unchanged to preserve score provenance.");
            return;
        }

        string? canonicalFallbackText = null;
        if (File.Exists(run.BookPath))
        {
            var bookText = await File.ReadAllTextAsync(run.BookPath);
            canonicalFallbackText = AdaptationService.ConvertHeuristic(run.BookTitle, AdaptationService.NormalizeBookText(bookText), "Author");
        }

        foreach (var m in run.ModelScores)
            await RegradeOneModelScoreAsync(m, run, screenplaysDirectory, canonicalFallbackText).ConfigureAwait(false);
    }

    private static async Task RegradeOneModelScoreAsync(
        ModelScoreSummary m, HistoricalBenchmarkRun run, string screenplaysDirectory, string? canonicalFallbackText)
    {
        var modelId = m.ModelId;
        var effortSuffix = string.IsNullOrWhiteSpace(run.ReasoningEffort) ? "" : $"_{SanitizeFileName(run.ReasoningEffort)}";
        var screenplayFile = Path.Combine(screenplaysDirectory, $"{SanitizeFileName(modelId)}{effortSuffix}.fountain");
        if (!File.Exists(screenplayFile))
        {
            Console.WriteLine($"  Model '{modelId,-15}' -> Archived screenplay file not found; left unchanged.");
            return;
        }

        var screenplayText = await File.ReadAllTextAsync(screenplayFile);
        var newSyntax = DeterministicSyntaxScorer.Evaluate(screenplayText);
        m.SyntaxAudit = newSyntax;
        m.IsGenerationFallback = canonicalFallbackText is not null
            && string.Equals(screenplayText, canonicalFallbackText, StringComparison.Ordinal);

        // Recompute composite score if live qual score is valid (>= 0)
        if (m.AvgOverallQualitative >= 0)
            m.CompositeScore = Math.Round((newSyntax.OverallSyntaxScore * 0.40) + (m.AvgOverallQualitative * 10.0 * 0.60), 1);

        var fallbackTag = m.IsGenerationFallback ? " ⚠️ FALLBACK DRAFT (not real model output)" : "";
        Console.WriteLine($"  Model '{modelId,-15}' -> Syntax: {newSyntax.OverallSyntaxScore,5:F1}% (Format: {newSyntax.FormatComplianceScore,3:F0}%, Budget: {newSyntax.SceneBudgetScore,3:F0}%, Pacing: {newSyntax.DialoguePacingScore,3:F0}%, Char: {newSyntax.CharacterDisambiguationScore,3:F0}%, Music: {newSyntax.MusicSpecScore,3:F0}%) | Composite: {m.CompositeScore:F1}{fallbackTag}");
    }

    private static Dictionary<string, string> FindArchivedScreenplayDirectories(string workspaceRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var resultsRoot = Path.Combine(workspaceRoot, "evals", "results");
        if (!Directory.Exists(resultsRoot)) return result;

        foreach (var runDataFile in Directory.EnumerateFiles(resultsRoot, "run_data.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(runDataFile));
                if (!document.RootElement.TryGetProperty("Timestamp", out var timestampElement)) continue;

                var timestamp = timestampElement.GetString();
                var bookDirectory = Path.GetDirectoryName(runDataFile);
                if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(bookDirectory)) continue;

                var screenplaysDirectory = Path.Combine(bookDirectory, "screenplays");
                if (!Directory.Exists(screenplaysDirectory)) continue;

                var bookSlug = new DirectoryInfo(bookDirectory).Name;
                result[MakeArchivedRunKey(timestamp, bookSlug)] = screenplaysDirectory;
            }
            catch (JsonException)
            {
                // Ignore malformed archived artifacts; the matching history entry stays untouched.
            }
        }

        return result;
    }

    private static string MakeArchivedRunKey(string timestamp, string bookSlug) =>
        $"{timestamp}\u001F{bookSlug}";
}
