using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Adaptation.Validation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Fountain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Abstractions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// AI: approved Fountain (+ optional book) → <c>source/cast_seeds.json</c>.
/// Cast identity for Characters UI / plates — not parsed solely from dialogue cues.
/// Book text is used heavily for description / visual_lock so portrait gen starts closer to right.
/// </summary>
public sealed class CastFromScreenplayService
{
    public const string PromptRelativePath = "prompts/fountain_to_cast.txt";

    /// <summary>Fountain budget for cast extract user messages.</summary>
    public const int FountainPromptChars = 80_000;
    /// <summary>
    /// Book budget for cast extract. Full text when under this size; otherwise
    /// name-targeted look excerpts from the whole book + multi-window spine samples
    /// so late-appearing / wardrobe-change cues still reach visual_lock.
    /// </summary>
    public const int BookPromptChars = 100_000;

    /// <summary>Share of book budget reserved for name-targeted look excerpts on long novels.</summary>
    public const double BookNameExcerptBudgetShare = 0.55;

    /// <summary>Evenly spaced narrative windows when the book is truncated (covers late chapters).</summary>
    public const int BookSpineWindowCount = 5;

    private const string KeyCharacterSeedTokens = "character_seed_tokens";
    private const string KeyLocationSeedTokens = "location_seed_tokens";
    private const string KeyWardrobeLockTokens = "wardrobe_lock_tokens";
    private const string KeyGlobalProductionVariables = "global_production_variables";
    private const string KeyDisplayName = "display_name";
    private const string KeyVariantOf = "variant_of";
    private const string KeyRenderStyleLock = "render_style_lock";
    private const string KeyPerformanceLock = "performance_lock";
    private const string KeyVisualLock = "visual_lock";
    private const string KeyCanonicalGivenName = "canonical_given_name";
    private const string KeyReferenceImagePlaceholder = "reference_image_placeholder";
    private const string KeyWardrobeAlways = "wardrobe_always";

    private readonly ProjectStore _projects;
    private readonly IChatClient _chat;
    private readonly CastVisualLiteralizeService _literalize;
    private readonly ProjectRulesService _projectRules;
    private readonly CharacterBookPlateService? _plateService;
    private readonly ILogger<CastFromScreenplayService> _log;
    private readonly XaiResponsesClient? _responses;
    private readonly BookTextRegistryService? _bookRegistry;
    private readonly IBookFileSessionFactory? _bookFileSessions;
    private readonly bool _useFakes;

    public CastFromScreenplayService(
        ProjectStore projects,
        IChatClient chat,
        CastVisualLiteralizeService literalize,
        ProjectRulesService projectRules,
        ILogger<CastFromScreenplayService> log,
        CharacterBookPlateService? plateService = null,
        XaiResponsesClient? responses = null,
        BookTextRegistryService? bookRegistry = null,
        IBookFileSessionFactory? bookFileSessions = null,
        IOptions<PageToMovieOptions>? opts = null)
    {
        _projects = projects;
        _chat = chat;
        _literalize = literalize;
        _projectRules = projectRules;
        _log = log;
        _plateService = plateService;
        _responses = responses;
        _bookRegistry = bookRegistry;
        _bookFileSessions = bookFileSessions;
        _useFakes = opts?.Value.UseFakes ?? false;
    }

    public sealed class ExtractResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? OutPath { get; init; }
        public int CharacterCount { get; init; }
        public List<string> CharacterKeys { get; init; } = new();
        public string? MovieTitle { get; init; }
        public string? RawPath { get; init; }
        /// <summary>Dialogue speakers with no cast_seeds entry (deterministic membership check).</summary>
        public List<string> SpeakersMissingFromCast { get; init; } = new();
        /// <summary>0–100 membership score from <see cref="PageToMovie.Adaptation.Validation.CastPackageCrossCheck"/>.</summary>
        public double MembershipScore { get; init; }
        /// <summary>True when every required speaker has a cast seed.</summary>
        public bool MembershipOk { get; init; } = true;
        /// <summary>Path to written cast package check report (when run).</summary>
        public string? PackageCheckPath { get; init; }
    }

    /// <summary>
    /// Build cast_seeds.json from screenplay.fountain (+ book_full.txt when present).
    /// </summary>
    public async Task<ExtractResult> ExtractAsync(
        string projectId,
        string? model = null,
        bool force = false,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_chat.IsConfigured)
            throw new InvalidOperationException("Connect service (API key) to build cast from the screenplay.");

        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        model = string.IsNullOrWhiteSpace(model)
            ? ProjectModelSelection.RequirePlanning(cfg, "Cast from screenplay")
            : ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Cast from screenplay");

        ScreenplayService.EnsureCanonicalDraft(_projects, projectId);
        var draftPath = ScreenplayService.GetDraftPath(_projects, projectId);
        if (!File.Exists(draftPath))
            return new ExtractResult { Ok = false, Error = "No screenplay draft. Import/approve a Fountain first." };

        var fountain = await File.ReadAllTextAsync(draftPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fountain))
            return new ExtractResult { Ok = false, Error = "Screenplay draft is empty." };

        var outPath = ScreenplayService.GetCastSeedsPath(_projects, projectId);
        if (!force && File.Exists(outPath))
        {
            try
            {
                using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false));
                var seeds = GetSeedsElement(existing.RootElement);
                if (seeds.ValueKind == JsonValueKind.Object && seeds.EnumerateObject().Any())
                {
                    onProgress?.Invoke("Cast file already present — use force to rebuild.");
                    var existingKeys = seeds.EnumerateObject().Select(p => p.Name).ToList();
                    return new ExtractResult
                    {
                        Ok = true,
                        OutPath = outPath,
                        CharacterCount = existingKeys.Count,
                        CharacterKeys = existingKeys,
                        MovieTitle = existing.RootElement.TryGetProperty(JsonKeys.MovieTitle, out var mt)
                            ? mt.GetString()
                            : null,
                    };
                }
            }
            catch { /* rebuild */ }
        }

        var book = await LoadBookTextAsync(projectId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(book))
            onProgress?.Invoke($"Book context loaded ({book.Length:N0} chars) for look extraction…");
        else
            onProgress?.Invoke("No book_full.txt — looks will come from screenplay action only.");

        // Closed cast is model-decided only (no name-discovery / verb / kinship heuristic lists).
        onProgress?.Invoke("Loading cast prompt…");
        var system = await LoadSystemPromptAsync(_projects.WorkspaceRoot, ct).ConfigureAwait(false);
        var locationHints = BuildLocationHintsFromFountain(fountain);
        var user = BuildUserPrompt(fountain, book, locationHints);

        onProgress?.Invoke("Calling Grok for closed cast + locations (book-aware looks)…");
        Dictionary<string, object?>? parsed = await TryParseViaFilesAsync(
            projectId, fountain, book, system, locationHints, model, onProgress, ct).ConfigureAwait(false);
        if (parsed is null)
        {
            var pipeline = new ValidatedModelOperation<CastModelInput, string, Dictionary<string, object?>>(
                new CastChatOperation(_chat, "cast_extraction", "1", ChatCallModes.CastFromScreenplay, 0.2),
                new CastJsonObjectParser(),
                new CastExtractionValidator(),
                new TerminalCastFallback(),
                new ModelOperationOptions { CorrectiveMaxAttempts = 1 });
            var lifecycle = await pipeline.ExecuteAsync(new CastModelInput(system, user, model), ct).ConfigureAwait(false);
            await WriteLifecycleManifestAsync(projectId, "cast_extraction", lifecycle, ct).ConfigureAwait(false);
            if (!lifecycle.Success || lifecycle.Value is null)
                return new ExtractResult
                {
                    Ok = false,
                    Error = lifecycle.Error ?? string.Join(" ", lifecycle.ValidationIssues.Select(i => i.Message)),
                };
            parsed = lifecycle.Value;
        }

        var normalized = NormalizeCastDoc(parsed, projectId, book);

        var seedsObj = GetSeedsDict(normalized);

        if (seedsObj.Count == 0)
            return new ExtractResult { Ok = false, Error = "Model returned no character_seed_tokens." };

        // Looks only: for seeds the model already chose, pull book paragraphs that mention
        // those names when description/visual_lock is empty or a stub.
        if (!string.IsNullOrWhiteSpace(book))
        {
            var filled = EnrichStubLooksFromSources(seedsObj, book, fountain);
            if (filled > 0)
            {
                onProgress?.Invoke($"Filled looks for {filled} cast member(s) from book/screenplay text…");
                normalized[KeyCharacterSeedTokens] = seedsObj;
            }
        }


        // Second AI pass: figurative / idiomatic visual language → literal filmable prose
        var literalSeeds = await _literalize.LiteralizeSeedsAsync(
            seedsObj, model, onProgress, ct).ConfigureAwait(false);
        normalized[KeyCharacterSeedTokens] = literalSeeds;
        seedsObj = literalSeeds;

        // Same figurative→literal / base-look scrub pass, applied once to the shared wardrobe
        // group text instead of per-character (there may be several members pointing at it).
        if (normalized.TryGetValue(KeyWardrobeLockTokens, out var wlObj) &&
            wlObj is Dictionary<string, object?> wlDict && wlDict.Count > 0)
        {
            normalized[KeyWardrobeLockTokens] = await _literalize.LiteralizeSeedsAsync(
                wlDict, model, onProgress, ct).ConfigureAwait(false);
        }

        // Location AI pass: model filmable set locks; fallback to Stage‑1 heading+action seeds.
        // Do NOT run CastVisualLiteralize on locations — that prompt is character-portrait
        // only and was wiping/mangling set-design text.
        var locSeeds = GetLocationSeedsDict(normalized);
        if (locSeeds.Count == 0)
        {
            onProgress?.Invoke("No AI location seeds — filling from screenplay headings…");
            locSeeds = _projects.ExtractLocationSeedObjects(projectId)
                       ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            onProgress?.Invoke($"Keeping {locSeeds.Count} AI location seed(s) (set-design locks)…");
            // Fill any heading places the model missed
            var fountainLocs = _projects.ExtractLocationSeedObjects(projectId);
            if (fountainLocs is not null)
            {
                foreach (var (k, v) in fountainLocs)
                {
                    if (!locSeeds.ContainsKey(k))
                        locSeeds[k] = v;
                }
            }
        }
        if (locSeeds.Count > 0)
            normalized[KeyLocationSeedTokens] = locSeeds;

        onProgress?.Invoke(
            $"Writing {seedsObj.Count} character seed(s)" +
            (locSeeds.Count > 0 ? $" · {locSeeds.Count} location(s)" : "") +
            "…");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        if (File.Exists(outPath))
        {
            try
            {
                var bakPath = outPath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                var bytes = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(bakPath, bytes, ct).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        var castIssues = StructuredOperationArtifacts.RequireJsonProperties(
            normalized, "schema_version", KeyCharacterSeedTokens);
        await StructuredOperationArtifacts.WriteAsync(
            await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "cast_extraction", model,
            new { projectId, fountain, book }, normalized, castIssues, ct).ConfigureAwait(false);
        if (castIssues.Any(i => i.Severity == ModelValidationSeverity.Error))
            return new ExtractResult
            {
                Ok = false,
                Error = string.Join(" ", castIssues.Select(i => i.Message)),
            };

        var json = JsonSerializer.Serialize(normalized, JsonDefaults.Indented);
        await File.WriteAllTextAsync(outPath, json + "\n", ct).ConfigureAwait(false);

        // Locations are already in the write above. Only merge when the model returned none
        // and we need Stage‑1 heading seeds — never re-merge over a full cast write (that path
        // previously produced locations-only cast_seeds.json and dropped 45 characters).
        try
        {
            if (GetLocationSeedsDict(normalized).Count == 0)
            {
                if (_projects.MergeLocationSeedsIntoCastFile(projectId))
                    onProgress?.Invoke("Merged location seeds from screenplay headings…");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Location seed merge failed for {Project}", projectId);
        }

        if (_plateService is not null)
        {
            try
            {
                onProgress?.Invoke("Automated book picture matching...");
                await _plateService.AttachAsync(projectId, force: true, copyIntoAssets: true, onProgress: onProgress, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Automated book picture matching failed for {Project}", projectId);
            }
        }

        // Project style + performance rules from book/screenplay (medium + audience address)
        try
        {
            if (normalized.TryGetValue(KeyRenderStyleLock, out var rslObj) &&
                rslObj?.ToString() is { Length: > 0 } rsl &&
                await _projectRules.EnsureStyleRuleFromRenderLockAsync(projectId, rsl, approvedBy: "cast_extract", ct: ct).ConfigureAwait(false))
                onProgress?.Invoke("Project style rule updated from book/screenplay medium.");

            // Persist structured medium for portraits (prefer adaptation vision_meta if already set).
            var rslText = normalized.TryGetValue(KeyRenderStyleLock, out var rslV) ? rslV?.ToString() : null;
            var perfText = normalized.TryGetValue(KeyPerformanceLock, out var plV) ? plV?.ToString() : null;
            ProjectVisionMeta.UpsertFromCast(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), rslText, perfText);

            if (normalized.TryGetValue(KeyPerformanceLock, out var perfObj) &&
                perfObj?.ToString() is { Length: > 0 } perf &&
                await _projectRules.EnsurePerformanceRuleFromLockAsync(projectId, perf, approvedBy: "cast_extract", ct: ct).ConfigureAwait(false))
                onProgress?.Invoke("Project performance/address rule updated from book/screenplay.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write style/performance project rules for {Project}", projectId);
        }

        var keys = seedsObj.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        // Deterministic: every dialogue speaker must have a cast seed (catch ELI/CLARA-style inventions).
        string? packageCheckPath = null;
        var membershipOk = true;
        var missingFromCast = new List<string>();
        var membershipScore = 100.0;
        try
        {
            var report = CastPackageCrossCheck.Evaluate(fountain, json, book);
            membershipOk = report.SpeakersMissingFromCast.Count == 0;
            missingFromCast = report.SpeakersMissingFromCast.ToList();
            membershipScore = report.MembershipScore;
            var checkDir = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "artifacts", "model_operations");
            Directory.CreateDirectory(checkDir);
            packageCheckPath = Path.Combine(checkDir, "cast_package_membership.json");
            var checkPayload = new
            {
                ok = membershipOk,
                membershipScore,
                speakersMissingFromCast = missingFromCast,
                speakersMissingFromBook = report.SpeakersMissingFromBook,
                failures = report.Failures,
                warnings = report.Warnings,
                groupCastKeys = report.GroupCastKeys,
            };
            await File.WriteAllTextAsync(
                packageCheckPath,
                JsonSerializer.Serialize(checkPayload, JsonDefaults.Indented) + "\n",
                ct).ConfigureAwait(false);
            if (!membershipOk)
            {
                onProgress?.Invoke(
                    $"Cast membership warning: {missingFromCast.Count} speaker(s) not in cast — "
                    + string.Join(", ", missingFromCast));
                _log.LogWarning(
                    "Cast extract {Project}: speakers missing from cast: {Missing}",
                    projectId, string.Join(", ", missingFromCast));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cast membership check failed for {Project}", projectId);
        }

        onProgress?.Invoke($"Cast ready · {keys.Count} character(s)");
        _log.LogInformation(
            "Cast extract {Project}: {Count} character(s) → {Keys}",
            projectId, keys.Count, string.Join(", ", keys));
        _projects.TriggerAutoGitCommit(projectId, ProjectStageCommits.CastBuilt);
        return new ExtractResult
        {
            Ok = true,
            OutPath = outPath,
            CharacterCount = keys.Count,
            CharacterKeys = keys,
            MovieTitle = normalized.TryGetValue(JsonKeys.MovieTitle, out var movieTitleObj) ? movieTitleObj?.ToString() : null,
            SpeakersMissingFromCast = missingFromCast,
            MembershipScore = membershipScore,
            MembershipOk = membershipOk,
            PackageCheckPath = packageCheckPath,
        };
    }


    /// <summary>
    /// Normalize model <c>cast_kind</c> or classify group/chorus from key/name/description.
    /// Production pin gates skip locked portraits when this returns <c>group</c>.
    /// </summary>
    public static string ResolveCastKind(
        string key,
        string? displayName = null,
        string? modelCastKind = null,
        string? description = null)
    {
        if (!string.IsNullOrWhiteSpace(modelCastKind))
        {
            var lower = modelCastKind.Trim().ToLowerInvariant();
            if (lower is "group" or "chorus" or "ensemble" or "crowd")
                return "group";
            if (lower is "individual" or "character" or "person")
                return "individual";
        }

        return CastKindClassifier.IsGroup(key, displayName, castKind: null, description)
            ? "group"
            : "individual";
    }

    public static Task<string> LoadSystemPromptAsync(string workspaceRoot, CancellationToken ct = default) =>
        PromptFiles.ReadAsync(PromptRelativePath, workspaceRoot, ct);

    private async Task WriteLifecycleManifestAsync<TResult>(
        string projectId,
        string operation,
        ValidatedModelResult<TResult> result,
        CancellationToken ct) where TResult : class
    {
        var dir = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "artifacts", "model_operations");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, operation + ".lifecycle.json"),
            ModelExecutionManifest.Serialize(result), ct).ConfigureAwait(false);
    }

    private async Task<string?> LoadBookTextAsync(string projectId, CancellationToken ct)
    {
        var source = Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "source");
        foreach (var name in new[] { "book_full.txt", "book.txt", "source.txt" })
        {
            var path = Path.Combine(source, name);
            if (!File.Exists(path)) continue;
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private async Task<Dictionary<string, object?>?> TryParseViaFilesAsync(
        string projectId,
        string fountain,
        string? book,
        string system,
        string? locationHints,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (_responses is null) return null;
        try
        {
            var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var deps = new ScreenplayEnrichFiles.Deps(_responses, _bookRegistry, _bookFileSessions, _useFakes);
            var raw = await ScreenplayEnrichFiles.TryCompleteAsync(
                deps, projectId, dir, fountain, book, system,
                ScreenplayEnrichFiles.CastInstruction(locationHints),
                model, onProgress, ct,
                attachBook: !string.IsNullOrWhiteSpace(book),
                requireScreenplay: true,
                screenplayKind: ProjectXaiArtifactFiles.KindScreenplayDraft,
                screenplayFilename: "screenplay.fountain",
                label: "Cast extract").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var parse = new CastJsonObjectParser().Parse(raw);
            if (parse.Value is null || parse.Issues.Count > 0)
            {
                onProgress?.Invoke("File-id cast response was not valid JSON — falling back to inlined chat.");
                return null;
            }
            var issues = new CastExtractionValidator().Validate(parse.Value);
            if (issues.Count > 0)
            {
                onProgress?.Invoke("File-id cast failed validation — falling back to inlined chat.");
                return null;
            }
            return parse.Value;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke("File-id cast failed, falling back to inlined chat: " + ex.Message);
            return null;
        }
    }

    private static string BuildUserPrompt(string fountain, string? book, string? locationHints = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the closed-cast and location authority.");
        sb.AppendLine("SOURCE OF TRUTH: the FOUNTAIN screenplay. It must describe the story world,");
        sb.AppendLine("including visual medium, on-screen cast, and places. BOOK text is supporting detail only");
        sb.AppendLine("when Fountain is thin — never override Fountain medium or cast with book-only invention.");
        sb.AppendLine("There is NO external name list and NO forced candidate list — membership is your judgment only.");
        sb.AppendLine("Include every person or animal who appears on screen or speaks (including silent leads");
        sb.AppendLine("named only in action lines, and titled beings from the book/title when they are story roles).");
        sb.AppendLine("Do NOT invent cast from Fountain scene headings (INT./EXT. lines), place names, times of day,");
        sb.AppendLine("transitions, camera directions, or bare action verbs. Those are staging, not people.");
        sb.AppendLine("Dialogue is not required for a silent lead who is clearly a character in action or book.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL: Fill description + visual_lock with concrete filmable appearance for every");
        sb.AppendLine("on-screen role (age, build, face, hair, eyes, wardrobe, era). Mine BOOK text when present.");
        sb.AppendLine("Never use stubs like \"as described in the screenplay\".");
        sb.AppendLine("On-camera POV/confessor narrators = ok_anytime (not voice-only).");
        sb.AppendLine("Set species_kind from the story (human/animal/etc.) — do not guess from word lists.");
        sb.AppendLine("Set cast_kind to group for plural unnamed bodies (children, crowd, classmates);");
        sb.AppendLine("do not invent proper names for those groups. Individuals stay cast_kind individual.");
        sb.AppendLine("REQUIRED: render_style_lock from the FOUNTAIN medium (title Notes / early Action /");
        sb.AppendLine("story register). The screenplay must carry the book's visual medium; do not choose");
        sb.AppendLine("cartoon vs photoreal from file type. One medium for all cast.");
        sb.AppendLine();
        sb.AppendLine("LOCATIONS (REQUIRED): Also emit location_seed_tokens for every distinct place in");
        sb.AppendLine("Fountain INT./EXT. headings. Same place day vs night = one Loc_* seed.");
        sb.AppendLine("Each description + visual_lock must be filmable set design (architecture, materials,");
        sb.AppendLine("era, signature props, light sources, palette) — not the place name alone.");
        sb.AppendLine("Do not freeze a single scene's time-of-day into visual_lock.");
        if (!string.IsNullOrWhiteSpace(locationHints))
        {
            sb.AppendLine();
            sb.AppendLine("KNOWN PLACES FROM HEADINGS (cover each; you may unify synonyms into one Loc_*):");
            sb.AppendLine(locationHints.TrimEnd());
        }
        sb.AppendLine();
        sb.AppendLine("Return JSON only (schema_version cast_seeds.v1, character_seed_tokens, location_seed_tokens).");
        sb.AppendLine();
        sb.AppendLine("--- BEGIN FOUNTAIN ---");
        sb.AppendLine(SelectTextForPrompt(fountain, FountainPromptChars));
        sb.AppendLine("--- END FOUNTAIN ---");
        if (!string.IsNullOrWhiteSpace(book))
        {
            sb.AppendLine();
            sb.AppendLine("--- BEGIN BOOK (cast + location looks — read fully; no external name list) ---");
            sb.AppendLine(SelectBookTextForCastPrompt(book, BookPromptChars, nameHints: null));
            sb.AppendLine("--- END BOOK ---");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("(No book text attached — infer cast + locations only from Fountain.)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Closed list of place keys/names from Stage‑1 heading parse so the model covers every set.
    /// </summary>
    internal static string BuildLocationHintsFromFountain(string fountain)
    {
        try
        {
            var model = FountainStage1Importer.BuildStage1(FountainParser.Parse(fountain));
            if (model.TryGetValue(KeyGlobalProductionVariables, out var gpvObj)
                && gpvObj is Dictionary<string, object?> gpv
                && gpv.TryGetValue(KeyLocationSeedTokens, out var locObj)
                && locObj is Dictionary<string, object?> locs
                && locs.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var (key, val) in locs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var display = key;
                    if (val is Dictionary<string, object?> d
                        && d.TryGetValue(KeyDisplayName, out var dn)
                        && !string.IsNullOrWhiteSpace(dn?.ToString()))
                        display = dn.ToString();
                    sb.AppendLine($"- {key} ({display})");
                }
                return sb.ToString();
            }
        }
        catch { /* optional hints */ }
        return "";
    }

    internal static Dictionary<string, object?> GetLocationSeedsDict(Dictionary<string, object?> doc)
    {
        if (doc.TryGetValue(KeyLocationSeedTokens, out var s) && s is Dictionary<string, object?> d)
            return d;
        if (doc.TryGetValue(KeyGlobalProductionVariables, out var g)
            && g is Dictionary<string, object?> gpv
            && gpv.TryGetValue(KeyLocationSeedTokens, out var s2)
            && s2 is Dictionary<string, object?> d2)
            return d2;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prefer full text when under budget; otherwise evenly spaced windows (spine sample).
    /// For books with cast names, prefer <see cref="SelectBookTextForCastPrompt"/>.
    /// </summary>
    public static string SelectTextForPrompt(string text, int maxChars)
    {
        text = NormalizeNewlines(text);
        if (text.Length <= maxChars) return text;
        if (maxChars < 4_000)
            return text[..maxChars] + "\n\n[[truncated for length]]\n";

        return SelectSpineWindows(text, maxChars, BookSpineWindowCount)
               + "\n\n[[sampled for length; full source remains on disk]]\n";
    }

    /// <summary>
    /// Book text for the cast model. Under budget → full book. Over budget →
    /// evenly spaced spine samples (no regex “name list” guessing).
    /// Optional <paramref name="nameHints"/> only used after the model returns cast,
    /// to prioritize look excerpts for known keys — never to invent cast membership.
    /// </summary>
    public static string SelectBookTextForCastPrompt(
        string bookText,
        int maxChars,
        IReadOnlyList<string>? nameHints = null)
    {
        bookText = NormalizeNewlines(bookText);
        if (bookText.Length <= maxChars) return bookText;
        if (maxChars < 4_000)
            return bookText[..maxChars] + "\n\n[[book truncated for length]]\n";

        nameHints ??= Array.Empty<string>();
        var sb = new StringBuilder(maxChars + 512);
        var used = 0;
        const int markerReserve = 200;

        // Optional: look excerpts for model-chosen names only (post-cast enrichment path).
        if (nameHints.Count > 0)
        {
            var nameBudget = Math.Max(2_000, (int)(maxChars * BookNameExcerptBudgetShare) - markerReserve);
            var excerpts = HarvestNameLookExcerpts(bookText, nameHints, nameBudget);
            if (!string.IsNullOrWhiteSpace(excerpts))
            {
                sb.AppendLine("[[LOOK EXCERPTS — paragraphs mentioning known cast names]]");
                sb.AppendLine(excerpts.TrimEnd());
                sb.AppendLine();
                used = sb.Length;
            }
        }

        var spineBudget = maxChars - used - 160;
        if (spineBudget >= 2_000)
        {
            sb.AppendLine("[[NARRATIVE SPINE — evenly spaced samples]]");
            sb.Append(SelectSpineWindows(bookText, spineBudget, BookSpineWindowCount));
            sb.AppendLine();
        }

        sb.AppendLine(nameHints.Count > 0
            ? "[[book sampled for length; look excerpts for known cast names; remainder on disk]]"
            : "[[book sampled for length (spine only — cast membership is model-decided); remainder on disk]]");
        var result = sb.ToString();
        if (result.Length > maxChars + 500)
            result = result[..maxChars] + "\n\n[[truncated for length]]\n";
        return result;
    }

    /// <summary>
    /// Fill empty/stub description + visual_lock using book/fountain paragraphs that
    /// mention names the <b>model already chose</b>. Returns how many seeds were updated.
    /// Does not add or remove cast members.
    /// </summary>
    public static int EnrichStubLooksFromSources(
        Dictionary<string, object?> seeds,
        string? bookText,
        string? fountainText)
    {
        if (seeds is null || seeds.Count == 0) return 0;
        var updated = 0;
        foreach (var (key, val) in seeds.ToList())
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var desc = CoerceString(seed, JsonKeys.Description) ?? "";
            var vlock = CoerceString(seed, KeyVisualLock) ?? "";
            var needsDesc = string.IsNullOrWhiteSpace(desc) || IsStubLook(desc);
            var needsLock = string.IsNullOrWhiteSpace(vlock) || IsStubLook(vlock);
            if (!needsDesc && !needsLock) continue;

            var display = CoerceString(seed, KeyCanonicalGivenName)
                          ?? CastKindClassifier.StripPrefix(key).Replace('_', ' ');
            var queryNames = new List<string> { display };
            var keyCore = CastKindClassifier.StripPrefix(key).Replace('_', ' ');
            if (!string.IsNullOrWhiteSpace(keyCore) &&
                !string.Equals(keyCore, display, StringComparison.OrdinalIgnoreCase))
                queryNames.Add(keyCore);

            // Fountain is source of truth; book only fills gaps.
            var look = "";
            if (!string.IsNullOrWhiteSpace(fountainText))
            {
                look = HarvestNameLookExcerpts(fountainText, queryNames, maxChars: 1_200);
                look = CollapseLookExcerptToSentence(look, display);
            }
            if (string.IsNullOrWhiteSpace(look) && !string.IsNullOrWhiteSpace(bookText))
            {
                look = HarvestNameLookExcerpts(bookText, queryNames, maxChars: 800);
                look = CollapseLookExcerptToSentence(look, display);
            }
            if (string.IsNullOrWhiteSpace(look)) continue;

            if (needsDesc)
                seed[JsonKeys.Description] = look;
            if (needsLock)
                seed[KeyVisualLock] = look;
            seeds[key] = seed;
            updated++;
        }
        return updated;
    }

    /// <summary>
    /// Character_Buster from "BUSTER"; Character_Bob_Cratchit from "BOB CRATCHIT";
    /// long epithets ("Buster the Noodle Head Dog") collapse to first strong token.
    /// </summary>
    public static string NameToCharacterKey(string displayName)
    {
        var raw = (displayName ?? "").Trim();
        var tokens = NonAlphaNumericRegex.Split(raw)
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0)
            return "Character_Unknown";

        // Drop trailing possessives fragments: Marley's → Marley, s
        tokens = tokens.Where(t => t.Length > 1 || t.Equals("I", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tokens.Count == 0)
            return "Character_Unknown";

        // "Buster the Noodle Head Dog" / "Creature, the" → first token only when long epithet
        if (tokens.Count >= 4)
            tokens = new List<string> { tokens[0] };
        else if (tokens.Count == 3 &&
                 tokens[1].Equals("the", StringComparison.OrdinalIgnoreCase))
            tokens = new List<string> { tokens[0] }; // "X the Y" → X
        // keep "Queen of Hearts", "Count Dracula", "Bob Cratchit", "The Creature"

        static string Pascal(string p) =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : "");

        var body = string.Join("_", tokens.Select(Pascal));
        if (string.IsNullOrWhiteSpace(body))
            body = "Unknown";
        return JsonKeys.CharacterPrefix + body;
    }

    /// <summary>
    /// Pull paragraphs that mention cast names, preferring appearance/wardrobe language.
    /// Scans the full book regardless of length. Public for tests.
    /// </summary>
    public static string HarvestNameLookExcerpts(
        string bookText,
        IReadOnlyList<string> nameHints,
        int maxChars)
    {
        bookText = NormalizeNewlines(bookText);
        if (string.IsNullOrWhiteSpace(bookText) || nameHints.Count == 0 || maxChars < 200)
            return "";

        var paras = ParagraphSplitRegex.Split(bookText.Trim())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0)
            paras.Add(bookText.Trim());

        // Build name matchers (word-boundary, case-insensitive)
        var nameRes = new List<(string Name, Regex Rx)>();
        foreach (var n in nameHints
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(40))
        {
            var escaped = Regex.Escape(n.Trim());
            // Allow multi-word names; avoid matching inside longer tokens
            nameRes.Add((n.Trim(), new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, CommonRegex.Timeout)));
        }

        // Cap per-name so one lead doesn't consume the whole budget
        var perNameCap = Math.Max(800, maxChars / Math.Max(3, Math.Min(nameRes.Count, 12)));
        var perNameUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(int Score, int Index, string Name, string Para)>();
        var selectedIdx = new HashSet<int>();
        var totalUsed = 0;

        for (var i = 0; i < paras.Count; i++)
        {
            var para = paras[i];
            if (para.Length < 24) continue;
            string? hitName = null;
            foreach (var (name, rx) in nameRes)
            {
                if (!rx.IsMatch(para)) continue;
                hitName = name;
                break;
            }
            if (hitName is null) continue;

            var score = 1;
            if (LookLanguage.IsMatch(para)) score += 5;
            if (para.Length is >= 80 and <= 1_200) score += 1;
            // Prefer later paragraphs slightly (wardrobe reveals often come mid/late)
            score += Math.Min(3, i * 3 / Math.Max(1, paras.Count));

            candidates.Add((score, i, hitName, para));
        }

        // Best paragraphs first; keep total under maxChars
        foreach (var item in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Index))
        {
            if (selectedIdx.Contains(item.Index)) continue;
            perNameUsed.TryGetValue(item.Name, out var usedForName);

            var slice = item.Para.Length > 1_600 ? item.Para[..1_600] + "…" : item.Para;
            var addCost = slice.Length + item.Name.Length + 32;
            if (usedForName > 0 && usedForName + addCost > perNameCap) continue;
            if (usedForName == 0 && addCost > perNameCap * 2) continue; // pathological mega-para
            if (totalUsed + addCost > maxChars) continue;

            selectedIdx.Add(item.Index);
            perNameUsed[item.Name] = usedForName + addCost;
            totalUsed += addCost;
        }

        // Emit in book order for readability
        var sb = new StringBuilder();
        foreach (var item in candidates
                     .Where(c => selectedIdx.Contains(c.Index))
                     .GroupBy(c => c.Index)
                     .Select(g => g.First())
                     .OrderBy(c => c.Index))
        {
            var slice = item.Para.Length > 1_600 ? item.Para[..1_600] + "…" : item.Para;
            var block = $"[{item.Name}] {slice}\n\n";
            if (sb.Length + block.Length > maxChars)
            {
                var room = maxChars - sb.Length - 20;
                if (room > 200)
                    sb.Append(block.AsSpan(0, Math.Min(room, block.Length))).Append("…\n");
                break;
            }
            sb.Append(block);
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly Regex LookLanguage = new(@"\b(hair|eyes?|wore|wearing|dressed|dress|coat|cloak|hat|beard|mustache|face|skin|pale|dark|"
        + @"fair|tall|short|thin|stout|slender|young|old|aged|years?\s+old|beautiful|handsome|"
        + @"blonde|blond|brunette|redhead|curly|bald|scar|freckle|wardrobe|gown|suit|boots?|"
        + @"complexion|features|figure|build|shoulder|cheek|lip|brow|forehead|chin|nose|"
        + @"species|fur|mane|paw|tail|whisker|feather|scale)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex NonAlphaNumericRegex = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ParagraphSplitRegex = new(@"\n\s*\n+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex MatchConsistentlyRegex = new(@"^Match\s+.+\s+consistently", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex MarkdownCodePrefixRegex = new(@"^```(?:json|text)?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex MarkdownCodeSuffixRegex = new(@"\s*```\s*$", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PhotorealMediumRegex = new(@"\b(photoreal|photo-?real|live[- ]?action)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static string SelectSpineWindows(string text, int maxChars, int windowCount)
    {
        text = NormalizeNewlines(text);
        if (text.Length <= maxChars) return text;
        windowCount = Math.Clamp(windowCount, 2, 8);

        // Leave room for separators between windows
        var sepOverhead = (windowCount - 1) * 40;
        var usable = Math.Max(1_000, maxChars - sepOverhead);
        var winLen = Math.Max(400, usable / windowCount);

        var sb = new StringBuilder(maxChars + 64);
        for (var w = 0; w < windowCount; w++)
        {
            int start;
            if (windowCount == 1)
                start = 0;
            else if (w == 0)
                start = 0;
            else if (w == windowCount - 1)
                start = Math.Max(0, text.Length - winLen);
            else
            {
                // Even interior anchors
                var frac = w / (double)(windowCount - 1);
                var center = (int)(frac * text.Length);
                start = Math.Clamp(center - winLen / 2, 0, Math.Max(0, text.Length - winLen));
            }

            var len = Math.Min(winLen, text.Length - start);
            // Snap to nearby newline to avoid mid-word cuts
            if (start > 0 && start < text.Length)
            {
                var searchLen = Math.Min(200, text.Length - start);
                var nl = text.IndexOf('\n', start, searchLen);
                if (nl >= start && nl < start + searchLen)
                {
                    start = nl + 1;
                    len = Math.Min(winLen, text.Length - start);
                }
            }

            if (len <= 0) continue;
            var slice = text.Substring(start, len).Trim();
            if (slice.Length == 0) continue;

            if (sb.Length > 0)
                sb.Append("\n\n[[… sample ").Append(w + 1).Append('/').Append(windowCount).Append(" …]]\n\n");
            sb.Append(slice);

            if (sb.Length >= maxChars)
                break;
        }

        if (sb.Length > maxChars)
            return sb.ToString(0, maxChars);
        return sb.ToString();
    }

    private static string NormalizeNewlines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string CollapseLookExcerptToSentence(string excerpt, string displayName)
    {
        if (string.IsNullOrWhiteSpace(excerpt)) return "";
        // Take first non-empty line/paragraph, cap length
        var line = excerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("[[", StringComparison.Ordinal) && l.Length > 20)
            ?? excerpt.Trim();
        line = WhitespaceRegex.Replace(line, " ").Trim();
        if (line.Length > 280) line = line[..277].TrimEnd() + "…";
        if (IsStubLook(line)) return "";
        // Prefer not to return pure label
        if (string.Equals(line, displayName, StringComparison.OrdinalIgnoreCase)) return "";
        return line;
    }

    /// <summary>Weak placeholder looks that must not be written as final seeds.</summary>
    public static bool IsStubLook(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Contains("as described in the screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("as in the screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("as described in the book", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("see screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (MatchConsistentlyRegex.IsMatch(t)) return true;
        if (t.Length < 12) return true;
        return false;
    }

    private static string StripFences(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = MarkdownCodePrefixRegex.Replace(text, "");
            text = MarkdownCodeSuffixRegex.Replace(text, "");
        }
        return text.Trim();
    }

    internal static Dictionary<string, object?> NormalizeCastDoc(
        Dictionary<string, object?> parsed,
        string projectId,
        string? bookText = null)
    {

        var outDoc = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["schema_version"] = "cast_seeds.v1",
            ["generation"] = new Dictionary<string, object?>
            {
                ["method"] = "CastFromScreenplayService",
                ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            },
        };

        if (parsed.TryGetValue(JsonKeys.MovieTitle, out var mt) && mt is not null)
            outDoc[JsonKeys.MovieTitle] = mt.ToString();
        else
            outDoc[JsonKeys.MovieTitle] = projectId;

        if (parsed.TryGetValue(KeyRenderStyleLock, out var rsl) && rsl is not null && !string.IsNullOrWhiteSpace(rsl.ToString()))
        {
            // Medium comes from cast model reading the screenplay — never from file type.
            outDoc[KeyRenderStyleLock] = rsl.ToString().Trim();
        }
        // If omitted, leave unset; CharacterDesignService reads Fountain Notes as fallback.

        // Film-level audience/performance conventions inferred from book (not hardcoded gaze recipes)
        if (parsed.TryGetValue(KeyPerformanceLock, out var pl) && pl is not null &&
            !string.IsNullOrWhiteSpace(pl.ToString()))
            outDoc[KeyPerformanceLock] = pl.ToString().Trim();

        var seedsIn = GetSeedsDict(parsed);
        var seedsOut = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in seedsIn)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var k = key.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
                ? key
                : JsonKeys.CharacterPrefix + NonAlphaNumericRegex.Replace(key, "_").Trim('_');
            if (string.IsNullOrWhiteSpace(k) || k == JsonKeys.CharacterPrefix) continue;

            var name = seed.TryGetValue(KeyCanonicalGivenName, out var cn) && cn is not null
                ? cn.ToString()
                : CastKindClassifier.StripPrefix(k).Replace('_', ' ');

            var off = string.Equals(
                seed.TryGetValue("display_name_policy", out var pol) ? pol?.ToString() : null,
                "never_on_screen",
                StringComparison.OrdinalIgnoreCase);

            // Prefer real model looks; never invent "as in the screenplay" stubs.
            var desc = CoerceString(seed, JsonKeys.Description) ?? "";
            if (IsStubLook(desc))
                desc = off ? $"{name} (voice only; not on screen)." : "";
            else if (off && string.IsNullOrWhiteSpace(desc))
                desc = $"{name} (voice only; not on screen).";

            var clean = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [JsonKeys.Description] = desc,
                [KeyCanonicalGivenName] = name,
                ["display_name_policy"] = off ? "never_on_screen" : "ok_anytime",
                ["species_kind"] = CoerceString(seed, "species_kind"),
                ["voice_label"] = CoerceString(seed, "voice_label") ?? name.Replace(' ', '_'),
                ["voice_profile"] = CoerceString(seed, "voice_profile")
                    ?? "Consistent character voice every scene.",
                [KeyReferenceImagePlaceholder] = CoerceString(seed, KeyReferenceImagePlaceholder)
                    ?? ProjectStore.CharacterRefFileName(k),
            };

            var vlock = CoerceString(seed, KeyVisualLock) ?? "";
            if (!off)
            {
                clean[KeyVisualLock] = IsStubLook(vlock) ? "" : vlock;
            }

            if (seed.TryGetValue(KeyWardrobeAlways, out var wa) && wa is List<object?> list)
                clean[KeyWardrobeAlways] = list;
            // Common model aliases
            if (!clean.ContainsKey(KeyWardrobeAlways) &&
                seed.TryGetValue("wardrobe", out var w2) && w2 is List<object?> list2)
                clean[KeyWardrobeAlways] = list2;
            if (seed.TryGetValue("source_image_pages", out var sip) && sip is List<object?> pages)
                clean["source_image_pages"] = pages;

            var perfNotes = CoerceString(seed, "performance_notes");
            if (!string.IsNullOrWhiteSpace(perfNotes))
                clean["performance_notes"] = perfNotes;

            // Shared uniform group (e.g. several police officers): the group's own
            // wardrobe_lock_tokens entry is the wardrobe source of truth, not this character's
            // own description/wardrobe_always — see EnsureWardrobeReferenceAsync.
            var wardrobeLock = CoerceString(seed, "wardrobe_lock");
            if (!string.IsNullOrWhiteSpace(wardrobeLock))
                clean["wardrobe_lock"] = NormalizeWardrobeKey(wardrobeLock);

            // cast_kind: prefer model output; else classify so pin gates skip group portraits.
            clean["cast_kind"] = ResolveCastKind(k, name, CoerceString(seed, "cast_kind"), desc);

            // Age-variant linking (see AGE-VARIANT CUES rule, fountain_to_cast.txt) — most
            // characters appear at one life stage and have neither field set.
            var ageBand = CoerceString(seed, "age_band");
            if (!string.IsNullOrWhiteSpace(ageBand))
                clean["age_band"] = ageBand.Trim();
            var variantOfRaw = CoerceString(seed, KeyVariantOf);
            if (!string.IsNullOrWhiteSpace(variantOfRaw))
            {
                var variantOfKey = variantOfRaw.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
                    ? variantOfRaw
                    : JsonKeys.CharacterPrefix + NonAlphaNumericRegex.Replace(variantOfRaw, "_").Trim('_');
                if (!string.Equals(variantOfKey, k, StringComparison.OrdinalIgnoreCase))
                    clean[KeyVariantOf] = variantOfKey;
            }

            seedsOut[k] = clean;
        }

        // Drop any variant_of that doesn't point at a real seed in this same cast (a
        // hallucinated or since-renamed base key) — a dangling pointer is worse than none:
        // the Characters UI would show a "sibling" that doesn't exist.
        foreach (var entry in seedsOut.Values)
        {
            if (entry is not Dictionary<string, object?> clean) continue;
            if (clean.TryGetValue(KeyVariantOf, out var vo) && vo is string voKey &&
                !seedsOut.ContainsKey(voKey))
                clean.Remove(KeyVariantOf);
        }

        outDoc[KeyCharacterSeedTokens] = seedsOut;

        var wardrobeIn = GetWardrobeLockDict(parsed);
        if (wardrobeIn.Count > 0)
        {
            var wardrobeOut = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawKey, rawVal) in wardrobeIn)
            {
                if (rawVal is not Dictionary<string, object?> entry) continue;
                var desc = CoerceString(entry, JsonKeys.Description) ?? "";
                if (string.IsNullOrWhiteSpace(desc)) continue;
                var k = NormalizeWardrobeKey(rawKey);
                var clean = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [JsonKeys.Description] = desc,
                    [KeyReferenceImagePlaceholder] = CoerceString(entry, KeyReferenceImagePlaceholder)
                        ?? ProjectStore.WardrobeRefFileName(k),
                };
                var vlock = CoerceString(entry, KeyVisualLock);
                if (!string.IsNullOrWhiteSpace(vlock))
                    clean[KeyVisualLock] = vlock;
                wardrobeOut[k] = clean;
            }
            if (wardrobeOut.Count > 0)
                outDoc[KeyWardrobeLockTokens] = wardrobeOut;
        }

        var locIn = GetLocationSeedsDict(parsed);
        if (locIn.Count > 0)
        {
            var locOut = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawKey, rawVal) in locIn)
            {
                if (rawVal is not Dictionary<string, object?> entry) continue;
                var k = NormalizeLocationKey(rawKey);
                if (string.IsNullOrWhiteSpace(k) || k == JsonKeys.LocationPrefix) continue;

                var display = CoerceString(entry, KeyDisplayName)
                              ?? CoerceString(entry, KeyCanonicalGivenName)
                              ?? k.Replace(JsonKeys.LocationPrefix, "").Replace('_', ' ');
                var desc = CoerceString(entry, JsonKeys.Description) ?? "";
                if (IsStubLook(desc) || desc.Equals(display, StringComparison.OrdinalIgnoreCase))
                    desc = "";
                var vlock = CoerceString(entry, KeyVisualLock) ?? "";
                if (IsStubLook(vlock) || vlock.Equals(display, StringComparison.OrdinalIgnoreCase))
                    vlock = "";

                var clean = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [KeyDisplayName] = display,
                    [JsonKeys.Description] = desc,
                    [KeyVisualLock] = string.IsNullOrWhiteSpace(vlock) ? desc : vlock,
                    ["location_type"] = CoerceString(entry, "location_type") ?? "",
                    [KeyReferenceImagePlaceholder] = CoerceString(entry, KeyReferenceImagePlaceholder)
                        ?? (k.ToLowerInvariant() + "_ref.png"),
                };
                // Skip empty AI stubs — Stage‑1 merge will fill later.
                if (string.IsNullOrWhiteSpace(clean[JsonKeys.Description]?.ToString())
                    && string.IsNullOrWhiteSpace(clean[KeyVisualLock]?.ToString()))
                    continue;
                locOut[k] = clean;
            }
            if (locOut.Count > 0)
                outDoc[KeyLocationSeedTokens] = locOut;
        }

        return outDoc;
    }

    /// <summary>Foreign-key-safe location key, e.g. "kirk street" → "Loc_Kirk_Street".</summary>
    private static string NormalizeLocationKey(string raw) =>
        raw.StartsWith(JsonKeys.LocationPrefix, StringComparison.OrdinalIgnoreCase)
            ? raw
            : JsonKeys.LocationPrefix + NonAlphaNumericRegex.Replace(raw, "_").Trim('_');

    /// <summary>Foreign-key-safe wardrobe group key, e.g. "police officer" → "Wardrobe_Police_Officer".</summary>
    private static string NormalizeWardrobeKey(string raw) =>
        raw.StartsWith("Wardrobe_", StringComparison.OrdinalIgnoreCase)
            ? raw
            : "Wardrobe_" + NonAlphaNumericRegex.Replace(raw, "_").Trim('_');

    private static Dictionary<string, object?> GetSeedsDict(Dictionary<string, object?> doc)
    {
        if (doc.TryGetValue(KeyCharacterSeedTokens, out var s) && s is Dictionary<string, object?> d)
            return d;
        // Offline fakes / older models sometimes use cast_seeds
        if (doc.TryGetValue("cast_seeds", out var cs) && cs is Dictionary<string, object?> dCs)
            return NormalizeLooseSeedMap(dCs);
        if (doc.TryGetValue(KeyGlobalProductionVariables, out var g) &&
            g is Dictionary<string, object?> gpv &&
            gpv.TryGetValue(KeyCharacterSeedTokens, out var s2) &&
            s2 is Dictionary<string, object?> d2)
            return d2;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> GetWardrobeLockDict(Dictionary<string, object?> doc)
    {
        if (doc.TryGetValue(KeyWardrobeLockTokens, out var s) && s is Dictionary<string, object?> d)
            return d;
        if (doc.TryGetValue(KeyGlobalProductionVariables, out var g) &&
            g is Dictionary<string, object?> gpv &&
            gpv.TryGetValue(KeyWardrobeLockTokens, out var s2) &&
            s2 is Dictionary<string, object?> d2)
            return d2;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map display_name / wardrobe aliases into seed shape.</summary>
    private static Dictionary<string, object?> NormalizeLooseSeedMap(Dictionary<string, object?> raw)
    {
        var outMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in raw)
        {
            if (val is not Dictionary<string, object?> seed)
            {
                outMap[key] = val;
                continue;
            }
            var copy = new Dictionary<string, object?>(seed, StringComparer.OrdinalIgnoreCase);
            if (!copy.ContainsKey(KeyCanonicalGivenName) &&
                copy.TryGetValue(KeyDisplayName, out var dn) && dn is not null)
                copy[KeyCanonicalGivenName] = dn.ToString();
            if (!copy.ContainsKey(KeyWardrobeAlways) &&
                copy.TryGetValue("wardrobe", out var w) && w is List<object?> wl)
                copy[KeyWardrobeAlways] = wl;
            outMap[key] = copy;
        }
        return outMap;
    }

    private static JsonElement GetSeedsElement(JsonElement root)
    {
        if (root.TryGetProperty(KeyCharacterSeedTokens, out var s) && s.ValueKind == JsonValueKind.Object)
            return s;
        if (root.TryGetProperty(KeyGlobalProductionVariables, out var g) &&
            g.TryGetProperty(KeyCharacterSeedTokens, out var s2) &&
            s2.ValueKind == JsonValueKind.Object)
            return s2;
        return default;
    }

    private static string? CoerceString(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString()?.Trim() : null;
}
