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
using EngineFountainMap = PageToMovie.Engine.BookToFountainConverter;
using VisionMetaStatus = PageToMovie.Engine.ProjectVisionMetaStatus;
using CastPackageCrossCheck = PageToMovie.Adaptation.Validation.CastPackageCrossCheck;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;

namespace ScreenplayBenchmark;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();

        TryLoadDotEnv();

        Console.WriteLine("==========================================================================");
        Console.WriteLine(" 🎬 Film Studio — Screenplay Generation & Blind Peer-Evaluation Benchmark ");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();

        string? bookPath = null;
        string? suiteDir = null;
        string? outDir = null;
        string? bookSlug = null;
        List<string>? requestedModels = null;
        List<string>? requestedJudges = null;
        string? reasoningEffort = null;
        bool dryRun = false;
        bool showLeaderboardOnly = false;
        bool showJudgeLeaderboardOnly = false;
        bool retryFailed = false;
        bool syntaxOnly = false;
        bool adaptationSessionPilot = false;
        string adaptationModel = "grok-4.5";
        // null = production BookTextAnalyzer.SuggestedTotalMinutes (same as Stage 1).
        // Set only via --target-runtime-minutes.
        int? targetRuntimeMinutesOverride = null;
        string? judgeModel = null;
        string? judgeModel2 = null;
        string? videoModel = null;
        double adaptationJudgeTemperature = 0.0;
        bool adaptationClipShotPlan = false;
        bool adaptationDualAttachClipPlan = false;
        // Default true: dual-attach (no chaining) is the committed pipeline, not an opt-in experiment
        // anymore — pass --chained-only to skip it and run only the older chained reference path.
        bool adaptationDualAttachAll = true;
        bool refreshDashboard = false;
        bool reviewPrompt = false;
        List<string>? reviewModels = null;
        bool sidecarPilot = false;
        string? sidecarPilotModel = null;
        string? validateSidecarDirectory = null;
        double samplingTemperature = 0.2;
        bool bypassCache = false;
        bool allowDirty = false;
        bool useSharedCache = true;
        string sharedCacheUser = Environment.GetEnvironmentVariable("PTM_BENCHMARK_CACHE_USER") ?? "benchmark";
        string sharedCacheVisibility = Environment.GetEnvironmentVariable("PTM_BENCHMARK_CACHE_VISIBILITY") ?? "Forkable";

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--book", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                bookPath = args[++i];
            }
            else if (arg.Equals("--adaptation-session-pilot", StringComparison.OrdinalIgnoreCase))
            {
                adaptationSessionPilot = true;
            }
            else if (arg.Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                adaptationModel = args[++i].Trim();
            }
            else if (arg.Equals("--target-runtime-minutes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var trm) && trm > 0)
                    targetRuntimeMinutesOverride = trm;
            }
            else if (arg.Equals("--judge-model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                judgeModel = args[++i].Trim();
            }
            else if (arg.Equals("--judge-model-2", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                judgeModel2 = args[++i].Trim();
            }
            else if (arg.Equals("--video-model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                videoModel = args[++i].Trim();
            }
            else if (arg.Equals("--judge-temperature", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (double.TryParse(args[++i], out var jt)) adaptationJudgeTemperature = jt;
            }
            else if (arg.Equals("--clip-shot-plan", StringComparison.OrdinalIgnoreCase))
            {
                adaptationClipShotPlan = true;
            }
            else if (arg.Equals("--dual-attach-clip-plan", StringComparison.OrdinalIgnoreCase))
            {
                adaptationDualAttachClipPlan = true;
            }
            else if (arg.Equals("--dual-attach-all", StringComparison.OrdinalIgnoreCase))
            {
                adaptationDualAttachAll = true;
            }
            else if (arg.Equals("--chained-only", StringComparison.OrdinalIgnoreCase))
            {
                adaptationDualAttachAll = false;
            }
            else if (arg.Equals("--suite", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                suiteDir = args[++i];
            }
            else if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outDir = args[++i];
            }
            else if (arg.Equals("--book-slug", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                bookSlug = args[++i];
            }
            else if (arg.Equals("--models", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                requestedModels = args[++i].Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            }
            else if (arg.Equals("--judges", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                requestedJudges = args[++i].Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            }
            else if (arg.Equals("--reasoning-effort", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                reasoningEffort = args[++i].Trim();
            }
            else if (arg.Equals("--temperature", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                     && double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTemperature)
                     && parsedTemperature is >= 0 and <= 2)
            {
                samplingTemperature = parsedTemperature;
            }
            else if (arg.Equals("--no-cache", StringComparison.OrdinalIgnoreCase))
            {
                bypassCache = true;
            }
            else if (arg.Equals("--allow-dirty", StringComparison.OrdinalIgnoreCase))
            {
                allowDirty = true;
            }
            else if (arg.Equals("--no-shared-cache", StringComparison.OrdinalIgnoreCase))
            {
                useSharedCache = false;
            }
            else if (arg.Equals("--cache-user", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                sharedCacheUser = args[++i].Trim();
            }
            else if (arg.Equals("--cache-visibility", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                sharedCacheVisibility = args[++i].Trim();
            }
            else if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (arg.Equals("--leaderboard", StringComparison.OrdinalIgnoreCase))
            {
                showLeaderboardOnly = true;
            }
            else if (arg.Equals("--judge-leaderboard", StringComparison.OrdinalIgnoreCase))
            {
                showJudgeLeaderboardOnly = true;
            }
            else if (arg.Equals("--retry-failed", StringComparison.OrdinalIgnoreCase) || arg.Equals("--resume", StringComparison.OrdinalIgnoreCase))
            {
                retryFailed = true;
            }
            else if (arg.Equals("--syntax-only", StringComparison.OrdinalIgnoreCase) || arg.Equals("--regrade", StringComparison.OrdinalIgnoreCase))
            {
                syntaxOnly = true;
            }
            else if (arg.Equals("--refresh-dashboard", StringComparison.OrdinalIgnoreCase))
            {
                refreshDashboard = true;
            }
            else if (arg.Equals("--review-prompt", StringComparison.OrdinalIgnoreCase))
            {
                reviewPrompt = true;
            }
            else if (arg.Equals("--review-models", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                reviewModels = args[++i].Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            }
            else if (arg.Equals("--sidecar-pilot", StringComparison.OrdinalIgnoreCase))
            {
                sidecarPilot = true;
            }
            else if (arg.Equals("--sidecar-pilot-model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                sidecarPilotModel = args[++i].Trim();
            }
            else if (arg.Equals("--validate-sidecar-pilot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                validateSidecarDirectory = args[++i].Trim();
            }
        }

        var (chat, workspaceRoot) = BuildServices();
        Console.WriteLine($"📂 Workspace root: {workspaceRoot}");

        if (adaptationSessionPilot)
        {
            if (string.IsNullOrWhiteSpace(bookPath) || !File.Exists(bookPath))
            {
                Console.WriteLine("❌ Error: --adaptation-session-pilot requires --book <path/to/book.txt>.");
                return 1;
            }
            var (pilotSurfaceOk, pilotPromptRevision, pilotPromptError) = await TryGetCommittedStage1SurfaceAsync(workspaceRoot, allowDirty: false).ConfigureAwait(false);
            if (!pilotSurfaceOk)
            {
                await Console.Error.WriteLineAsync($"❌ Adaptation session pilot not started: {pilotPromptError}");
                await Console.Error.WriteLineAsync("   Commit Stage‑1 prompts and host/PageToMovie.Adaptation/, then run again.");
                return 1;
            }
            var pilotBookText = await File.ReadAllTextAsync(bookPath);
            var pilotRuntimeMinutes = ResolveTargetRuntimeMinutes(pilotBookText, targetRuntimeMinutesOverride);
            Console.WriteLine(
                $"⏱️  Target runtime {pilotRuntimeMinutes} min " +
                DescribeRuntimeSource(pilotBookText, targetRuntimeMinutesOverride));
            return await AdaptationSessionPilot.RunAsync(
                bookPath, bookSlug, adaptationModel, pilotRuntimeMinutes, workspaceRoot, pilotPromptRevision, CancellationToken.None,
                judgeModel, samplingTemperature, adaptationJudgeTemperature, adaptationClipShotPlan,
                adaptationDualAttachClipPlan, adaptationDualAttachAll, judgeModel2, videoModel);
        }

        var historyFilePath = Path.Combine(workspaceRoot, "evals", "benchmark_history.json");
        var historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        if (await BackfillLegacyPromptRevisionsAsync(historyStore, workspaceRoot).ConfigureAwait(false))
            BenchmarkHistoryStore.SaveHistory(historyStore, historyFilePath);

        if (reviewPrompt)
        {
            var models = reviewModels is { Count: > 0 }
                ? reviewModels
                : new List<string> { "gpt-5.6-terra", "grok-4.5" };
            return await PromptImprovementReview.RunAsync(workspaceRoot, chat, models);
        }

        if (sidecarPilot)
        {
            if (string.IsNullOrWhiteSpace(bookPath))
            {
                Console.Error.WriteLine("--sidecar-pilot requires --book <path/to/book.txt>.");
                return 1;
            }
            return await SidecarPlanningPilot.RunAsync(workspaceRoot, bookPath, sidecarPilotModel ?? "grok-4.5", chat);
        }

        if (!string.IsNullOrWhiteSpace(validateSidecarDirectory))
        {
            var validation = await SidecarArtifactValidator.ValidateDirectoryAsync(validateSidecarDirectory);
            Console.WriteLine($"🧪 Validation: {validation["status"]} ({validation["summary"]?["failure_count"]} repair target(s))");
            Console.WriteLine($"📄 Report: {Path.Combine(validateSidecarDirectory, "validation_report.json")}");
            return validation["status"]?.GetValue<string>() == "passed" ? 0 : 2;
        }

        if (showLeaderboardOnly)
        {
            PrintHistoricalLeaderboard(historyStore);
            return 0;
        }

        if (showJudgeLeaderboardOnly)
        {
            PrintJudgeLeaderboard(historyStore);
            return 0;
        }

        if (refreshDashboard)
        {
            var (revOk, revision, _) = await TryGetCommittedPromptRevisionAsync(workspaceRoot).ConfigureAwait(false);
            var currentPromptCommit = revOk
                ? revision
                : null;
            var dashboardHtml = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, currentPromptCommit);
            var dashboardFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
            await File.WriteAllTextAsync(dashboardFile, dashboardHtml);
            Console.WriteLine($"✅ Dashboard refreshed: {Path.GetFullPath(dashboardFile)}");
            return 0;
        }

        if (syntaxOnly)
        {
            await RegradeSyntaxOnlyAsync(historyFilePath, workspaceRoot);
            return 0;
        }

        var (surfaceOk, promptRevision, promptError) = await TryGetCommittedStage1SurfaceAsync(workspaceRoot, allowDirty).ConfigureAwait(false);
        if (!surfaceOk)
        {
            await Console.Error.WriteLineAsync($"❌ Benchmark not started: {promptError}");
            await Console.Error.WriteLineAsync("   Commit Stage‑1 prompts and host/PageToMovie.Adaptation/, then run again.");
            await Console.Error.WriteLineAsync("   (Local experiments only: pass --allow-dirty to skip this gate.)");
            return 1;
        }

        var adaptationVersion = PageToMovie.Adaptation.AdaptationVersion.Current;
        Console.WriteLine($"🔖 Prompt revision: {promptRevision}  ·  Adaptation version: {adaptationVersion}");

        outDir ??= Path.Combine(workspaceRoot, "evals", "results", $"screenplay_benchmark_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(outDir);

        if (!dryRun && !chat.IsConfigured)
        {
            Console.WriteLine("⚠️  No provider API key found in the environment (XAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY).");
            Console.WriteLine("   Generation and peer-judging will fall back to mock data. Pass --dry-run to silence this warning.");
        }

        List<string> bookSuiteFiles = new();
        if (!string.IsNullOrWhiteSpace(suiteDir) && Directory.Exists(suiteDir))
        {
            bookSuiteFiles = Directory.GetFiles(suiteDir, "*.txt", SearchOption.TopDirectoryOnly).ToList();
        }
        else if (string.IsNullOrWhiteSpace(bookPath))
        {
            // Default to curated 5-book benchmark suite
            bookSuiteFiles = LocateDefaultSuiteBooks(workspaceRoot);
        }

        if (bookSuiteFiles.Count > 0)
        {
            Console.WriteLine($"📚 Running Default 5-Book Evaluation Suite across {bookSuiteFiles.Count} stories...");
            foreach (var file in bookSuiteFiles)
            {
                var slug = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                await RunSingleBookBenchmarkAsync(file, slug, outDir, requestedModels, requestedJudges, dryRun, retryFailed, historyFilePath, chat, workspaceRoot, promptRevision, adaptationVersion, reasoningEffort, samplingTemperature, bypassCache, adaptationJudgeTemperature, useSharedCache, sharedCacheUser, sharedCacheVisibility, targetRuntimeMinutesOverride);
            }

            // Generate updated HTML Dashboard after suite execution
            historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
            var dashboardHtml = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, promptRevision);
            var dashboardFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
            await File.WriteAllTextAsync(dashboardFile, dashboardHtml);

            Console.WriteLine();
            Console.WriteLine($"✅ Multi-Book Suite Completed! Global Dashboard updated at:");
            Console.WriteLine($"   🌐 {Path.GetFullPath(dashboardFile)}");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(bookPath) || !File.Exists(bookPath))
        {
            Console.WriteLine("❌ Error: Book file not found. Provide --book <path/to/book.txt> or --suite <dir>.");
            return 1;
        }

        bookSlug ??= Path.GetFileNameWithoutExtension(bookPath).ToLowerInvariant();
        await RunSingleBookBenchmarkAsync(bookPath, bookSlug, outDir, requestedModels, requestedJudges, dryRun, retryFailed, historyFilePath, chat, workspaceRoot, promptRevision, adaptationVersion, reasoningEffort, samplingTemperature, bypassCache, adaptationJudgeTemperature, useSharedCache, sharedCacheUser, sharedCacheVisibility, targetRuntimeMinutesOverride);

        // Generate updated HTML Dashboard
        historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        var html = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, promptRevision);
        var dashFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
        await File.WriteAllTextAsync(dashFile, html);

        Console.WriteLine($"   🌐 Interactive HTML Dashboard: {Path.GetFullPath(dashFile)}");
        return 0;
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
            var sharedPrompt = await new AdaptationService().BuildSystemPromptAsync(generationRuntimeMinutes);
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
        var adaptationFacade = new AdaptationService();
        var canonicalFallbackText = adaptationFacade.ConvertHeuristic(
            Path.GetFileNameWithoutExtension(bookPath), adaptationFacade.NormalizeBookText(bookText), "Author");
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
            Console.Write($"  [Adaptation] Model '{modelId}'... ");
            string screenplayText;
            ProjectVisionMeta.Document? visionMeta;

            var screenplayFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.fountain");
            var visionMetaFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.vision_meta.json");
            // A screenplay cache must be scoped to the committed prompt revision, the Adaptation
            // module surface (converter + embedded prompt identity), and the model / effort /
            // temperature. Otherwise a V4 benchmark could silently reuse a V3 draft, or a
            // prompt-unchanged converter fix would keep grading stale Fountain.
            var adaptationKey = SanitizeFileName(adaptationVersion);
            var cacheFile = Path.Combine(workspaceRoot, "evals", "cache", bookSlug, $"{SanitizeFileName(modelId)}{effortSuffix}_{promptRevision}_{adaptationKey}_temp{temperatureKey}.fountain");
            var cacheVisionMetaFile = Path.Combine(workspaceRoot, "evals", "cache", bookSlug, $"{SanitizeFileName(modelId)}{effortSuffix}_{promptRevision}_{adaptationKey}_temp{temperatureKey}.vision_meta.json");


            var diskCached = File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile) : null;
            var localCached = File.Exists(screenplayFile) ? await File.ReadAllTextAsync(screenplayFile) : null;
            var diskVisionMeta = await ReadVisionMetaAsync(cacheVisionMetaFile);
            var localVisionMeta = await ReadVisionMetaAsync(visionMetaFile);
            var sharedBehaviorVersions = JsonSerializer.Serialize(new
            {
                title = Path.GetFileNameWithoutExtension(bookPath),
                author = "Author",
                totalRuntimeMinutes = generationRuntimeMinutes,
                visionMetaSchema = ProjectVisionMeta.SchemaVersion,
                reasoningEffort,
                cachePackageSchema = "adaptation-conversion.v1",
            });
            DerivedBookArtifact? sharedArtifact = null;
            if (sharedCache is not null && sharedBook is not null && sharedPromptHash is not null)
            {
                sharedArtifact = await sharedCache.FindArtifactAsync(
                    sharedBook.BookId, sharedCacheUser, "adaptation_conversion", modelId,
                    "book-to-fountain-" + sharedPromptHash[..12], sharedPromptHash,
                    samplingTemperature, sharedBehaviorVersions);
            }

            if (sharedArtifact is not null &&
                JsonSerializer.Deserialize<EngineConversionResult>(sharedArtifact.Content) is
                    { Fountain.Length: > 0, VisionMeta: not null } sharedConversion)
            {
                screenplayText = sharedConversion.Fountain;
                visionMeta = sharedConversion.VisionMeta;
                Console.WriteLine($"(reused shared cache {sharedArtifact.ArtifactId})");
            }
            else if (!bypassCache && diskCached is not null && diskVisionMeta is not null && !string.Equals(diskCached, canonicalFallbackText, StringComparison.Ordinal))
            {
                screenplayText = diskCached;
                visionMeta = diskVisionMeta;
                Console.WriteLine("(reused from disk cache)");
            }
            else if (localCached is not null && localVisionMeta is not null && !string.Equals(localCached, canonicalFallbackText, StringComparison.Ordinal))
            {
                screenplayText = localCached;
                visionMeta = localVisionMeta;
                Console.WriteLine("(reused from local run folder)");
            }
            else if (dryRun)
            {
                if (diskCached is not null) Console.Write("(ignoring stale fallback-poisoned cache) ");
                screenplayText = GenerateMockScreenplay(modelId);
                visionMeta = null;
                Console.WriteLine("(mock generated)");
            }
            else
            {
                if (diskCached is not null)
                    Console.Write("(ignoring stale fallback-poisoned cache, retrying live) ");
                try
                {
                    var budget = ResolveRateLimitSafeBudgetOverride(modelId);
                    var adaptResult = await new AdaptationService().ConvertAsync(
                        new PageToMovie.Adaptation.Contracts.AdaptationRequest
                        {
                            BookText = bookText,
                            Title = Path.GetFileNameWithoutExtension(bookPath),
                            Author = "Author",
                            TargetRuntimeMinutes = generationRuntimeMinutes,
                            ModelId = modelId,
                            Temperature = samplingTemperature,
                            ReasoningEffort = reasoningEffort,
                        },
                        chat,
                        new Progress<string>(msg => Console.WriteLine($"    · {msg}")),
                        budgetOverride: budget);
                    if (adaptResult.UsedHeuristicFallback)
                        generationFallbacks[modelId] = "adaptation_heuristic_fallback";
                    screenplayText = adaptResult.Fountain;
                    visionMeta = EngineFountainMap.MapVision(adaptResult.VisionMeta);
                    var conversion = new EngineConversionResult
                    {
                        Fountain = screenplayText,
                        VisionMeta = visionMeta,
                        VisionMetaStatus = adaptResult.VisionMetaStatus switch
                        {
                            PageToMovie.Adaptation.Contracts.AdaptationVisionMetaStatus.PrimaryResponse => VisionMetaStatus.PrimaryResponse,
                            PageToMovie.Adaptation.Contracts.AdaptationVisionMetaStatus.RepairResponse => VisionMetaStatus.RepairResponse,
                            PageToMovie.Adaptation.Contracts.AdaptationVisionMetaStatus.Missing => VisionMetaStatus.Missing,
                            PageToMovie.Adaptation.Contracts.AdaptationVisionMetaStatus.Malformed => VisionMetaStatus.Malformed,
                            PageToMovie.Adaptation.Contracts.AdaptationVisionMetaStatus.InvalidValue => VisionMetaStatus.InvalidValue,
                            _ => VisionMetaStatus.Missing,
                        },
                        VisionMetaError = adaptResult.VisionMetaError,
                    };

                    if (generationFallbacks.TryGetValue(modelId, out var fallbackReason))
                    {
                        Console.WriteLine($"FALLBACK ({fallbackReason}) — non-AI heuristic draft, not cached, excluded from comparison");
                    }
                    else
                    {
                        Console.WriteLine("DONE");

                        // A complete cache entry requires both genuine Fountain and its required
                        // visual metadata. Fountain-only legacy entries are intentionally ignored.
                        if (visionMeta is not null)
                        {
                            Directory.CreateDirectory(Path.Combine(workspaceRoot, "evals", "cache", bookSlug));
                            await File.WriteAllTextAsync(cacheFile, screenplayText);
                            await WriteVisionMetaAsync(cacheVisionMetaFile, visionMeta);
                            if (sharedCache is not null && sharedBook is not null && sharedPromptHash is not null)
                            {
                                await sharedCache.RegisterArtifactAsync(
                                    sharedBook.BookId, sharedCacheUser, "adaptation_conversion",
                                    JsonSerializer.Serialize(conversion), modelId,
                                    "book-to-fountain-" + sharedPromptHash[..12], sharedPromptHash,
                                    samplingTemperature, sharedBehaviorVersions);
                            }
                        }
                        else
                        {
                            Console.WriteLine($"    · {conversion.VisionMetaError} Candidate package will not be cached.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex.Message}");
                    screenplayText = $"FADE IN:\n\nINT. ERROR - DAY\n\n[Adaptation failed for {modelId}: {ex.Message}]\n\nFADE OUT.";
                    visionMeta = null;
                    generationFallbacks[modelId] = ex.Message;
                }
            }

            await File.WriteAllTextAsync(screenplayFile, screenplayText);
            await WriteVisionMetaAsync(visionMetaFile, visionMeta);
            generatedScreenplays[modelId] = screenplayText;
            generatedVisionMeta[modelId] = visionMeta;

            var syntaxAudit = DeterministicSyntaxScorer.Evaluate(screenplayText);
            deterministicResults[modelId] = syntaxAudit;

            // Layer 2 (deterministic): cast package when present alongside the Fountain.
            // Stage 1 score alone does not judge cast_seeds.json — that is intentional until
            // cast extraction is part of the run. When the file exists, cross-check membership.
            var castSeedsFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.cast_seeds.json");
            if (File.Exists(castSeedsFile))
            {
                var castJson = await File.ReadAllTextAsync(castSeedsFile);
                var castReport = CastPackageCrossCheck.Evaluate(screenplayText, castJson, bookText);
                castPackageResults[modelId] = castReport;
                await File.WriteAllTextAsync(
                    Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.cast_package_report.json"),
                    JsonSerializer.Serialize(castReport, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(
                    castReport.Ok
                        ? $"    · Cast package OK · score {castReport.Score:F1}"
                        : $"    · Cast package FAIL · score {castReport.Score:F1} · {castReport.Failures.Count} issue(s)");
            }
            else
            {
                // Explicit null marker so reports can show "cast not evaluated".
                castPackageResults[modelId] = null;
            }
        }

        // Phase 3 & 4: Blind Cross-Evaluation
        var judgeEvaluations = new Dictionary<string, JudgeEvaluationPayload>(StringComparer.OrdinalIgnoreCase);
        var random = new Random(42);

        // Same shared prompt every candidate was generated from (workspaceRoot is ignored by
        // PromptFiles.ReadAsync — it always resolves PAGETOMOVIE_PROMPTS_DIR or the embedded
        // prompts/book_to_fountain.txt, so this is the exact text every model above just ran
        // under, not an approximation). Judges need to see this to suggest a REAL prompt fix
        // instead of guessing blind at what the prompt might already say.
        var generationSystemPrompt = await new AdaptationService().BuildSystemPromptAsync(generationRuntimeMinutes);

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
            Console.Write($"  [Peer Judge] Model '{judgeModelId}'... ");

            if (realCandidates.Count == 0)
            {
                Console.WriteLine("(no real candidates to judge — all generations fell back)");
                judgeEvaluations[judgeModelId] = GenerateMockJudgePayload(new Dictionary<string, string>(), judgeModelId);
                continue;
            }

            var keys = realCandidates.OrderBy(_ => random.Next()).ToList();
            var anonMapping = new Dictionary<string, string>();
            var anonScreenplays = new Dictionary<string, string>();

            for (int i = 0; i < keys.Count; i++)
            {
                var label = $"Screenplay {(char)('A' + i)}";
                anonMapping[label] = keys[i];
                anonScreenplays[label] = BuildJudgeCandidatePackage(
                    generatedScreenplays[keys[i]], generatedVisionMeta[keys[i]]);
            }

            // Same effort-suffix reasoning as the generation cache above: a judge verdict formed
            // at boosted reasoning effort is not interchangeable with one at default effort, even
            // when ScreenplaysHash matches (the candidates could be unchanged while only the
            // judge's own effort level changed).
            var judgeCacheFile = Path.Combine(workspaceRoot, "evals", "cache", bookSlug, $"judge_{judgeModelId}{effortSuffix}_{promptRevision}_{SanitizeFileName(adaptationVersion)}_temp{temperatureKey}_judgetemp{judgeTemperatureKey}.json");
            JudgeEvaluationPayload? cachedJudge = null;

            if (!bypassCache && File.Exists(judgeCacheFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(judgeCacheFile);
                    var loaded = JsonSerializer.Deserialize<JudgeEvaluationPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded is not null && !loaded.IsMock && loaded.Evaluations.Count > 0 && loaded.Evaluations.All(e => e.OverallQualitativeScore >= 0.0)
                        && loaded.RubricVersion == ScreenplayJudgmentRubric.RubricVersion
                        && loaded.ScreenplaysHash == screenplaysHash)
                    {
                        cachedJudge = loaded;
                    }
                }
                catch { /* Corrupt cache — re-evaluate */ }
            }

            JudgeEvaluationPayload evalPayload;
            if (cachedJudge is not null && (!retryFailed || !cachedJudge.IsMock))
            {
                evalPayload = cachedJudge;
                Console.WriteLine("DONE (cached live evaluation)");
            }
            else if (dryRun)
            {
                evalPayload = GenerateMockJudgePayload(anonMapping, judgeModelId);
                Console.WriteLine("(mock evaluated)");
            }
            else if (!chat.IsConfigured)
            {
                evalPayload = GenerateMockJudgePayload(anonMapping, judgeModelId);
                Console.WriteLine("(no provider API key configured — mock evaluated)");
            }
            else
            {
                try
                {
                    var userPrompt = ScreenplayJudgmentRubric.BuildPrompt(bookText, anonScreenplays, generationSystemPrompt);
                    var raw = await chat.CompleteAsync(
                        systemPrompt: "Respond with ONLY the JSON object described in the instructions. No prose, no markdown code fences.",
                        userPrompt: userPrompt,
                        model: judgeModelId,
                        temperature: judgeTemperature,
                        mode: "screenplay_benchmark_judge",
                        reasoningEffort: reasoningEffort);
                    evalPayload = ParseJudgePayload(raw, anonMapping.Keys);
                    evalPayload.IsMock = false;
                    evalPayload.RubricVersion = ScreenplayJudgmentRubric.RubricVersion;
                    evalPayload.ScreenplaysHash = screenplaysHash;
                    Console.WriteLine("DONE");

                    // Save valid live evaluation to cache
                    Directory.CreateDirectory(Path.Combine(workspaceRoot, "evals", "cache", bookSlug));
                    await File.WriteAllTextAsync(judgeCacheFile, JsonSerializer.Serialize(evalPayload, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED ({ex.Message}) — falling back to mock evaluation (-1.0)");
                    evalPayload = GenerateMockJudgePayload(anonMapping, judgeModelId);
                }
            }

            var deAnonymizedPayload = DeAnonymizePayload(evalPayload, anonMapping);
            judgeEvaluations[judgeModelId] = deAnonymizedPayload;
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
            ReservedOverheadChars = AdaptationFountain.ReservedOverheadChars,
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
        var runData = new BenchmarkRunData
        {
            BookPath = bookPath,
        };

        var BordaScores = candidateModels.ToDictionary(m => m, _ => 0);
        var RankSums = candidateModels.ToDictionary(m => m, _ => 0.0);
        var RankCounts = candidateModels.ToDictionary(m => m, _ => 0);

        foreach (var (judgeId, payload) in judgeEvaluations)
        {
            runData.JudgeMatrix[judgeId] = new Dictionary<string, double>();
            runData.JudgeRankMatrix[judgeId] = new Dictionary<string, int>();
            runData.JudgeSummaries[judgeId] = payload.JudgeSummaryNotes;
            runData.JudgeRationale[judgeId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            runData.JudgePromptSuggestions[judgeId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var eval in payload.Evaluations)
            {
                if (!string.IsNullOrWhiteSpace(eval.Rationale))
                    runData.JudgeRationale[judgeId][eval.ScreenplayId] = eval.Rationale; // last-wins if a malformed judge response repeats a screenplayId
                if (!string.IsNullOrWhiteSpace(eval.PromptImprovementSuggestion))
                    runData.JudgePromptSuggestions[judgeId][eval.ScreenplayId] = eval.PromptImprovementSuggestion;
            }

            if (payload.IsMock)
            {
                foreach (var key in payload.ForcedRanking)
                {
                    runData.JudgeRankMatrix[judgeId][key] = -1;
                    runData.JudgeMatrix[judgeId][key] = -1.0;
                }
                continue; // Do NOT count points or ranks for mock judges
            }

            for (int r = 0; r < payload.ForcedRanking.Count; r++)
            {
                var authorId = payload.ForcedRanking[r];
                var rank = r + 1;
                var points = candidateModels.Count - r;

                if (BordaScores.ContainsKey(authorId))
                {
                    BordaScores[authorId] += points;
                    RankSums[authorId] += rank;
                    RankCounts[authorId]++;
                }

                runData.JudgeRankMatrix[judgeId][authorId] = rank;
            }

            foreach (var eval in payload.Evaluations)
            {
                runData.JudgeMatrix[judgeId][eval.ScreenplayId] = eval.OverallQualitativeScore >= 0.0 ? eval.OverallQualitativeScore : -1.0;
            }
        }

        // Self-bias check: every judge is also a candidate here, so compare each judge's score for
        // its OWN screenplay against the average score OTHER (non-mock) judges gave that same
        // candidate. A judge rating itself well above its peers' consensus is the exact failure mode
        // blind anonymized review is meant to catch.
        const double SelfBiasThreshold = 1.0;
        foreach (var judgeId in candidateModels)
        {
            if (!judgeEvaluations.TryGetValue(judgeId, out var judgePayload) || judgePayload.IsMock) continue;

            var selfEval = judgePayload.Evaluations.FirstOrDefault(e =>
                string.Equals(e.ScreenplayId, judgeId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0);
            if (selfEval is null) continue;

            var peerScores = judgeEvaluations
                .Where(kv => !string.Equals(kv.Key, judgeId, StringComparison.OrdinalIgnoreCase) && !kv.Value.IsMock)
                .SelectMany(kv => kv.Value.Evaluations)
                .Where(e => string.Equals(e.ScreenplayId, judgeId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0)
                .Select(e => e.OverallQualitativeScore)
                .ToList();
            if (peerScores.Count == 0) continue;

            var peerAvg = peerScores.Average();
            var delta = selfEval.OverallQualitativeScore - peerAvg;
            if (delta >= SelfBiasThreshold)
            {
                runData.SelfBiasNotes.Add(
                    $"⚠️ {judgeId} rated its own screenplay {selfEval.OverallQualitativeScore:F1}/10 vs. a {peerAvg:F1}/10 average from {peerScores.Count} other judge(s) (+{delta:F1}) — possible self-preference bias.");
            }
            else if (delta <= -SelfBiasThreshold)
            {
                runData.SelfBiasNotes.Add(
                    $"ℹ️ {judgeId} rated its own screenplay {selfEval.OverallQualitativeScore:F1}/10 vs. a {peerAvg:F1}/10 average from {peerScores.Count} other judge(s) ({delta:F1}) — notably harsher on itself than peers were.");
            }
        }

        foreach (var modelId in candidateModels)
        {
            var syntax = deterministicResults[modelId];

            var modelEvals = judgeEvaluations.Values
                .Where(p => !p.IsMock)
                .SelectMany(p => p.Evaluations)
                .Where(e => string.Equals(e.ScreenplayId, modelId, StringComparison.OrdinalIgnoreCase) && e.OverallQualitativeScore >= 0.0)
                .ToList();

            var disqualifyingFlags = judgeEvaluations
                .Where(kv => !kv.Value.IsMock)
                .SelectMany(kv => kv.Value.Evaluations
                    .Where(e => string.Equals(e.ScreenplayId, modelId, StringComparison.OrdinalIgnoreCase) && !e.ProductionReady)
                    .SelectMany(e => e.DisqualifyingIssues.Count > 0
                        ? e.DisqualifyingIssues.Select(issue => $"{kv.Key}: {issue}")
                        : new[] { $"{kv.Key}: flagged not production-ready (no specific issue given)" }))
                .ToList();

            var avgFidelity = modelEvals.Count > 0 ? modelEvals.Average(e => e.AdaptationFidelity) : 0.0;
            var avgCharSplit = modelEvals.Count > 0 ? modelEvals.Average(e => e.CharacterDisambiguation) : 0.0;
            var avgDirect = modelEvals.Count > 0 ? modelEvals.Average(e => e.AiVideoDirectibility) : 0.0;
            var avgPacing = modelEvals.Count > 0 ? modelEvals.Average(e => e.DramaticPacing) : 0.0;
            var avgDialogue = modelEvals.Count > 0 ? modelEvals.Average(e => e.DialogueAuthenticity) : 0.0;
            var avgMusic = modelEvals.Count > 0 ? modelEvals.Average(e => e.SoundDesignMusic) : 0.0;
            var avgQual = modelEvals.Count > 0 ? modelEvals.Average(e => e.OverallQualitativeScore) : 0.0;

            var avgRank = RankCounts[modelId] > 0 ? RankSums[modelId] / RankCounts[modelId] : candidateModels.Count / 2.0;
            var composite = Math.Round((syntax.OverallSyntaxScore * 0.40) + (avgQual * 10.0 * 0.60), 1);

            var isFallback = generationFallbacks.TryGetValue(modelId, out var fallbackReason);

            CastPackageCrossCheck.Report? castReport = null;
            castPackageResults?.TryGetValue(modelId, out castReport);

            runData.Leaderboard.Add(new ModelScoreSummary
            {
                ModelId = modelId,
                CompositeScore = composite,
                BordaPoints = BordaScores[modelId],
                AvgJudgeRank = Math.Round(avgRank, 1),
                SyntaxAudit = syntax,
                AvgAdaptationFidelity = Math.Round(avgFidelity, 1),
                AvgCharacterDisambiguation = Math.Round(avgCharSplit, 1),
                AvgAiVideoDirectibility = Math.Round(avgDirect, 1),
                AvgDramaticPacing = Math.Round(avgPacing, 1),
                AvgDialogueAuthenticity = Math.Round(avgDialogue, 1),
                AvgSoundDesignMusic = Math.Round(avgMusic, 1),
                AvgOverallQualitative = Math.Round(avgQual, 1),
                IsGenerationFallback = isFallback,
                GenerationFallbackReason = fallbackReason,
                DisqualifyingFlags = disqualifyingFlags,
                CastPackageScore = castReport?.Score,
                CastPackageMembershipScore = castReport?.MembershipScore,
                CastPackageDescriptionScore = castReport?.DescriptionScore,
                SpeakersMissingFromCast = castReport?.SpeakersMissingFromCast?.ToList() ?? new List<string>(),
                CastPackageOk = castReport?.Ok,
                CastPackageFailures = castReport?.Failures?.ToList() ?? new List<string>(),
                CastPackageWarnings = castReport?.Warnings?.ToList() ?? new List<string>(),
            });
        }

        runData.Leaderboard = runData.Leaderboard.OrderByDescending(l => l.CompositeScore).ToList();
        return runData;
    }

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
            var envFiles = new[] { Path.Combine(dir, ".env"), Path.Combine(dir, ".env.local") };
            foreach (var envPath in envFiles)
            {
                if (File.Exists(envPath))
                {
                    foreach (var line in File.ReadAllLines(envPath))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;
                        var idx = trimmed.IndexOf('=');
                        if (idx > 0)
                        {
                            var k = trimmed.Substring(0, idx).Trim();
                            var v = trimmed.Substring(idx + 1).Trim(' ', '"', '\'', '\r', '\n', '\t');
                            if (!string.IsNullOrWhiteSpace(k) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)))
                            {
                                Environment.SetEnvironmentVariable(k, v);
                            }
                        }
                    }
                }
            }
        }
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
        {
            var bookSlug = run.BookSlug;
            Console.WriteLine($"\n📖 Story: '{run.BookTitle}' ({bookSlug}) — Date: {run.Timestamp}");

            if (!archivedScreenplayDirectories.TryGetValue(MakeArchivedRunKey(run.Timestamp, bookSlug), out var screenplaysDirectory))
            {
                Console.WriteLine("  Archived screenplay files not found for this historical run; left unchanged to preserve score provenance.");
                continue;
            }

            string? canonicalFallbackText = null;
            if (File.Exists(run.BookPath))
            {
                var bookText = await File.ReadAllTextAsync(run.BookPath);
                canonicalFallbackText = new AdaptationService().ConvertHeuristic(run.BookTitle, new AdaptationService().NormalizeBookText(bookText), "Author");
            }

            foreach (var m in run.ModelScores)
            {
                var modelId = m.ModelId;
                var effortSuffix = string.IsNullOrWhiteSpace(run.ReasoningEffort) ? "" : $"_{SanitizeFileName(run.ReasoningEffort)}";
                var screenplayFile = Path.Combine(screenplaysDirectory, $"{SanitizeFileName(modelId)}{effortSuffix}.fountain");
                if (File.Exists(screenplayFile))
                {
                    var screenplayText = await File.ReadAllTextAsync(screenplayFile);
                    var newSyntax = DeterministicSyntaxScorer.Evaluate(screenplayText);
                    m.SyntaxAudit = newSyntax;
                    m.IsGenerationFallback = canonicalFallbackText is not null
                        && string.Equals(screenplayText, canonicalFallbackText, StringComparison.Ordinal);

                    // Recompute composite score if live qual score is valid (>= 0)
                    if (m.AvgOverallQualitative >= 0)
                    {
                        m.CompositeScore = Math.Round((newSyntax.OverallSyntaxScore * 0.40) + (m.AvgOverallQualitative * 10.0 * 0.60), 1);
                    }

                    var fallbackTag = m.IsGenerationFallback ? " ⚠️ FALLBACK DRAFT (not real model output)" : "";
                    Console.WriteLine($"  Model '{modelId,-15}' -> Syntax: {newSyntax.OverallSyntaxScore,5:F1}% (Format: {newSyntax.FormatComplianceScore,3:F0}%, Budget: {newSyntax.SceneBudgetScore,3:F0}%, Pacing: {newSyntax.DialoguePacingScore,3:F0}%, Char: {newSyntax.CharacterDisambiguationScore,3:F0}%, Music: {newSyntax.MusicSpecScore,3:F0}%) | Composite: {m.CompositeScore:F1}{fallbackTag}");
                }
                else
                {
                    Console.WriteLine($"  Model '{modelId,-15}' -> Archived screenplay file not found; left unchanged.");
                }
            }
        }

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
