using PageToMovie.Core.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Sort book page plates onto character seeds as design_reference_images.
/// Cast comes from Fountain (in-memory); plates persist to source/cast_seeds.json
/// and are mirrored into the blueprint when present.
/// Order of attack: cast <c>source_image_pages</c> → OCR name hits in <c>book_full.txt</c>
/// (neighbor art pages) → optional Grok vision on remaining art (early-stop at 3/character) →
/// heuristic fill for empties. pipeline_state.character_plates.sorted_by_character records completion.
/// </summary>
public sealed class CharacterBookPlateService
{
    private const string DesignReferenceImagesKey = "design_reference_images";
    private const string BookReferenceImagesKey = "book_reference_images";
    private const string CanonicalGivenNameKey = "canonical_given_name";
    private const string CharacterPrefix = "Character_";
    private const string DescriptionKey = "description";
    private const string CoverCategory = "cover";
    private const string SparseCategory = "sparse";
    private const string SourceImagePagesKey = "source_image_pages";
    private const string CharacterSeedTokensKey = "character_seed_tokens";
    private const string GlobalProductionVariablesKey = "global_production_variables";

    private readonly ProjectStore _projects;
    private readonly IVisionClient _vision;
    private readonly PlateRankClassifier? _plateRank;
    private readonly ILogger<CharacterBookPlateService> _log;

    public CharacterBookPlateService(
        ProjectStore projects,
        IVisionClient vision,
        ILogger<CharacterBookPlateService> log,
        PlateRankClassifier? plateRank = null)
    {
        _projects = projects;
        _vision = vision;
        _log = log;
        _plateRank = plateRank;
    }

    public async Task<PageToMovie.Core.Models.AttachCharacterPlatesResult> AttachAsync(
        string projectId,
        bool force = false,
        bool copyIntoAssets = true,
        string? onlyCharKey = null,
        bool useGrok = true,
        string? visionModel = null,
        int maxImages = 32,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        visionModel = string.IsNullOrWhiteSpace(visionModel)
            ? ProjectModelSelection.RequireVision(cfg, "Character book plates")
            : ProjectModelSelection.RequireExplicit(visionModel, ModelCapability.Vision, "Character book plates");

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var castSeedsPath = ScreenplayService.GetCastSeedsPath(_projects, projectId);
        var result = new PageToMovie.Core.Models.AttachCharacterPlatesResult();
        var platesState = _projects.GetCharacterPlatesState(projectId);

        if (TryCompleteIfAlreadySorted(force, onlyCharKey, platesState, result, onProgress))
            return result;

        // Prefer cast_seeds.json (full cast incl. silent leads); else Fountain
        var seeds = await TryLoadCastSeedsAsync(projectId, castSeedsPath, result, ct).ConfigureAwait(false);
        if (seeds is null)
            return result;
        if (seeds.Count == 0)
        {
            result.Reason = "no_character_seeds";
            onProgress?.Invoke("No cast seeds — build cast from screenplay first.");
            return result;
        }

        var inventoryAll = await LoadBookImageInventoryAsync(projectDir).ConfigureAwait(false);
        var inventory = inventoryAll.Where(r => !IsLikelyTextLayout(r)).ToList();
        if (inventory.Count == 0)
            return CompleteEmptyInventory(projectId, onlyCharKey, result);

        var cast = BuildCastHints(seeds, onlyCharKey);
        if (cast.Count == 0)
        {
            result.Reason = "no_on_screen_cast";
            return result;
        }

        var charsDir = _projects.GetCharactersDir(projectId);
        if (copyIntoAssets)
            Directory.CreateDirectory(charsDir);

        // scores[charKey] = list of (row, score)
        var scores = cast.ToDictionary(
            c => c.Key,
            _ => new List<(BookImageRow Row, double Score)>(),
            StringComparer.OrdinalIgnoreCase);

        var (fromPages, fromOcr, ocrPages) = await SeedPagesAndOcrAsync(
            scores, inventory, seeds, onlyCharKey, projectDir, onProgress, ct).ConfigureAwait(false);
        var method = BuildInitialMethod(fromPages, fromOcr);
        method = await ApplyVisionOrHeuristicAsync(
            useGrok, onlyCharKey, method, fromPages, fromOcr,
            inventory, scores, seeds, ocrPages, maxImages, cast, visionModel,
            result, onProgress, ct).ConfigureAwait(false);

        result.Method = method;

        // Drop plates that clearly belong to another cast member (e.g. BUSTER cover on Daddy)
        PurgeCrossCharacterNameCollisions(scores, seeds);
        // Drop animal plates from human seeds and human-looking dumps from animal seeds
        PurgeSpeciesMismatches(scores, seeds);

        // Exclusive assignment: each source image (and each page) to at most one character
        // → stops B0≈B2 duplicates and Mom/Dad sharing the same dog plate
        var assigned = AssignPlatesExclusively(scores, maxPerCharacter: 3);
        onProgress?.Invoke(
            $"Exclusive assign: {assigned.Sum(kv => kv.Value.Count)} unique plate(s) across {assigned.Count} character(s)");

        // Write top plates per character
        await AttachPlatesToSeedsAsync(
            seeds, onlyCharKey, force, assigned, projectDir, charsDir, copyIntoAssets,
            method, result, onProgress, ct).ConfigureAwait(false);

        // Persist plates to cast_seeds.json (Fountain remains story source of truth)
        await PersistCastSeedsAsync(castSeedsPath, seeds, ct).ConfigureAwait(false);

        await TryMirrorBlueprintAsync(projectDir, seeds);

        FinalizeAttachResult(projectId, onlyCharKey, platesState, cast.Count, method, result, onProgress);
        return result;
    }

    private static bool TryCompleteIfAlreadySorted(
        bool force,
        string? onlyCharKey,
        CharacterPlatesState platesState,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress)
    {
        if (!force &&
            string.IsNullOrWhiteSpace(onlyCharKey) &&
            platesState.SortedByCharacter)
        {
            result.Ok = true;
            result.AlreadySorted = true;
            result.SortedByCharacter = true;
            result.SortedAt = platesState.SortedAt;
            result.Reason = "already_sorted";
            result.Method = platesState.Method;
            onProgress?.Invoke($"Already sorted at {platesState.SortedAt} ({platesState.Method})");
            return true;
        }

        return false;
    }

    private async Task<JsonObject?> TryLoadCastSeedsAsync(
        string projectId,
        string castSeedsPath,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        CancellationToken ct)
    {
        try
        {
            return await LoadOrBuildCastSeedsAsync(projectId, castSeedsPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Reason = $"no_cast:{ex.Message}";
            return null;
        }
    }

    private PageToMovie.Core.Models.AttachCharacterPlatesResult CompleteEmptyInventory(
        string projectId,
        string? onlyCharKey,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result)
    {
        result.Reason = "no_illustrated_book_images";
        if (string.IsNullOrWhiteSpace(onlyCharKey))
        {
            _projects.MarkCharacterPlatesSorted(projectId, 0, method: "text_only");
            var after = _projects.GetCharacterPlatesState(projectId);
            result.SortedByCharacter = after.SortedByCharacter;
            result.SortedAt = after.SortedAt;
            result.Method = "text_only";
        }
        result.Ok = true;
        return result;
    }

    private static async Task<(int FromPages, int FromOcr, List<BookOcrPlateShortlist.PageText> OcrPages)> SeedPagesAndOcrAsync(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey,
        string projectDir,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Seed from AI cast source_image_pages first (high confidence)
        var fromPages = SeedScoresFromSourceImagePages(scores, inventory, seeds, onlyCharKey);
        if (fromPages > 0)
            onProgress?.Invoke($"Seeded {fromPages} plate hit(s) from cast source_image_pages…");

        // OCR text (book_full.txt already produced at import) → name hit → neighbor art pages
        var ocrPages = await BookOcrPlateShortlist.TryLoadAsync(projectDir, ct).ConfigureAwait(false);
        var fromOcr = 0;
        if (ocrPages.Count > 0)
        {
            fromOcr = SeedScoresFromOcrNeighbors(scores, inventory, seeds, ocrPages, onlyCharKey, maxPerCharacter: 3);
            if (fromOcr > 0)
                onProgress?.Invoke(
                    $"OCR text→art neighbors: {fromOcr} plate hit(s) from {BookOcrPlateShortlist.BookFullFileName}…");
        }
        else
        {
            onProgress?.Invoke("No book_full.txt OCR — skipping text→art neighbor shortlist");
        }

        return (fromPages, fromOcr, ocrPages);
    }

    private static string BuildInitialMethod(int fromPages, int fromOcr)
    {
        var methodParts = new List<string>();
        if (fromPages > 0) methodParts.Add(SourceImagePagesKey);
        if (fromOcr > 0) methodParts.Add("ocr_neighbor");
        return methodParts.Count > 0 ? string.Join("+", methodParts) : "heuristic";
    }

    private async Task<string> ApplyVisionOrHeuristicAsync(
        bool useGrok,
        string? onlyCharKey,
        string method,
        int fromPages,
        int fromOcr,
        List<BookImageRow> inventory,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        int maxImages,
        List<CharacterClassifyHint> cast,
        string visionModel,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var wantGrok = ComputeWantGrok(useGrok, onlyCharKey, onProgress);
        // If OCR (+ source pages) already filled every cast member to 3, skip vision API entirely
        var ocrComplete = IsOcrCastComplete(scores, cast);

        if (wantGrok && !ocrComplete)
            return await RunGrokVisionSortAsync(
                method, inventory, scores, seeds, ocrPages, maxImages, cast, visionModel,
                onlyCharKey, result, onProgress, ct).ConfigureAwait(false);
        if (wantGrok)
            return CompleteOcrSkipVision(method, onProgress);
        if (!ocrComplete)
            return await RunHeuristicSortAsync(
                method, fromPages, fromOcr, scores, inventory, seeds, onlyCharKey, onProgress, ct)
                .ConfigureAwait(false);
        return method;
    }

    private bool ComputeWantGrok(bool useGrok, string? onlyCharKey, Action<string>? onProgress)
    {
        var wantGrok = useGrok && _vision.IsConfigured && string.IsNullOrWhiteSpace(onlyCharKey);
        if (useGrok && !_vision.IsConfigured)
            onProgress?.Invoke("XAI_API_KEY missing — using page seeds / OCR / heuristic plate sort");
        return wantGrok;
    }

    private static bool IsOcrCastComplete(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast)
    {
        foreach (var c in cast)
        {
            if (!scores.TryGetValue(c.Key, out var list) || list.Count < 3)
                return false;
        }
        return true;
    }

    private async Task<string> RunGrokVisionSortAsync(
        string method,
        List<BookImageRow> inventory,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        int maxImages,
        List<CharacterClassifyHint> cast,
        string visionModel,
        string? onlyCharKey,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        method = string.IsNullOrEmpty(method) || method == "heuristic"
            ? "grok_vision"
            : method + "+grok_vision";
        var toScan = BuildVisionScanList(inventory, scores, seeds, ocrPages, Math.Clamp(maxImages, 4, 64));
        onProgress?.Invoke(
            $"Grok vision: classifying up to {toScan.Count} book image(s) for {cast.Count} character(s) (OCR shortlist first)…");
        result.ImagesClassified = 0;
        result.ImagesSkippedText = 0;

        await ClassifyVisionImagesAsync(toScan, scores, cast, visionModel, result, onProgress, ct)
            .ConfigureAwait(false);

        var emptyCast = CountEmptyCastMembers(scores, cast);
        if (emptyCast > 0)
        {
            onProgress?.Invoke(
                $"{emptyCast} cast member(s) still empty — filling from pages / hero heuristic…");
            method += "+heuristic_fill";
            await ApplyHeuristicScoresAsync(scores, inventory, seeds, onlyCharKey, onlyEmpty: true, heroOnly: false, ct)
                .ConfigureAwait(false);
        }

        return method;
    }

    private async Task ClassifyVisionImagesAsync(
        List<BookImageRow> toScan,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast,
        string visionModel,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        for (var i = 0; i < toScan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (CastScoresComplete(scores, cast, maxPerCharacter: 3))
            {
                onProgress?.Invoke(
                    $"All cast have {3} plate candidate(s) — stopping vision early ({result.ImagesClassified} image(s) scanned)");
                break;
            }

            var row = toScan[i];
            onProgress?.Invoke(
                $"Grok vision {i + 1}/{toScan.Count}: {Path.GetFileName(row.AbsPath)} (p{row.Page})…");
            await ClassifyOneVisionImageAsync(row, scores, cast, visionModel, result, onProgress, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ClassifyOneVisionImageAsync(
        BookImageRow row,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast,
        string visionModel,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        try
        {
            var cls = await _vision.ClassifyCharactersOnImageAsync(
                row.AbsPath, row.Page, cast, visionModel, ct);
            result.ImagesClassified++;

            if (cls.PageKind is "text_heavy" or "text")
            {
                result.ImagesSkippedText++;
                return;
            }

            // One page → one best cast match (avoids multiple cast claiming the same plate)
            ApplyBestVisionMatch(cls, scores, row);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vision classify failed for {File}", row.Name);
            onProgress?.Invoke($"  skip {row.Name}: {ex.Message}");
        }
    }

    private static void ApplyBestVisionMatch(
        CharacterPageClassification cls,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        BookImageRow row)
    {
        var bestMatch = PickBestMatch(cls);
        if (bestMatch is null) return;
        if (!scores.TryGetValue(bestMatch.Key, out var list)) return;
        if (list.Count >= 3) return; // already full
        list.Add((row, bestMatch.Confidence));
    }

    private static int CountEmptyCastMembers(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast)
    {
        var emptyCast = 0;
        foreach (var c in cast)
        {
            if (!scores.TryGetValue(c.Key, out var list) || list.Count == 0)
                emptyCast++;
        }
        return emptyCast;
    }

    private static string CompleteOcrSkipVision(string method, Action<string>? onProgress)
    {
        onProgress?.Invoke("OCR shortlist already filled cast (3 each) — skipping vision API");
        method += method.Contains("ocr", StringComparison.Ordinal) ? "" : "+ocr_neighbor";
        return method;
    }

    private async Task<string> RunHeuristicSortAsync(
        string method,
        int fromPages,
        int fromOcr,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        method = fromPages > 0 || fromOcr > 0 ? method + "+heuristic" : "heuristic";
        onProgress?.Invoke("Heuristic plate sort (OCR neighbors + cast pages + illustration ranking)…");
        await ApplyHeuristicScoresAsync(
                scores, inventory, seeds, onlyCharKey,
                onlyEmpty: fromPages > 0 || fromOcr > 0, ct: ct)
            .ConfigureAwait(false);
        return method;
    }

    private async Task AttachPlatesToSeedsAsync(
        JsonObject seeds,
        string? onlyCharKey,
        bool force,
        Dictionary<string, List<BookImageRow>> assigned,
        string projectDir,
        string charsDir,
        bool copyIntoAssets,
        string method,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        foreach (var (key, seedNode) in seeds.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await AttachPlatesToOneSeedAsync(
                key, seedNode, onlyCharKey, force, assigned, projectDir, charsDir,
                copyIntoAssets, method, result, onProgress, ct).ConfigureAwait(false);
        }
    }

    private async Task AttachPlatesToOneSeedAsync(
        string key,
        JsonNode? seedNode,
        string? onlyCharKey,
        bool force,
        Dictionary<string, List<BookImageRow>> assigned,
        string projectDir,
        string charsDir,
        bool copyIntoAssets,
        string method,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (onlyCharKey is { Length: > 0 } &&
            !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
            return;

        if (seedNode is not JsonObject seed)
            return;

        if (IsVoiceOnly(key, seed))
        {
            seed.Remove(DesignReferenceImagesKey);
            seed.Remove(BookReferenceImagesKey);
            CleanupStaleBookrefs(charsDir, key, keepCount: 0);
            result.CharactersSkipped++;
            result.AttachedByCharacter[key] = new List<string> { "(voice_only)" };
            return;
        }

        if (!force && HasExistingIllustratedPlates(seed, out var existingPaths))
        {
            result.CharactersSkipped++;
            result.AttachedByCharacter[key] = existingPaths;
            return;
        }

        assigned.TryGetValue(key, out var picks);
        picks ??= new List<BookImageRow>();

        ApplyAttachedSourcePages(seed, picks);

        var relPaths = await CopyPlatesAsync(projectDir, charsDir, key, picks, copyIntoAssets, ct).ConfigureAwait(false);
        CleanupStaleBookrefs(charsDir, key, keepCount: relPaths.Count);

        RecordAttachedPaths(seed, key, relPaths, picks, method, result, onProgress);
    }

    private static bool HasExistingIllustratedPlates(JsonObject seed, out List<string> paths)
    {
        paths = new List<string>();
        if (seed[DesignReferenceImagesKey] is not JsonArray existing)
            return false;
        if (existing.Count == 0)
            return false;
        paths = CollectIllustratedPlatePaths(existing);
        return paths.Count > 0;
    }

    private static List<string> CollectIllustratedPlatePaths(JsonArray existing)
    {
        var paths = new List<string>();
        foreach (var x in existing)
        {
            var s = x?.GetValue<string>() ?? "";
            if (s.Length > 0 && !ProjectStore.IsTextOnlyPlatePath(s))
                paths.Add(s);
        }
        return paths;
    }

    private static void ApplyAttachedSourcePages(JsonObject seed, List<BookImageRow> picks)
    {
        // Keep AI source_image_pages when present; else record pages we actually attached
        var priorPages = PagesForSeed(seed);
        var usedPages = picks.Where(p => p.Page > 0).Select(p => p.Page).Distinct().ToList();
        if (usedPages.Count > 0)
            seed[SourceImagePagesKey] = new JsonArray(usedPages.Select(p => (JsonNode)p).ToArray());
        else if (priorPages.Count > 0)
            seed[SourceImagePagesKey] = new JsonArray(priorPages.Select(p => (JsonNode)p).ToArray());
    }

    private void RecordAttachedPaths(
        JsonObject seed,
        string key,
        List<string> relPaths,
        List<BookImageRow> picks,
        string method,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress)
    {
        if (relPaths.Count == 0)
        {
            seed.Remove(DesignReferenceImagesKey);
            seed.Remove(BookReferenceImagesKey);
            result.CharactersSkipped++;
            result.AttachedByCharacter[key] = new List<string> { $"(none via {method})" };
            return;
        }

        seed[DesignReferenceImagesKey] = new JsonArray(
            relPaths.Select(r => (JsonNode)r).ToArray());
        seed[BookReferenceImagesKey] = new JsonArray(
            relPaths.Select(r => (JsonNode)r).ToArray());
        result.CharactersUpdated++;
        result.AttachedByCharacter[key] = relPaths;
        _log.LogInformation(
            "Attached {Count} book plate(s) to {Key} via {Method}",
            relPaths.Count, key, method);
        onProgress?.Invoke(
            $"{key}: {relPaths.Count} plate(s) pages=[{string.Join(",", picks.Select(p => p.Page))}]");
    }

    private static async Task PersistCastSeedsAsync(string castSeedsPath, JsonObject seeds, CancellationToken ct)
    {
        var castRoot = new JsonObject
        {
            ["schema_version"] = "cast_seeds.v1",
            [CharacterSeedTokensKey] = seeds,
        };
        TryBackupCastSeeds(castSeedsPath);

        Directory.CreateDirectory(Path.GetDirectoryName(castSeedsPath) ?? ".");
        await File.WriteAllTextAsync(
            castSeedsPath,
            castRoot.ToJsonString(JsonDefaults.Indented) + "\n",
            ct).ConfigureAwait(false);
    }

    private static void TryBackupCastSeeds(string castSeedsPath)
    {
        try
        {
            if (File.Exists(castSeedsPath))
            {
                var bak = castSeedsPath + $".bak_attach_plates_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(castSeedsPath, bak, overwrite: true);
            }
        }
        catch { /* ignore */ }
    }

    private void FinalizeAttachResult(
        string projectId,
        string? onlyCharKey,
        CharacterPlatesState platesState,
        int onScreenNeedPlates,
        string method,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress)
    {
        if (string.IsNullOrWhiteSpace(onlyCharKey))
            ApplyFullCastSortedState(projectId, onScreenNeedPlates, method, result, onProgress);
        else
            ApplySingleCharSortedState(platesState, result);

        result.Ok = result.CharactersUpdated > 0 || result.CharactersSkipped > 0;
        if (!result.Ok)
            result.Reason ??= "nothing_attached";
        onProgress?.Invoke(
            $"Done ({method}): updated={result.CharactersUpdated} skipped={result.CharactersSkipped}");
    }

    private void ApplyFullCastSortedState(
        string projectId,
        int onScreenNeedPlates,
        string method,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result,
        Action<string>? onProgress)
    {
        var anyPlates = result.CharactersUpdated > 0;
        // Only mark sorted when we actually attached plates (or cast is empty)
        if (anyPlates || onScreenNeedPlates == 0)
        {
            _projects.MarkCharacterPlatesSorted(projectId, result.CharactersUpdated, method: method);
            var after = _projects.GetCharacterPlatesState(projectId);
            result.SortedByCharacter = after.SortedByCharacter;
            result.SortedAt = after.SortedAt;
            return;
        }

        result.SortedByCharacter = false;
        result.Reason = "no_plates_attached";
        onProgress?.Invoke(
            "No book pictures attached — try again after Build cast, or upload pictures manually.");
    }

    private static void ApplySingleCharSortedState(
        CharacterPlatesState platesState,
        PageToMovie.Core.Models.AttachCharacterPlatesResult result)
    {
        result.SortedByCharacter = platesState.SortedByCharacter || result.CharactersUpdated > 0;
        result.SortedAt = platesState.SortedAt;
    }

    /// <summary>
    /// High-confidence scores from OCR name hits → neighboring art pages (book_full.txt).
    /// </summary>
    private static int SeedScoresFromOcrNeighbors(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        string? onlyCharKey,
        int maxPerCharacter)
    {
        var hits = 0;
        var byPage = inventory
            .Where(r => r.Page > 0 && !IsLikelyTextLayout(r))
            .GroupBy(r => r.Page)
            .ToDictionary(g => g.Key, g => RankIllustrationFirst(g).ToList());

        foreach (var (key, seedNode) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seedNode is not JsonObject seed) continue;
            if (IsVoiceOnly(key, seed)) continue;
            if (!scores.TryGetValue(key, out var list)) continue;

            var aliases = BookOcrPlateShortlist.AliasesForSeed(key, seed);
            var pages = BookOcrPlateShortlist.ShortlistArtPages(ocrPages, aliases, maxPerCharacter);
            foreach (var pg in pages)
            {
                if (!byPage.TryGetValue(pg, out var rows) || rows.Count == 0) continue;
                var row = rows[0];
                // Avoid duplicate page rows
                if (list.Any(x => x.Row.Page == pg ||
                                  x.Row.AbsPath.Equals(row.AbsPath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add((row, 0.93)); // high confidence: OCR name → art neighbor
                hits++;
            }
        }
        return hits;
    }

    /// <summary>Prefer OCR-shortlisted art pages, then remaining illustrated inventory.</summary>
    private static List<BookImageRow> BuildVisionScanList(
        List<BookImageRow> inventory,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        int maxImages)
    {
        var priorityPages = CollectPriorityArtPages(scores, seeds, ocrPages);

        var art = inventory.Where(r => !IsLikelyTextLayout(r)).ToList();
        var head = RankIllustrationFirst(art.Where(r => r.Page > 0 && priorityPages.Contains(r.Page)));
        var tail = RankIllustrationFirst(art.Where(r => r.Page <= 0 || !priorityPages.Contains(r.Page)));
        return head.Concat(tail)
            .GroupBy(r => r.AbsPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(maxImages)
            .ToList();
    }

    private static HashSet<int> CollectPriorityArtPages(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages)
    {
        var priorityPages = new HashSet<int>();
        if (ocrPages.Count == 0)
            return priorityPages;
        foreach (var (key, seedNode) in seeds)
            AddPriorityPagesForSeed(key, seedNode, scores, ocrPages, priorityPages);
        return priorityPages;
    }

    private static void AddPriorityPagesForSeed(
        string key,
        JsonNode? seedNode,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        HashSet<int> priorityPages)
    {
        if (seedNode is not JsonObject seed)
            return;
        if (IsVoiceOnly(key, seed))
            return;
        var have = scores.TryGetValue(key, out var list) ? list.Count : 0;
        if (have >= 3)
            return;
        var aliases = BookOcrPlateShortlist.AliasesForSeed(key, seed);
        foreach (var pg in BookOcrPlateShortlist.ShortlistArtPages(ocrPages, aliases, maxPlates: 6))
            priorityPages.Add(pg);
    }

    private static bool CastScoresComplete(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast,
        int maxPerCharacter)
    {
        foreach (var c in cast)
        {
            if (!scores.TryGetValue(c.Key, out var list) || list.Count < maxPerCharacter)
                return false;
        }
        return cast.Count > 0;
    }

    /// <summary>
    /// High-confidence plate hits from cast AI <c>source_image_pages</c> (page → book_images).
    /// </summary>
    private static int SeedScoresFromSourceImagePages(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey)
    {
        var hits = 0;
        foreach (var (key, seedNode) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seedNode is not JsonObject seed || IsVoiceOnly(key, seed)) continue;
            if (!scores.ContainsKey(key))
                scores[key] = new List<(BookImageRow, double)>();

            var pages = PagesForSeed(seed);
            if (pages.Count == 0) continue;
            var picks = RowsForPages(inventory, pages);
            foreach (var p in picks)
            {
                scores[key].Add((p, 0.92));
                hits++;
            }
        }
        return hits;
    }

    private static List<CharacterClassifyHint> BuildCastHints(JsonObject seeds, string? onlyCharKey)
    {
        var cast = new List<CharacterClassifyHint>();
        foreach (var (key, node) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (node is not JsonObject seed) continue;
            if (IsVoiceOnly(key, seed)) continue;
            var display = seed[CanonicalGivenNameKey]?.GetValue<string>()
                          ?? seed["voice_label"]?.GetValue<string>()
                          ?? key.Replace(CharacterPrefix, "").Replace('_', ' ');
            var desc = seed[DescriptionKey]?.GetValue<string>()
                       ?? seed["visual_lock"]?.GetValue<string>()
                       ?? "";
            cast.Add(new CharacterClassifyHint
            {
                Key = key,
                DisplayName = display,
                Description = desc,
            });
        }
        return cast;
    }

    private async Task ApplyHeuristicScoresAsync(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey,
        bool onlyEmpty = false,
        bool heroOnly = false,
        CancellationToken ct = default)
    {
        var index = 0;
        foreach (var (key, seedNode) in seeds)
        {
            if (TryBeginHeuristicSeed(
                    key, seedNode, onlyCharKey, scores, onlyEmpty, heroOnly, index,
                    out var seed, out var list, out var isHero))
            {
                var picks = await ResolveHeuristicPicksAsync(
                    inventory, key, seed, index, isHero, ct).ConfigureAwait(false);
                foreach (var p in picks.Where(r => !IsLikelyTextLayout(r)))
                    list.Add((p, isHero ? 0.5 : 0.4));
            }
            index++;
        }
    }

    private static bool TryBeginHeuristicSeed(
        string key,
        JsonNode? seedNode,
        string? onlyCharKey,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        bool onlyEmpty,
        bool heroOnly,
        int index,
        out JsonObject seed,
        out List<(BookImageRow Row, double Score)> list,
        out bool isHero)
    {
        seed = null!;
        list = null!;
        isHero = false;
        if (onlyCharKey is { Length: > 0 } &&
            !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (seedNode is not JsonObject seedObj || IsVoiceOnly(key, seedObj))
            return false;
        seed = seedObj;
        if (!scores.TryGetValue(key, out list!))
        {
            list = new List<(BookImageRow, double)>();
            scores[key] = list;
        }
        if (onlyEmpty && list.Count > 0)
            return false;

        var desc = (seed[DescriptionKey]?.GetValue<string>() ?? "").ToLowerInvariant();
        // Hero = first cast or primary animal species — not humans whose text mentions the animal medium
        isHero = index == 0 ||
                 CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                     key, ageBand: "", description: desc, visualLock: "", animalWord: "dog") ||
                 CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                     key, ageBand: "", description: desc, visualLock: "", animalWord: "cat");
        // heroOnly: never invent Mom/Dad plates from shared early covers
        return !heroOnly || isHero;
    }

    private async Task<List<BookImageRow>> ResolveHeuristicPicksAsync(
        List<BookImageRow> inventory,
        string key,
        JsonObject seed,
        int index,
        bool isHero,
        CancellationToken ct)
    {
        var pages = PagesForSeed(seed);
        if (pages.Count == 0)
        {
            // Non-hero: only filename/name hits — never dump generic early pages onto Mom/Dad
            return await HeuristicPicksRankedAsync(inventory, key, seed, index, nameHitsOnly: !isHero, ct)
                .ConfigureAwait(false);
        }

        var picks = RowsForPages(inventory, pages);
        if (picks.Count > 0)
            return picks;
        return await HeuristicPicksRankedAsync(inventory, key, seed, index, nameHitsOnly: !isHero, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Prefer primary_character_key when confident; else single highest-confidence match.
    /// Never multi-assign the same page to several cast members.
    /// </summary>
    private static CharacterPageMatch? PickBestMatch(CharacterPageClassification cls)
    {
        const double minConf = 0.55;
        var viable = cls.Matches.Where(m => m.Confidence >= minConf).ToList();
        if (viable.Count == 0) return null;

        if (cls.PrimaryCharacterKey is { Length: > 0 } primary)
        {
            var prim = viable.FirstOrDefault(m =>
                string.Equals(m.Key, primary, StringComparison.OrdinalIgnoreCase));
            if (prim is not null)
                return new CharacterPageMatch
                {
                    Key = prim.Key,
                    Confidence = Math.Min(1.0, prim.Confidence + 0.12),
                    Notes = prim.Notes,
                };
        }

        return viable.OrderByDescending(m => m.Confidence).First();
    }

    /// <summary>
    /// Greedy exclusive plate assignment ranked by score.
    /// Dedupes by content hash, absolute path, and page number so B0/B2 cannot be the same cover.
    /// Each image goes to at most one character so Mom/Dad never share identical plates.
    /// Characters with fewer candidate pages are preferred so supporting cast is not wiped by the hero.
    /// </summary>
    private static Dictionary<string, List<BookImageRow>> AssignPlatesExclusively(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        int maxPerCharacter)
    {
        // Fewer unique pages ⇒ higher priority (supporting cast with one page beats hero with many)
        var uniquePageCount = scores.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(x => x.Row.Page).Where(p => p > 0).Distinct().Count(),
            StringComparer.OrdinalIgnoreCase);

        var flat = FlattenScoredPlates(scores)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => uniquePageCount.GetValueOrDefault(x.Key, 99)) // scarce pages first
            .ThenBy(x => IllustrationScore(x.Row))
            .ToList();

        var claimed = new ExclusivePlateClaims();
        var assigned = new Dictionary<string, List<BookImageRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, row, _, fp) in flat)
            TryAssignExclusivePlate(key, row, fp, maxPerCharacter, claimed, assigned);

        // No last-resort sharing of claimed plates across cast (that put the dog cover on Dad).
        // Empty book refs are better than wrong species / wrong character.
        return assigned;
    }

    private static List<(string Key, BookImageRow Row, double Score, string Fingerprint)> FlattenScoredPlates(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores)
    {
        var flat = new List<(string Key, BookImageRow Row, double Score, string Fingerprint)>();
        foreach (var (key, list) in scores)
        {
            foreach (var (row, score) in list)
            {
                if (IsLikelyTextLayout(row)) continue;
                flat.Add((key, row, score, ContentFingerprint(row)));
            }
        }
        return flat;
    }

    private sealed class ExclusivePlateClaims
    {
        public HashSet<string> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> Pages { get; } = new();
        public Dictionary<string, HashSet<int>> PerCharPages { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void TryAssignExclusivePlate(
        string key,
        BookImageRow row,
        string fp,
        int maxPerCharacter,
        ExclusivePlateClaims claimed,
        Dictionary<string, List<BookImageRow>> assigned)
    {
        if (!assigned.TryGetValue(key, out var picks))
        {
            picks = new List<BookImageRow>();
            assigned[key] = picks;
            claimed.PerCharPages[key] = new HashSet<int>();
        }
        if (picks.Count >= maxPerCharacter)
            return;
        if (PlateAlreadyClaimed(row, fp, key, claimed))
            return;

        picks.Add(row);
        ClaimExclusivePlate(row, fp, key, claimed);
    }

    private static bool PlateAlreadyClaimed(
        BookImageRow row, string fp, string key, ExclusivePlateClaims claimed)
    {
        if (fp.Length > 0 && claimed.Fingerprints.Contains(fp))
            return true;
        if (claimed.Paths.Contains(row.AbsPath))
            return true;
        // Same page number → almost always same art (cover embed + cover render)
        if (row.Page > 0 && claimed.Pages.Contains(row.Page))
            return true;
        return row.Page > 0 && claimed.PerCharPages[key].Contains(row.Page);
    }

    private static void ClaimExclusivePlate(
        BookImageRow row, string fp, string key, ExclusivePlateClaims claimed)
    {
        if (fp.Length > 0)
            claimed.Fingerprints.Add(fp);
        claimed.Paths.Add(row.AbsPath);
        if (row.Page <= 0)
            return;
        claimed.Pages.Add(row.Page);
        claimed.PerCharPages[key].Add(row.Page);
    }

    /// <summary>
    /// If a plate filename strongly matches another cast member's distinctive name
    /// (e.g. BUSTER in the path) and not this cast member's name, drop it from scores.
    /// </summary>
    private static void PurgeCrossCharacterNameCollisions(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds)
    {
        var tokensByKey = BuildDistinctiveNameTokens(seeds);
        foreach (var (key, list) in scores.ToList())
            scores[key] = KeepPlatesNotOwnedByOthers(key, list, tokensByKey);
    }

    private static Dictionary<string, List<string>> BuildDistinctiveNameTokens(JsonObject seeds)
    {
        var tokensByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, node) in seeds)
        {
            if (node is not JsonObject seed || IsVoiceOnly(key, seed))
                continue;
            tokensByKey[key] = DistinctiveTokensForSeed(key, seed);
        }
        return tokensByKey;
    }

    private static List<string> DistinctiveTokensForSeed(string key, JsonObject seed)
    {
        var toks = new List<string>();
        var suffix = key.Replace(CharacterPrefix, "", StringComparison.OrdinalIgnoreCase);
        if (suffix.Length >= 3 && !IsGenericRoleToken(suffix))
            toks.Add(suffix);
        var given = seed[CanonicalGivenNameKey]?.GetValue<string>() ?? "";
        if (given.Length >= 3 && !IsGenericRoleToken(given))
            toks.Add(given);
        return toks;
    }

    private static List<(BookImageRow Row, double Score)> KeepPlatesNotOwnedByOthers(
        string key,
        List<(BookImageRow Row, double Score)> list,
        Dictionary<string, List<string>> tokensByKey)
    {
        var own = tokensByKey.GetValueOrDefault(key) ?? new List<string>();
        var kept = new List<(BookImageRow Row, double Score)>();
        foreach (var (row, score) in list)
        {
            var name = row.Name + " " + (row.PathRel ?? "");
            if (!PlateHitsOtherCharacter(name, key, own, tokensByKey))
                kept.Add((row, score));
        }
        return kept;
    }

    private static bool PlateHitsOtherCharacter(
        string name,
        string key,
        List<string> own,
        Dictionary<string, List<string>> tokensByKey)
    {
        foreach (var (otherKey, otherToks) in tokensByKey)
        {
            if (HitsOtherExclusiveToken(name, key, otherKey, otherToks, own))
                return true;
        }
        return false;
    }

    private static bool HitsOtherExclusiveToken(
        string name,
        string key,
        string otherKey,
        List<string> otherToks,
        List<string> own)
    {
        if (string.Equals(otherKey, key, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var t in otherToks)
        {
            if (TokenHitsOtherNotSelf(name, t, own))
                return true;
        }
        return false;
    }

    private static bool TokenHitsOtherNotSelf(string name, string t, List<string> own)
    {
        if (t.Length < 3)
            return false;
        if (!name.Contains(t, StringComparison.OrdinalIgnoreCase))
            return false;
        // Only purge if this plate does not also name *us*
        var hitsSelf = own.Any(o => o.Length >= 3 &&
            name.Contains(o, StringComparison.OrdinalIgnoreCase));
        return !hitsSelf;
    }

    private static void PurgeSpeciesMismatches(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds)
    {
        ClassifySeedSpecies(seeds, out var animalKeys, out var humanKeys);
        if (animalKeys.Count == 0 || humanKeys.Count == 0) return;

        var animalNameHits = CollectAnimalNameHits(seeds, animalKeys);
        foreach (var hk in humanKeys)
        {
            if (!scores.TryGetValue(hk, out var list) || list.Count == 0) continue;
            scores[hk] = list.Where(x => !PlateFilenameHitsAnimalName(x, animalNameHits)).ToList();
        }
    }

    private static void ClassifySeedSpecies(
        JsonObject seeds,
        out HashSet<string> animalKeys,
        out HashSet<string> humanKeys)
    {
        animalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        humanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, node) in seeds)
        {
            if (node is not JsonObject seed || IsVoiceOnly(key, seed)) continue;
            var desc = seed[DescriptionKey]?.GetValue<string>() ?? "";
            var vlock = seed["visual_lock"]?.GetValue<string>() ?? "";
            if (IsAnimalSeed(key, desc, vlock))
                animalKeys.Add(key);
            else if (CharacterVisualTextScrubber.IsHumanAdultCharacter(key, "", desc, vlock) ||
                     IsHumanishSeed(key, desc))
                humanKeys.Add(key);
        }
    }

    private static List<string> CollectAnimalNameHits(JsonObject seeds, HashSet<string> animalKeys)
    {
        var animalNameHits = new List<string>();
        foreach (var ak in animalKeys)
        {
            var suffix = ak.Replace(CharacterPrefix, "", StringComparison.OrdinalIgnoreCase);
            if (suffix.Length >= 3) animalNameHits.Add(suffix);
            if (seeds[ak] is JsonObject seed)
            {
                var given = seed[CanonicalGivenNameKey]?.GetValue<string>() ?? "";
                if (given.Length >= 3) animalNameHits.Add(given);
            }
        }
        animalNameHits.AddRange(new[] { "dog", "puppy", "cat", "kitten", "fox", "bear", "bunny", "rabbit" });
        return animalNameHits;
    }

    private static bool PlateFilenameHitsAnimalName(
        (BookImageRow Row, double Score) item, List<string> animalNameHits)
    {
        var n = item.Row.Name + " " + (item.Row.PathRel ?? "");
        foreach (var t in animalNameHits)
        {
            if (t.Length >= 3 && n.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsGenericRoleToken(string t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        return t is "mom" or "dad" or "daddy" or "mum" or "mother" or "father" or "parent"
            or "narrator" or "boy" or "girl" or "man" or "woman" or "child" or "kid";
    }

    private static bool IsAnimalSeed(string key, string desc, string vlock) =>
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "dog") ||
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "cat") ||
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "fox") ||
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "bear") ||
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "bunny") ||
        CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", desc, vlock, "rabbit");

    private static bool IsHumanishSeed(string key, string desc)
    {
        var blob = $"{key} {desc}";
        return CommonRegex.IsMatch(
            blob,
            @"\b(man|woman|mother|father|mom|dad|daddy|mum|parent|boy|girl|human|adult)\b",
            RegexOptions.IgnoreCase);
    }

    private static string ContentFingerprint(BookImageRow row)
    {
        try
        {
            if (!File.Exists(row.AbsPath)) return "";
            using var fs = File.OpenRead(row.AbsPath);
            // Hash first 256KB + length — enough to catch identical embeds cheaply
            var buf = new byte[256 * 1024];
            var n = fs.Read(buf, 0, buf.Length);
            // CA1850: static HashData avoids SHA256 instance allocation
            var hash = System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, n));
            var len = new FileInfo(row.AbsPath).Length;
            return Convert.ToHexString(hash)[..16] + ":" + len;
        }
        catch
        {
            return row.AbsPath;
        }
    }

    private static async Task<List<string>> CopyPlatesAsync(
        string projectDir,
        string charsDir,
        string key,
        List<BookImageRow> picks,
        bool copyIntoAssets,
        CancellationToken ct = default)
    {
        var relPaths = new List<string>();
        var usedDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < Math.Min(3, picks.Count); j++)
        {
            var row = picks[j];
            if (IsTextOnlyPlate(row)) continue;
            if (copyIntoAssets && File.Exists(row.AbsPath))
                await CopyPlateIntoAssetsAsync(projectDir, charsDir, key, row, j, usedDest, relPaths, ct)
                    .ConfigureAwait(false);
            else if (!ProjectStore.IsTextOnlyPlatePath(row.PathRel))
                relPaths.Add(row.PathRel);
        }
        return relPaths;
    }

    private static bool IsTextOnlyPlate(BookImageRow row) =>
        ProjectStore.IsTextOnlyPlatePath(row.PathRel) || ProjectStore.IsTextOnlyPlatePath(row.Name);

    private static async Task CopyPlateIntoAssetsAsync(
        string projectDir,
        string charsDir,
        string key,
        BookImageRow row,
        int j,
        HashSet<string> usedDest,
        List<string> relPaths,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(row.AbsPath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var destName = $"{key.ToLowerInvariant()}_bookref_{j + 1}{ext}";
        var dest = Path.Combine(charsDir, destName);
        var srcFp = ContentFingerprint(row);
        if (srcFp.Length > 0 && usedDest.Contains(srcFp))
            return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(row.AbsPath, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
            relPaths.Add(Path.GetRelativePath(projectDir, dest).Replace('\\', '/'));
            if (srcFp.Length > 0) usedDest.Add(srcFp);
        }
        catch
        {
            if (!ProjectStore.IsTextOnlyPlatePath(row.PathRel))
                relPaths.Add(row.PathRel);
        }
    }

    /// <summary>Remove leftover bookref_N from earlier sorts that are no longer referenced.</summary>
    private static void CleanupStaleBookrefs(string charsDir, string key, int keepCount)
    {
        if (!Directory.Exists(charsDir)) return;
        var prefix = key.ToLowerInvariant() + "_bookref_";
        foreach (var fi in new DirectoryInfo(charsDir).EnumerateFiles())
        {
            var name = fi.Name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // character_x_bookref_2.png → index 2
            var m = CommonRegex.Match(name, @"_bookref_(\d+)\.", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var idx)) continue;
            if (idx > keepCount)
            {
                try { File.Delete(fi.FullName); } catch { /* ignore */ }
            }
        }
        // Also drop alternate extensions for slots we rewrote (e.g. keep .jpg, delete old .png)
        if (keepCount <= 0) return;
        for (var i = 1; i <= keepCount; i++)
        {
            var matches = new DirectoryInfo(charsDir).GetFiles($"{prefix}{i}.*");
            if (matches.Length <= 1) continue;
            // Keep newest
            var ordered = matches.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var stale in ordered.Skip(1))
            {
                try { File.Delete(stale.FullName); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Prefer AI <c>cast_seeds.json</c> (full cast + source_image_pages), then Fountain parse,
    /// then scenes.json.
    /// </summary>
    private async Task<JsonObject> LoadOrBuildCastSeedsAsync(
        string projectId,
        string castSeedsPath,
        CancellationToken ct)
    {
        JsonObject? seeds = null;

        // 1) cast_seeds.json (canonical cast)
        var castPath = File.Exists(castSeedsPath)
            ? castSeedsPath
            : Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "source", ScreenplayService.CastSeedsFileName);
        if (File.Exists(castPath))
        {
            try
            {
                var existing = JsonNode.Parse(await File.ReadAllTextAsync(castPath, ct).ConfigureAwait(false))
                               as JsonObject;
                var existingSeeds = existing?[CharacterSeedTokensKey] as JsonObject
                    ?? existing?[GlobalProductionVariablesKey]?[CharacterSeedTokensKey] as JsonObject;
                if (existingSeeds is not null && existingSeeds.Count > 0)
                    seeds = existingSeeds.DeepClone().AsObject();
            }
            catch { /* try fountain */ }
        }

        // 2) Fountain-derived cast (dialogue cues) — merge only missing keys
        var model = ScreenplayService.TryBuildModelFromProject(_projects, projectId);
        if (model is not null &&
            model.TryGetValue(GlobalProductionVariablesKey, out var gpvObj) &&
            gpvObj is Dictionary<string, object?> gpv &&
            gpv.TryGetValue(CharacterSeedTokensKey, out var charObj) &&
            charObj is Dictionary<string, object?> charDict &&
            charDict.Count > 0)
        {
            var json = JsonSerializer.Serialize(charDict, JsonDefaults.Indented);
            var fromFountain = JsonNode.Parse(json) as JsonObject;
            if (fromFountain is not null)
            {
                seeds ??= new JsonObject();
                foreach (var (key, node) in fromFountain)
                {
                    if (node is null) continue;
                    if (seeds.ContainsKey(key)) continue; // AI cast wins
                    seeds[key] = node.DeepClone();
                }
            }
        }

        return seeds ?? new JsonObject();
    }

    private async Task TryMirrorBlueprintAsync(string projectDir, JsonObject stage1Seeds)
    {
        try
        {
            var projectId = Path.GetFileName(
                projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var bp = await _projects.FindBlueprintPathAsync(projectId, CancellationToken.None)
                .ConfigureAwait(false);
            if (bp is null || !File.Exists(bp)) return;

            var root = JsonNode.Parse(await File.ReadAllTextAsync(bp, CancellationToken.None)) as JsonObject;
            if (root is null) return;
            var gpv = root["global_production_variables"] as JsonObject ?? new JsonObject();
            root["global_production_variables"] = gpv;
            var bpSeeds = gpv["character_seed_tokens"] as JsonObject ?? new JsonObject();
            gpv["character_seed_tokens"] = bpSeeds;

            foreach (var (key, seedNode) in stage1Seeds)
            {
                if (seedNode is not JsonObject src) continue;
                if (bpSeeds[key] is not JsonObject dest)
                {
                    bpSeeds[key] = src.DeepClone();
                    continue;
                }
                if (src[DesignReferenceImagesKey] is JsonArray arr)
                {
                    dest[DesignReferenceImagesKey] = arr.DeepClone();
                    dest[BookReferenceImagesKey] = arr.DeepClone();
                }
                else
                {
                    dest.Remove(DesignReferenceImagesKey);
                    dest.Remove(BookReferenceImagesKey);
                }
                if (src[SourceImagePagesKey] is JsonArray pages)
                    dest[SourceImagePagesKey] = pages.DeepClone();
            }

            await File.WriteAllTextAsync(bp, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not mirror book plates into blueprint");
        }
    }

    internal static bool IsVoiceOnly(string key, JsonObject seed)
    {
        // Voice-only = never appears on screen — a per-story fact read from the cast seed's
        // display_name_policy, NOT the name "Narrator". An on-camera / POV narrator (e.g. Tell-Tale
        // Heart's confessor) has policy "ok_anytime" and must get a portrait; a pure off-screen
        // narrator (e.g. Mary's) has "never_on_screen". The cast-extraction step already sets that
        // policy from the screenplay (upgrading a V.O.-only role to on-camera when it later appears).
        // Single source of truth: CastKindClassifier.IsVoiceOnlyPolicy.
        var pol = seed["display_name_policy"]?.GetValue<string>();
        return CastKindClassifier.IsVoiceOnlyPolicy(pol);
    }

    private static List<int> PagesForSeed(JsonObject seed)
    {
        var outList = new List<int>();
        var raw = seed[SourceImagePagesKey] ?? seed["image_pages"];
        if (raw is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var one))
                outList.Add(one);
            else if (jv.TryGetValue<string>(out var s))
            {
                foreach (Match m in CommonRegex.Matches(s, @"\d+"))
                    if (int.TryParse(m.Value, out var n)) outList.Add(n);
            }
        }
        else if (raw is JsonArray arr)
        {
            foreach (var x in arr)
            {
                if (x is null) continue;
                if (x is JsonValue v && v.TryGetValue<int>(out var n))
                    outList.Add(n);
                else if (int.TryParse(x.ToString(), out var n2))
                    outList.Add(n2);
            }
        }
        return outList;
    }

    private static List<BookImageRow> RowsForPages(List<BookImageRow> inventory, List<int> pages)
    {
        var byPage = inventory.GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.ToList());
        var picks = new List<BookImageRow>();
        foreach (var pg in pages)
        {
            if (!byPage.TryGetValue(pg, out var cands) || cands.Count == 0) continue;
            var best = RankIllustrationFirst(cands).FirstOrDefault(r => !IsLikelyTextLayout(r));
            if (best is null) continue;
            picks.Add(best);
        }
        return RankIllustrationFirst(picks).Take(3).ToList();
    }

    private static List<BookImageRow> HeuristicPicks(
        List<BookImageRow> inventory,
        string key,
        JsonObject seed,
        int index,
        bool nameHitsOnly = false)
    {
        var ranked = RankIllustrationFirst(inventory.Where(r => !IsLikelyTextLayout(r)));
        var early = ranked.Where(r =>
                r.Page is > 0 and <= 8 ||
                r.Name.Contains(CoverCategory, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(SparseCategory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (early.Count == 0)
            early = ranked.Take(6).ToList();

        var token = key.Replace(CharacterPrefix, "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        var given = (seed[CanonicalGivenNameKey]?.GetValue<string>() ?? "").ToLowerInvariant();
        // "mom"/"dad" tokens are useless for filename matching and caused false plate attaches
        var genericRoleToken = token is "mom" or "dad" or "daddy" or "mum" or "mother" or "father" or "parent";
        var nameHits = RankIllustrationFirst(inventory.Where(r =>
        {
            if (IsLikelyTextLayout(r)) return false;
            if (!genericRoleToken && token.Length >= 3 &&
                r.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (given.Length >= 3 && !genericRoleToken &&
                r.Name.Contains(given, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }).ToList());

        var desc = seed[DescriptionKey]?.GetValue<string>() ?? "";
        var isHero = index == 0 ||
                     CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                         key, "", desc, "", "dog") ||
                     CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                         key, "", desc, "", "cat");

        List<BookImageRow> baseline;
        if (nameHits.Count > 0) baseline = nameHits.Take(8).ToList();
        else if (nameHitsOnly) return new List<BookImageRow>(); // supporting cast: no plate is correct
        else if (isHero) baseline = early.Take(8).ToList();
        else return new List<BookImageRow>(); // never invent plates for supporting cast

        // Optional chat re-rank of basenames (sync path keeps baseline; async attach uses RankHeuristicAsync)
        return baseline.Take(3).ToList();
    }

    /// <summary>Async wrapper: heuristic candidates then optional chat re-rank.</summary>
    private async Task<List<BookImageRow>> HeuristicPicksRankedAsync(
        List<BookImageRow> inventory,
        string key,
        JsonObject seed,
        int index,
        bool nameHitsOnly = false,
        CancellationToken ct = default)
    {
        var baseline = HeuristicPicks(inventory, key, seed, index, nameHitsOnly);
        if (_plateRank is null || !_plateRank.IsEnabled || baseline.Count <= 1)
            return baseline;
        var names = baseline.Select(r => r.Name).ToList();
        var desc = seed[DescriptionKey]?.GetValue<string>() ?? "";
        var (ranked, usedAi) = await _plateRank.RankAsync(key, desc, names, ct).ConfigureAwait(false);
        if (!usedAi || ranked.Count == 0) return baseline.Take(3).ToList();
        var byName = baseline.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BookImageRow>();
        foreach (var n in ranked)
        {
            if (byName.TryGetValue(n, out var row))
                ordered.Add(row);
        }
        foreach (var row in baseline.Where(row =>
                     !ordered.Any(o => o.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase))))
            ordered.Add(row);
        return ordered.Take(3).ToList();
    }

    private static List<BookImageRow> RankIllustrationFirst(IEnumerable<BookImageRow> rows) =>
        rows.OrderBy(IllustrationScore)
            .ThenByDescending(r =>
            {
                try { return new FileInfo(r.AbsPath).Length; }
                catch { return 0L; }
            })
            .ThenBy(r => r.Page > 0 ? r.Page : 99)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int IllustrationScore(BookImageRow r)
    {
        var n = r.Name;
        var score = 50;
        if (n.Contains(CoverCategory, StringComparison.OrdinalIgnoreCase)) score -= 40;
        if (n.Contains(SparseCategory, StringComparison.OrdinalIgnoreCase)) score -= 30;
        if (r.Kind == "rendered_page" || n.StartsWith("page_", StringComparison.OrdinalIgnoreCase))
            score -= 5;
        if (r.Kind == "embedded" || n.Contains("embedded", StringComparison.OrdinalIgnoreCase))
            score -= 8;
        if (IsLikelyTextLayout(r)) score += 35;
        if (n.Contains("text", StringComparison.OrdinalIgnoreCase) &&
            !n.Contains(SparseCategory, StringComparison.OrdinalIgnoreCase))
            score += 10;
        return score;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> TextOnlyCache = new();

    public static void ClearTextOnlyCache() => TextOnlyCache.Clear();

    public static bool IsTextOnlyImageFile(string absPath)
    {
        if (string.IsNullOrWhiteSpace(absPath) || !File.Exists(absPath))
            return false;

        try
        {
            var info = new FileInfo(absPath);
            var cacheKey = $"{absPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            if (TextOnlyCache.TryGetValue(cacheKey, out var cached))
                return cached;

            using var bitmap = SkiaSharp.SKBitmap.Decode(absPath);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return false;

            int samplesX = Math.Min(40, bitmap.Width);
            int samplesY = Math.Min(40, bitmap.Height);
            int stepX = Math.Max(1, bitmap.Width / samplesX);
            int stepY = Math.Max(1, bitmap.Height / samplesY);

            int totalSamples = 0;
            int colorCount = 0;

            int width = bitmap.Width;
            var pixels = bitmap.Pixels;
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x += stepX)
                {
                    totalSamples++;
                    var pixel = pixels[rowOffset + x];
                    int r = pixel.Red;
                    int g = pixel.Green;
                    int b = pixel.Blue;

                    int maxC = Math.Max(r, Math.Max(g, b));
                    int minC = Math.Min(r, Math.Min(g, b));
                    if (maxC - minC > 20)
                    {
                        colorCount++;
                    }
                }
            }

            if (totalSamples == 0) return false;

            double colorRatio = (double)colorCount / totalSamples;

            // Text-only page: < 1.5% color saturation pixels (monochrome text on white/gray paper)
            bool isTextOnly = colorRatio < 0.015;

            TextOnlyCache[cacheKey] = isTextOnly;
            return isTextOnly;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyTextLayout(BookImageRow r)
    {
        if (ProjectStore.IsTextOnlyPlatePath(r.Name) || ProjectStore.IsTextOnlyPlatePath(r.PathRel))
            return true;

        if (r.Name.Contains("text_page", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains("text-only", StringComparison.OrdinalIgnoreCase) ||
            r.Kind.Contains("text", StringComparison.OrdinalIgnoreCase))
            return true;

        // Manifest relevance (book_images.v1) — pure text spreads must never seed portraits
        if (string.Equals(r.Relevance, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Relevance, "text_only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Relevance, "text_heavy", StringComparison.OrdinalIgnoreCase))
            return true;

        // Explicit cover or sparse visual tag override
        if (r.Name.Contains(CoverCategory, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains(SparseCategory, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains("bookref", StringComparison.OrdinalIgnoreCase))
            return false;

        // Pixel-level bitmap inspection for image files on disk
        if (!string.IsNullOrWhiteSpace(r.AbsPath) && File.Exists(r.AbsPath) && IsTextOnlyImageFile(r.AbsPath))
            return true;

        return false;
    }

    private static async Task<List<BookImageRow>> LoadBookImageInventoryAsync(string projectDir)
    {
        var rows = new List<BookImageRow>();
        var source = Path.Combine(projectDir, "source");
        var imgDir = Path.Combine(source, "book_images");
        var man = Path.Combine(imgDir, "manifest.json");

        await TryAddManifestBookImageRowsAsync(projectDir, source, imgDir, man, rows).ConfigureAwait(false);
        if (rows.Count == 0 && Directory.Exists(imgDir))
            AddFilesystemBookImageRows(projectDir, imgDir, rows);

        return rows;
    }

    private static async Task TryAddManifestBookImageRowsAsync(
        string projectDir,
        string source,
        string imgDir,
        string man,
        List<BookImageRow> rows)
    {
        if (!File.Exists(man))
            return;

        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(man));
            AddRowsFromManifestDocument(doc, projectDir, source, imgDir, rows);
        }
        catch { /* fall through */ }
    }

    private static void AddRowsFromManifestDocument(
        JsonDocument doc,
        string projectDir,
        string source,
        string imgDir,
        List<BookImageRow> rows)
    {
        if (!doc.RootElement.TryGetProperty("images", out var imgs) ||
            imgs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var im in imgs.EnumerateArray())
        {
            var row = TryCreateManifestBookImageRow(im, projectDir, source, imgDir);
            if (row is not null)
                rows.Add(row);
        }
    }

    private static BookImageRow? TryCreateManifestBookImageRow(
        JsonElement im,
        string projectDir,
        string source,
        string imgDir)
    {
        var rel = im.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        rel = rel.Replace('\\', '/');
        var abs = Path.IsPathRooted(rel)
            ? rel
            : Path.Combine(source, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
            abs = Path.Combine(imgDir, Path.GetFileName(rel));
        if (!File.Exists(abs))
            return null;
        var page = im.TryGetProperty("page", out var pg) && pg.TryGetInt32(out var pn) ? pn : 0;
        var kind = im.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        var relevance = im.TryGetProperty("relevance", out var relEl)
            ? relEl.GetString() ?? ""
            : "";
        var pathRel = Path.GetRelativePath(projectDir, abs).Replace('\\', '/');
        return new BookImageRow(
            pathRel, abs, page, kind,
            Path.GetFileName(abs).ToLowerInvariant(),
            relevance);
    }

    private static void AddFilesystemBookImageRows(string projectDir, string imgDir, List<BookImageRow> rows)
    {
        foreach (var fi in new DirectoryInfo(imgDir).EnumerateFiles()
                     .Where(f => CommonRegex.IsMatch(f.Extension, @"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = fi.Name;
            var f = fi.FullName;
            var m = CommonRegex.Match(name, @"(?:page|p|embedded_p)0*(\d+)", RegexOptions.IgnoreCase);
            var page = m.Success && int.TryParse(m.Groups[1].Value, out var pn) ? pn : 0;
            var pathRel = Path.GetRelativePath(projectDir, f).Replace('\\', '/');
            rows.Add(new BookImageRow(pathRel, f, page, "file", name.ToLowerInvariant()));
        }
    }

    public async Task<List<RankedBookCandidate>> GetRankedBookCandidatesAsync(
        string projectId,
        string charKey,
        CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var inventoryAll = await LoadBookImageInventoryAsync(projectDir).ConfigureAwait(false);
        var inventory = inventoryAll.Where(r => !IsLikelyTextLayout(r)).ToList();
        if (inventory.Count == 0)
            return new List<RankedBookCandidate>();

        var seeds = _projects.GetAllCharacterSeeds(projectId);
        seeds.TryGetValue(charKey, out var el);
        var charSeed = TryParseSeedJson(el) ?? new JsonObject();
        var aliases = BookOcrPlateShortlist.AliasesForSeed(charKey, charSeed);
        var ocrPages = await BookOcrPlateShortlist.TryLoadAsync(projectDir, ct).ConfigureAwait(false);
        var textHits = BookOcrPlateShortlist.FindTextHitPages(ocrPages, aliases);
        var textHitPages = new HashSet<int>(textHits);
        var neighborPages = BuildNeighborPages(textHits);
        var currentRefs = CollectCurrentReferencePaths(el);

        var list = new List<RankedBookCandidate>();
        foreach (var r in inventory)
            list.Add(BuildRankedCandidate(projectId, r, aliases, textHitPages, neighborPages, currentRefs));

        return list.OrderByDescending(c => c.Score)
                   .ThenBy(c => c.Page > 0 ? c.Page : 999)
                   .ThenBy(c => c.Name)
                   .ToList();
    }

    private JsonObject? TryParseSeedJson(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Undefined)
            return null;
        try { return JsonNode.Parse(el.GetRawText()) as JsonObject; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Optional character seed JSON was not an object");
            return null;
        }
    }

    private static HashSet<int> BuildNeighborPages(IEnumerable<int> textHits)
    {
        var neighborPages = new HashSet<int>();
        foreach (var p in textHits)
        {
            neighborPages.Add(p);
            neighborPages.Add(p + 1);
            neighborPages.Add(p - 1);
        }
        return neighborPages;
    }

    private static HashSet<string> CollectCurrentReferencePaths(JsonElement el)
    {
        var currentRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind == JsonValueKind.Undefined)
            return currentRefs;
        AddReferencePathsFromProperty(el, DesignReferenceImagesKey, currentRefs);
        AddReferencePathsFromProperty(el, BookReferenceImagesKey, currentRefs);
        return currentRefs;
    }

    private static void AddReferencePathsFromProperty(JsonElement el, string prop, HashSet<string> currentRefs)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } s)
                currentRefs.Add(s.Replace('\\', '/'));
        }
    }

    private static RankedBookCandidate BuildRankedCandidate(
        string projectId,
        BookImageRow r,
        List<string> aliases,
        HashSet<int> textHitPages,
        HashSet<int> neighborPages,
        HashSet<string> currentRefs)
    {
        var baseScore = 50.0;
        baseScore += OcrProximityBoost(r.Page, textHitPages, neighborPages);
        baseScore += ImageTypeQualityBoost(r.Name);
        baseScore += AliasFilenameBoost(r.Name, aliases);

        var rel = r.PathRel.Replace('\\', '/');
        if (!rel.StartsWith("source/")) rel = "source/" + rel;

        return new RankedBookCandidate
        {
            Name = r.Name,
            PathRel = rel,
            Url = $"/api/projects/{projectId}/book-images/{Uri.EscapeDataString(r.Name)}",
            Page = r.Page,
            Score = Math.Min(99.0, Math.Max(10.0, baseScore)),
            Description = $"Page {(r.Page > 0 ? r.Page.ToString() : "Art")}",
            IsSelected = currentRefs.Contains(rel) || currentRefs.Contains(r.Name),
        };
    }

    private static double OcrProximityBoost(int page, HashSet<int> textHitPages, HashSet<int> neighborPages)
    {
        if (page > 0 && textHitPages.Contains(page)) return 35.0;
        if (page > 0 && neighborPages.Contains(page)) return 25.0;
        return 0.0;
    }

    private static double ImageTypeQualityBoost(string name)
    {
        name = name.ToLowerInvariant();
        if (name.Contains(CoverCategory)) return 15.0;
        if (name.Contains(SparseCategory) || name.Contains("figure") || name.Contains("embedded")) return 10.0;
        if (name.StartsWith("page_")) return 5.0;
        return 0.0;
    }

    private static double AliasFilenameBoost(string name, List<string> aliases)
    {
        foreach (var a in aliases)
        {
            if (a.Length >= 3 && name.Contains(a, StringComparison.OrdinalIgnoreCase))
                return 20.0;
        }
        return 0.0;
    }

    private sealed record BookImageRow(
        string PathRel,
        string AbsPath,
        int Page,
        string Kind,
        string Name,
        string Relevance = "");
}

public sealed class RankedBookCandidate
{
    public string Name { get; set; } = "";
    public string PathRel { get; set; } = "";
    public string Url { get; set; } = "";
    public int Page { get; set; }
    public double Score { get; set; }
    public string Description { get; set; } = "";
    public bool IsSelected { get; set; }
}

