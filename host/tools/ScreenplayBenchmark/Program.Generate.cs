using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Validation;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using CastPackageCrossCheck = PageToMovie.Adaptation.Validation.CastPackageCrossCheck;
using EngineConversionResult = PageToMovie.Engine.ProjectAdaptationConversionResult;
using EngineFountainMap = PageToMovie.Engine.BookToFountainConverter;
using VisionMetaStatus = PageToMovie.Engine.ProjectVisionMetaStatus;

namespace ScreenplayBenchmark;

public static partial class Program
{
    private static async Task GenerateOneCandidateAsync(
        string modelId,
        string bookPath,
        string bookSlug,
        string bookText,
        string screenplaysDir,
        string workspaceRoot,
        string promptRevision,
        string adaptationVersion,
        string effortSuffix,
        string temperatureKey,
        int generationRuntimeMinutes,
        string? reasoningEffort,
        double samplingTemperature,
        bool bypassCache,
        bool dryRun,
        IChatClient chat,
        string canonicalFallbackText,
        BookTextRegistryService? sharedCache,
        BookTextIdentity? sharedBook,
        string? sharedPromptHash,
        string sharedCacheUser,
        string sharedCacheVisibility,
        Dictionary<string, string> generatedScreenplays,
        Dictionary<string, ProjectVisionMeta.Document?> generatedVisionMeta,
        Dictionary<string, DeterministicSyntaxResult> deterministicResults,
        Dictionary<string, CastPackageCrossCheck.Report?> castPackageResults,
        Dictionary<string, string> generationFallbacks)
    {
        _ = sharedCacheVisibility;
        Console.Write($"  [Adaptation] Model '{modelId}'... ");

        var screenplayFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.fountain");
        var visionMetaFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.vision_meta.json");
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
            visionMetaSchema = ProjectVisionMeta.CurrentSchemaVersion,
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

        var (screenplayText, visionMeta) = await ResolveOrGenerateCandidateAsync(
            modelId, bookPath, bookText, generationRuntimeMinutes, reasoningEffort, samplingTemperature,
            bypassCache, dryRun, chat, canonicalFallbackText, diskCached, localCached,
            diskVisionMeta, localVisionMeta, sharedArtifact, sharedCache, sharedBook, sharedPromptHash,
            sharedCacheUser, sharedBehaviorVersions, cacheFile, cacheVisionMetaFile, workspaceRoot,
            bookSlug, generationFallbacks).ConfigureAwait(false);

        await File.WriteAllTextAsync(screenplayFile, screenplayText);
        await WriteVisionMetaAsync(visionMetaFile, visionMeta);
        generatedScreenplays[modelId] = screenplayText;
        generatedVisionMeta[modelId] = visionMeta;
        deterministicResults[modelId] = DeterministicSyntaxScorer.Evaluate(screenplayText);
        await EvaluateCastPackageAsync(
            modelId, screenplayText, bookText, screenplaysDir, effortSuffix, castPackageResults).ConfigureAwait(false);
    }

    private static async Task<(string Screenplay, ProjectVisionMeta.Document? VisionMeta)> ResolveOrGenerateCandidateAsync(
        string modelId,
        string bookPath,
        string bookText,
        int generationRuntimeMinutes,
        string? reasoningEffort,
        double samplingTemperature,
        bool bypassCache,
        bool dryRun,
        IChatClient chat,
        string canonicalFallbackText,
        string? diskCached,
        string? localCached,
        ProjectVisionMeta.Document? diskVisionMeta,
        ProjectVisionMeta.Document? localVisionMeta,
        DerivedBookArtifact? sharedArtifact,
        BookTextRegistryService? sharedCache,
        BookTextIdentity? sharedBook,
        string? sharedPromptHash,
        string sharedCacheUser,
        string sharedBehaviorVersions,
        string cacheFile,
        string cacheVisionMetaFile,
        string workspaceRoot,
        string bookSlug,
        Dictionary<string, string> generationFallbacks)
    {
        if (sharedArtifact is not null &&
            JsonSerializer.Deserialize<EngineConversionResult>(sharedArtifact.Content) is
                { Fountain.Length: > 0, VisionMeta: not null } sharedConversion)
        {
            Console.WriteLine($"(reused shared cache {sharedArtifact.ArtifactId})");
            return (sharedConversion.Fountain, sharedConversion.VisionMeta);
        }
        if (!bypassCache && diskCached is not null && diskVisionMeta is not null && !string.Equals(diskCached, canonicalFallbackText, StringComparison.Ordinal))
        {
            Console.WriteLine("(reused from disk cache)");
            return (diskCached, diskVisionMeta);
        }
        if (localCached is not null && localVisionMeta is not null && !string.Equals(localCached, canonicalFallbackText, StringComparison.Ordinal))
        {
            Console.WriteLine("(reused from local run folder)");
            return (localCached, localVisionMeta);
        }
        if (dryRun)
        {
            if (diskCached is not null) Console.Write("(ignoring stale fallback-poisoned cache) ");
            Console.WriteLine("(mock generated)");
            return (GenerateMockScreenplay(modelId), null);
        }
        return await GenerateLiveCandidateAsync(
            modelId, bookPath, bookText, generationRuntimeMinutes, reasoningEffort, samplingTemperature,
            chat, diskCached, sharedCache, sharedBook, sharedPromptHash, sharedCacheUser,
            sharedBehaviorVersions, cacheFile, cacheVisionMetaFile, workspaceRoot, bookSlug,
            generationFallbacks).ConfigureAwait(false);
    }

    private static async Task<(string Screenplay, ProjectVisionMeta.Document? VisionMeta)> GenerateLiveCandidateAsync(
        string modelId,
        string bookPath,
        string bookText,
        int generationRuntimeMinutes,
        string? reasoningEffort,
        double samplingTemperature,
        IChatClient chat,
        string? diskCached,
        BookTextRegistryService? sharedCache,
        BookTextIdentity? sharedBook,
        string? sharedPromptHash,
        string sharedCacheUser,
        string sharedBehaviorVersions,
        string cacheFile,
        string cacheVisionMetaFile,
        string workspaceRoot,
        string bookSlug,
        Dictionary<string, string> generationFallbacks)
    {
        if (diskCached is not null)
            Console.Write("(ignoring stale fallback-poisoned cache, retrying live) ");
        try
        {
            var budget = ResolveRateLimitSafeBudgetOverride(modelId);
            var adaptResult = await AdaptationService.ConvertAsync(
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
            var screenplayText = adaptResult.Fountain;
            var visionMeta = EngineFountainMap.MapVision(adaptResult.VisionMeta);
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
                return (screenplayText, visionMeta);
            }

            Console.WriteLine("DONE");
            if (visionMeta is null)
            {
                Console.WriteLine($"    · {conversion.VisionMetaError} Candidate package will not be cached.");
                return (screenplayText, visionMeta);
            }

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
            return (screenplayText, visionMeta);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            generationFallbacks[modelId] = ex.Message;
            return ($"FADE IN:\n\nINT. ERROR - DAY\n\n[Adaptation failed for {modelId}: {ex.Message}]\n\nFADE OUT.", null);
        }
    }

    private static async Task EvaluateCastPackageAsync(
        string modelId,
        string screenplayText,
        string bookText,
        string screenplaysDir,
        string effortSuffix,
        Dictionary<string, CastPackageCrossCheck.Report?> castPackageResults)
    {
        var castSeedsFile = Path.Combine(screenplaysDir, $"{SanitizeFileName(modelId)}{effortSuffix}.cast_seeds.json");
        if (!File.Exists(castSeedsFile))
        {
            castPackageResults[modelId] = null;
            return;
        }
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

    private static async Task EvaluateOneJudgeAsync(
        string judgeModelId,
        List<string> realCandidates,
        Dictionary<string, string> generatedScreenplays,
        Dictionary<string, ProjectVisionMeta.Document?> generatedVisionMeta,
        string bookText,
        string generationSystemPrompt,
        string bookSlug,
        string workspaceRoot,
        string promptRevision,
        string adaptationVersion,
        string effortSuffix,
        string temperatureKey,
        string judgeTemperatureKey,
        bool bypassCache,
        bool retryFailed,
        bool dryRun,
        IChatClient chat,
        double judgeTemperature,
        string? reasoningEffort,
        string screenplaysHash,
        Random random,
        Dictionary<string, JudgeEvaluationPayload> judgeEvaluations)
    {
        Console.Write($"  [Peer Judge] Model '{judgeModelId}'... ");
        if (realCandidates.Count == 0)
        {
            Console.WriteLine("(no real candidates to judge — all generations fell back)");
            judgeEvaluations[judgeModelId] = GenerateMockJudgePayload(new Dictionary<string, string>(), judgeModelId);
            return;
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

        var judgeCacheFile = Path.Combine(workspaceRoot, "evals", "cache", bookSlug, $"judge_{judgeModelId}{effortSuffix}_{promptRevision}_{SanitizeFileName(adaptationVersion)}_temp{temperatureKey}_judgetemp{judgeTemperatureKey}.json");
        var cachedJudge = await TryLoadCachedJudgeAsync(judgeCacheFile, bypassCache, screenplaysHash).ConfigureAwait(false);
        var evalPayload = await ResolveJudgePayloadAsync(
            cachedJudge, retryFailed, dryRun, chat, anonMapping, anonScreenplays,
            bookText, generationSystemPrompt, judgeModelId, judgeTemperature, reasoningEffort,
            screenplaysHash, judgeCacheFile, bookSlug, workspaceRoot).ConfigureAwait(false);
        judgeEvaluations[judgeModelId] = DeAnonymizePayload(evalPayload, anonMapping);
    }

    private static async Task<JudgeEvaluationPayload?> TryLoadCachedJudgeAsync(
        string judgeCacheFile, bool bypassCache, string screenplaysHash)
    {
        if (bypassCache || !File.Exists(judgeCacheFile)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(judgeCacheFile);
            var loaded = JsonSerializer.Deserialize<JudgeEvaluationPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is not null && !loaded.IsMock && loaded.Evaluations.Count > 0 && loaded.Evaluations.All(e => e.OverallQualitativeScore >= 0.0)
                && loaded.RubricVersion == ScreenplayJudgmentRubric.RubricVersion
                && loaded.ScreenplaysHash == screenplaysHash)
                return loaded;
        }
        catch { /* Corrupt cache — re-evaluate */ }
        return null;
    }

    private static async Task<JudgeEvaluationPayload> ResolveJudgePayloadAsync(
        JudgeEvaluationPayload? cachedJudge,
        bool retryFailed,
        bool dryRun,
        IChatClient chat,
        Dictionary<string, string> anonMapping,
        Dictionary<string, string> anonScreenplays,
        string bookText,
        string generationSystemPrompt,
        string judgeModelId,
        double judgeTemperature,
        string? reasoningEffort,
        string screenplaysHash,
        string judgeCacheFile,
        string bookSlug,
        string workspaceRoot)
    {
        if (cachedJudge is not null && (!retryFailed || !cachedJudge.IsMock))
        {
            Console.WriteLine("DONE (cached live evaluation)");
            return cachedJudge;
        }
        if (dryRun)
        {
            Console.WriteLine("(mock evaluated)");
            return GenerateMockJudgePayload(anonMapping, judgeModelId);
        }
        if (!chat.IsConfigured)
        {
            Console.WriteLine("(no provider API key configured — mock evaluated)");
            return GenerateMockJudgePayload(anonMapping, judgeModelId);
        }
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
            var evalPayload = ParseJudgePayload(raw, anonMapping.Keys);
            evalPayload.IsMock = false;
            evalPayload.RubricVersion = ScreenplayJudgmentRubric.RubricVersion;
            evalPayload.ScreenplaysHash = screenplaysHash;
            Console.WriteLine("DONE");
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "evals", "cache", bookSlug));
            await File.WriteAllTextAsync(judgeCacheFile, JsonSerializer.Serialize(evalPayload, new JsonSerializerOptions { WriteIndented = true }));
            return evalPayload;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED ({ex.Message}) — falling back to mock evaluation (-1.0)");
            return GenerateMockJudgePayload(anonMapping, judgeModelId);
        }
    }
}
