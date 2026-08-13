using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PageToMovie.Engine.Abstractions;

namespace ScreenplayBenchmark;

public static partial class Program
{
    private sealed class CliOptions
    {
        public string? BookPath;
        public string? SuiteDir;
        public string? OutDir;
        public string? BookSlug;
        public List<string>? RequestedModels;
        public List<string>? RequestedJudges;
        public string? ReasoningEffort;
        public bool DryRun;
        public bool ShowLeaderboardOnly;
        public bool ShowJudgeLeaderboardOnly;
        public bool RetryFailed;
        public bool SyntaxOnly;
        public bool AdaptationSessionPilot;
        public string AdaptationModel = "grok-4.5";
        public int? TargetRuntimeMinutesOverride;
        public string? JudgeModel;
        public string? JudgeModel2;
        public string? VideoModel;
        public double AdaptationJudgeTemperature;
        public bool AdaptationClipShotPlan;
        public bool AdaptationDualAttachClipPlan;
        public bool AdaptationDualAttachAll = true;
        public bool RefreshDashboard;
        public bool ReviewPrompt;
        public List<string>? ReviewModels;
        public bool SidecarPilot;
        public string? SidecarPilotModel;
        public string? ValidateSidecarDirectory;
        public double SamplingTemperature = 0.2;
        public bool BypassCache;
        public bool AllowDirty;
        public bool UseSharedCache = true;
        public string SharedCacheUser = Environment.GetEnvironmentVariable("PTM_BENCHMARK_CACHE_USER") ?? "benchmark";
        public string SharedCacheVisibility = Environment.GetEnvironmentVariable("PTM_BENCHMARK_CACHE_VISIBILITY") ?? "Forkable";

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                if (TryParsePilotArg(args, ref i, o)) continue;
                if (TryParsePathArg(args, ref i, o)) continue;
                if (TryParseCacheArg(args, ref i, o)) continue;
                TryParseModeArg(args, ref i, o);
            }
            return o;
        }
    }

    private static bool Flag(string arg, string name) =>
        arg.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool TakeValue(string[] args, ref int i, out string value)
    {
        if (i + 1 >= args.Length)
        {
            value = "";
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryParsePilotArg(string[] args, ref int i, CliOptions o)
    {
        var arg = args[i];
        if (Flag(arg, "--adaptation-session-pilot")) { o.AdaptationSessionPilot = true; return true; }
        if (Flag(arg, "--model") && TakeValue(args, ref i, out var model)) { o.AdaptationModel = model.Trim(); return true; }
        if (Flag(arg, "--target-runtime-minutes") && TakeValue(args, ref i, out var trmRaw))
        {
            if (int.TryParse(trmRaw, out var trm) && trm > 0)
                o.TargetRuntimeMinutesOverride = trm;
            return true;
        }
        if (Flag(arg, "--judge-model") && TakeValue(args, ref i, out var jm)) { o.JudgeModel = jm.Trim(); return true; }
        if (Flag(arg, "--judge-model-2") && TakeValue(args, ref i, out var jm2)) { o.JudgeModel2 = jm2.Trim(); return true; }
        if (Flag(arg, "--video-model") && TakeValue(args, ref i, out var vm)) { o.VideoModel = vm.Trim(); return true; }
        if (Flag(arg, "--judge-temperature") && TakeValue(args, ref i, out var jtRaw))
        {
            if (double.TryParse(jtRaw, out var jt)) o.AdaptationJudgeTemperature = jt;
            return true;
        }
        if (Flag(arg, "--clip-shot-plan")) { o.AdaptationClipShotPlan = true; return true; }
        if (Flag(arg, "--dual-attach-clip-plan")) { o.AdaptationDualAttachClipPlan = true; return true; }
        if (Flag(arg, "--dual-attach-all")) { o.AdaptationDualAttachAll = true; return true; }
        if (Flag(arg, "--chained-only")) { o.AdaptationDualAttachAll = false; return true; }
        return false;
    }

    private static bool TryParsePathArg(string[] args, ref int i, CliOptions o)
    {
        var arg = args[i];
        if (Flag(arg, "--book") && TakeValue(args, ref i, out var book)) { o.BookPath = book; return true; }
        if (Flag(arg, "--suite") && TakeValue(args, ref i, out var suite)) { o.SuiteDir = suite; return true; }
        if (Flag(arg, "--out") && TakeValue(args, ref i, out var outDir)) { o.OutDir = outDir; return true; }
        if (Flag(arg, "--book-slug") && TakeValue(args, ref i, out var slug)) { o.BookSlug = slug; return true; }
        if (Flag(arg, "--models") && TakeValue(args, ref i, out var models))
        {
            o.RequestedModels = models.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            return true;
        }
        if (Flag(arg, "--judges") && TakeValue(args, ref i, out var judges))
        {
            o.RequestedJudges = judges.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            return true;
        }
        if (Flag(arg, "--reasoning-effort") && TakeValue(args, ref i, out var effort)) { o.ReasoningEffort = effort.Trim(); return true; }
        return false;
    }

    private static bool TryParseCacheArg(string[] args, ref int i, CliOptions o)
    {
        var arg = args[i];
        if (Flag(arg, "--temperature"))
        {
            if (i + 1 < args.Length
                && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTemperature)
                && parsedTemperature is >= 0 and <= 2)
                o.SamplingTemperature = parsedTemperature;
            return true;
        }
        if (Flag(arg, "--no-cache")) { o.BypassCache = true; return true; }
        if (Flag(arg, "--allow-dirty")) { o.AllowDirty = true; return true; }
        if (Flag(arg, "--no-shared-cache")) { o.UseSharedCache = false; return true; }
        if (Flag(arg, "--cache-user") && TakeValue(args, ref i, out var user)) { o.SharedCacheUser = user.Trim(); return true; }
        if (Flag(arg, "--cache-visibility") && TakeValue(args, ref i, out var vis)) { o.SharedCacheVisibility = vis.Trim(); return true; }
        return false;
    }

    private static bool TryParseModeArg(string[] args, ref int i, CliOptions o)
    {
        var arg = args[i];
        if (Flag(arg, "--dry-run")) { o.DryRun = true; return true; }
        if (Flag(arg, "--leaderboard")) { o.ShowLeaderboardOnly = true; return true; }
        if (Flag(arg, "--judge-leaderboard")) { o.ShowJudgeLeaderboardOnly = true; return true; }
        if (Flag(arg, "--retry-failed") || Flag(arg, "--resume")) { o.RetryFailed = true; return true; }
        if (Flag(arg, "--syntax-only") || Flag(arg, "--regrade")) { o.SyntaxOnly = true; return true; }
        if (Flag(arg, "--refresh-dashboard")) { o.RefreshDashboard = true; return true; }
        if (Flag(arg, "--review-prompt")) { o.ReviewPrompt = true; return true; }
        if (Flag(arg, "--review-models") && TakeValue(args, ref i, out var models))
        {
            o.ReviewModels = models.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            return true;
        }
        if (Flag(arg, "--sidecar-pilot")) { o.SidecarPilot = true; return true; }
        if (Flag(arg, "--sidecar-pilot-model") && TakeValue(args, ref i, out var spm)) { o.SidecarPilotModel = spm.Trim(); return true; }
        if (Flag(arg, "--validate-sidecar-pilot") && TakeValue(args, ref i, out var dir)) { o.ValidateSidecarDirectory = dir.Trim(); return true; }
        return false;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" 🎬 Film Studio — Screenplay Generation & Blind Peer-Evaluation Benchmark ");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();
    }

    private static async Task<int> RunParsedAsync(CliOptions o)
    {
        var (chat, workspaceRoot) = BuildServices();
        Console.WriteLine($"📂 Workspace root: {workspaceRoot}");

        if (await TryRunAdaptationPilotAsync(o, workspaceRoot).ConfigureAwait(false) is { } pilotCode)
            return pilotCode;

        var historyFilePath = Path.Combine(workspaceRoot, "evals", "benchmark_history.json");
        var historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        if (await BackfillLegacyPromptRevisionsAsync(historyStore, workspaceRoot).ConfigureAwait(false))
            BenchmarkHistoryStore.SaveHistory(historyStore, historyFilePath);

        if (await TryRunReviewPromptAsync(o, chat, workspaceRoot).ConfigureAwait(false) is { } reviewCode)
            return reviewCode;
        if (await TryRunSidecarPilotAsync(o, chat, workspaceRoot).ConfigureAwait(false) is { } sidecarCode)
            return sidecarCode;
        if (await TryValidateSidecarAsync(o).ConfigureAwait(false) is { } validateCode)
            return validateCode;

        if (o.ShowLeaderboardOnly) { PrintHistoricalLeaderboard(historyStore); return 0; }
        if (o.ShowJudgeLeaderboardOnly) { PrintJudgeLeaderboard(historyStore); return 0; }
        if (await TryRefreshDashboardAsync(o, historyStore, workspaceRoot).ConfigureAwait(false) is { } dashCode)
            return dashCode;
        if (o.SyntaxOnly)
        {
            await RegradeSyntaxOnlyAsync(historyFilePath, workspaceRoot);
            return 0;
        }

        return await RunBenchmarkAsync(o, chat, workspaceRoot, historyFilePath).ConfigureAwait(false);
    }

    private static async Task<int?> TryRunAdaptationPilotAsync(CliOptions o, string workspaceRoot)
    {
        if (!o.AdaptationSessionPilot) return null;
        if (string.IsNullOrWhiteSpace(o.BookPath) || !File.Exists(o.BookPath))
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
        var pilotBookText = await File.ReadAllTextAsync(o.BookPath);
        var pilotRuntimeMinutes = ResolveTargetRuntimeMinutes(pilotBookText, o.TargetRuntimeMinutesOverride);
        Console.WriteLine(
            $"⏱️  Target runtime {pilotRuntimeMinutes} min " +
            DescribeRuntimeSource(pilotBookText, o.TargetRuntimeMinutesOverride));
        return await AdaptationSessionPilot.RunAsync(
            o.BookPath, o.BookSlug, o.AdaptationModel, pilotRuntimeMinutes, workspaceRoot, pilotPromptRevision, CancellationToken.None,
            o.JudgeModel, o.SamplingTemperature, o.AdaptationJudgeTemperature, o.AdaptationClipShotPlan,
            o.AdaptationDualAttachClipPlan, o.AdaptationDualAttachAll, o.JudgeModel2, o.VideoModel);
    }

    private static async Task<int?> TryRunReviewPromptAsync(CliOptions o, IChatClient chat, string workspaceRoot)
    {
        if (!o.ReviewPrompt) return null;
        var models = o.ReviewModels is { Count: > 0 }
            ? o.ReviewModels
            : new List<string> { "gpt-5.6-terra", "grok-4.5" };
        return await PromptImprovementReview.RunAsync(workspaceRoot, chat, models);
    }

    private static async Task<int?> TryRunSidecarPilotAsync(CliOptions o, IChatClient chat, string workspaceRoot)
    {
        if (!o.SidecarPilot) return null;
        if (string.IsNullOrWhiteSpace(o.BookPath))
        {
            Console.Error.WriteLine("--sidecar-pilot requires --book <path/to/book.txt>.");
            return 1;
        }
        return await SidecarPlanningPilot.RunAsync(workspaceRoot, o.BookPath, o.SidecarPilotModel ?? "grok-4.5", chat);
    }

    private static async Task<int?> TryValidateSidecarAsync(CliOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ValidateSidecarDirectory)) return null;
        var validation = await SidecarArtifactValidator.ValidateDirectoryAsync(o.ValidateSidecarDirectory);
        Console.WriteLine($"🧪 Validation: {validation["status"]} ({validation["summary"]?["failure_count"]} repair target(s))");
        Console.WriteLine($"📄 Report: {Path.Combine(o.ValidateSidecarDirectory, "validation_report.json")}");
        return validation["status"]?.GetValue<string>() == "passed" ? 0 : 2;
    }

    private static async Task<int?> TryRefreshDashboardAsync(
        CliOptions o, HistoricalStoreContainer historyStore, string workspaceRoot)
    {
        if (!o.RefreshDashboard) return null;
        var (revOk, revision, _) = await TryGetCommittedPromptRevisionAsync(workspaceRoot).ConfigureAwait(false);
        var currentPromptCommit = revOk ? revision : null;
        var dashboardHtml = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, currentPromptCommit);
        var dashboardFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
        await File.WriteAllTextAsync(dashboardFile, dashboardHtml);
        Console.WriteLine($"✅ Dashboard refreshed: {Path.GetFullPath(dashboardFile)}");
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(
        CliOptions o, IChatClient chat, string workspaceRoot, string historyFilePath)
    {
        var (surfaceOk, promptRevision, promptError) = await TryGetCommittedStage1SurfaceAsync(workspaceRoot, o.AllowDirty).ConfigureAwait(false);
        if (!surfaceOk)
        {
            await Console.Error.WriteLineAsync($"❌ Benchmark not started: {promptError}");
            await Console.Error.WriteLineAsync("   Commit Stage‑1 prompts and host/PageToMovie.Adaptation/, then run again.");
            await Console.Error.WriteLineAsync("   (Local experiments only: pass --allow-dirty to skip this gate.)");
            return 1;
        }

        var adaptationVersion = PageToMovie.Adaptation.AdaptationVersion.Current;
        Console.WriteLine($"🔖 Prompt revision: {promptRevision}  ·  Adaptation version: {adaptationVersion}");

        o.OutDir ??= Path.Combine(workspaceRoot, "evals", "results", $"screenplay_benchmark_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(o.OutDir);

        if (!o.DryRun && !chat.IsConfigured)
        {
            Console.WriteLine("⚠️  No provider API key found in the environment (XAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY).");
            Console.WriteLine("   Generation and peer-judging will fall back to mock data. Pass --dry-run to silence this warning.");
        }

        var bookSuiteFiles = ResolveBookSuiteFiles(o, workspaceRoot);
        if (bookSuiteFiles.Count > 0)
            return await RunSuiteAsync(o, bookSuiteFiles, chat, workspaceRoot, historyFilePath, promptRevision, adaptationVersion).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(o.BookPath) || !File.Exists(o.BookPath))
        {
            Console.WriteLine("❌ Error: Book file not found. Provide --book <path/to/book.txt> or --suite <dir>.");
            return 1;
        }

        o.BookSlug ??= Path.GetFileNameWithoutExtension(o.BookPath).ToLowerInvariant();
        await RunSingleBookBenchmarkAsync(
            o.BookPath, o.BookSlug, o.OutDir, o.RequestedModels, o.RequestedJudges, o.DryRun, o.RetryFailed,
            historyFilePath, chat, workspaceRoot, promptRevision, adaptationVersion, o.ReasoningEffort,
            o.SamplingTemperature, o.BypassCache, o.AdaptationJudgeTemperature, o.UseSharedCache,
            o.SharedCacheUser, o.SharedCacheVisibility, o.TargetRuntimeMinutesOverride);

        var historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        var html = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, promptRevision);
        var dashFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
        await File.WriteAllTextAsync(dashFile, html);
        Console.WriteLine($"   🌐 Interactive HTML Dashboard: {Path.GetFullPath(dashFile)}");
        return 0;
    }

    private static List<string> ResolveBookSuiteFiles(CliOptions o, string workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(o.SuiteDir) && Directory.Exists(o.SuiteDir))
            return Directory.GetFiles(o.SuiteDir, "*.txt", SearchOption.TopDirectoryOnly).ToList();
        if (string.IsNullOrWhiteSpace(o.BookPath))
            return LocateDefaultSuiteBooks(workspaceRoot);
        return new List<string>();
    }

    private static async Task<int> RunSuiteAsync(
        CliOptions o, List<string> bookSuiteFiles, IChatClient chat, string workspaceRoot,
        string historyFilePath, string promptRevision, string adaptationVersion)
    {
        Console.WriteLine($"📚 Running Default 5-Book Evaluation Suite across {bookSuiteFiles.Count} stories...");
        foreach (var file in bookSuiteFiles)
        {
            var slug = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            await RunSingleBookBenchmarkAsync(
                file, slug, o.OutDir!, o.RequestedModels, o.RequestedJudges, o.DryRun, o.RetryFailed,
                historyFilePath, chat, workspaceRoot, promptRevision, adaptationVersion, o.ReasoningEffort,
                o.SamplingTemperature, o.BypassCache, o.AdaptationJudgeTemperature, o.UseSharedCache,
                o.SharedCacheUser, o.SharedCacheVisibility, o.TargetRuntimeMinutesOverride);
        }

        var historyStore = BenchmarkHistoryStore.LoadHistory(historyFilePath);
        var dashboardHtml = HtmlDashboardGenerator.GenerateHtmlDashboard(historyStore, null, promptRevision);
        var dashboardFile = Path.Combine(workspaceRoot, "evals", "benchmark_dashboard.html");
        await File.WriteAllTextAsync(dashboardFile, dashboardHtml);
        Console.WriteLine();
        Console.WriteLine($"✅ Multi-Book Suite Completed! Global Dashboard updated at:");
        Console.WriteLine($"   🌐 {Path.GetFullPath(dashboardFile)}");
        return 0;
    }
}
