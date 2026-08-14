using System.Text;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;
using PageToMovie.Fountain;

using PageToMovie.Core.Utils;
namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Complete output of book adaptation (Adaptation-owned). Fountain remains clean screenplay
/// text while visual metadata is carried as <see cref="AdaptationVisionMeta"/>.
/// </summary>
public sealed record AdaptationConversionResult
{
    public required string Fountain { get; init; }
    public AdaptationVisionMeta? VisionMeta { get; init; }
    public AdaptationVisionMetaStatus VisionMetaStatus { get; init; }
    public string? VisionMetaError { get; init; }
    public AdaptationReport? AdaptationReport { get; init; }
    public AdaptationReportStatus AdaptationReportStatus { get; init; }
    public string? AdaptationReportError { get; init; }
}

/// <summary>
/// Optional structural-gate telemetry (Engine maps this to GenerationErrorLogger).
/// </summary>
public sealed class StructuralGateFailure
{
    public string Stage { get; init; } = "";
    public string Model { get; init; } = "";
    public string ErrorType { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public string? ResponseSummary { get; init; }
}

/// <summary>
/// Book text → editable Fountain via chat (<c>prompts/book_to_fountain.txt</c>).
/// Prefers a single full-book pass when input fits the model budget; multi-chunk
/// adapt → stitch → merge is a fallback for over-budget books or weak quality.
/// Pure Stage‑1 — no ProjectStore, no Engine references.
/// </summary>
public static class BookToFountainConverter
{
    /// <summary>
    /// Historical single-shot length threshold (also used as a "large book" floor in tests).
    /// Path selection now uses <see cref="ResolvePromptBudget"/> instead of this alone.
    /// </summary>
    public const int SingleShotMaxChars = 28_000;

    /// <summary>Default soft max book chars per adapt chunk when caller omits budget.</summary>
    public const int DefaultFallbackChunkSoftMaxChars = 16_000;

    /// <summary>Default cap on adapt calls for typical books (cost / latency).</summary>
    public const int MaxAdaptChunks = 8;

    /// <summary>
    /// Absolute ceiling on adapt calls even for very long books. <see cref="ResolveMaxChunks"/>
    /// scales past <see cref="MaxAdaptChunks"/> up to this when the book needs it, so a long
    /// novel doesn't dump everything past chunk N into one oversized final chunk.
    /// </summary>
    public const int AbsoluteMaxAdaptChunks = 24;

    /// <summary>Default max BOOK_TEXT chars for one chat call (large-context chat models).</summary>
    public const int DefaultSingleShotBookMaxChars = 120_000;

    /// <summary>Default soft max book chars per multi-chunk adapt call.</summary>
    public const int DefaultChunkSoftMaxChars = 40_000;

    /// <summary>Books shorter than this never use multi-chunk fallback (chunking won't help).</summary>
    public const int MinBookCharsForChunkFallback = 24_000;

    /// <summary>Product safety ceiling for a single book payload.</summary>
    public const int AbsoluteSingleShotCeiling = 400_000;

    /// <summary>Reserved chars for system prompt + scaffolding + continuity.</summary>
    public const int DefaultReservedOverheadChars = 12_000;

    public enum AdaptPath
    {
        Single,
        Multi,
        Indexed,
    }

    /// <summary>Per-model (or default) input budgets for book → Fountain.</summary>
    public sealed class PromptBudget
    {
        public required string ModelId { get; init; }

        /// <summary>Max chars of BOOK_TEXT in one chat call.</summary>
        public int SingleShotBookMaxChars { get; init; }

        /// <summary>Soft max book chars per adapt chunk.</summary>
        public int ChunkSoftMaxChars { get; init; }

        public int MaxChunks { get; init; }

        public int ReservedOverheadChars { get; init; }
    }

    /// <summary>Result of structural + coverage checks after a model draft.</summary>
    public sealed class QualityResult
    {
        public bool Ok { get; init; }
        public string Reason { get; init; } = "";
        public int SceneCount { get; init; }
        public int FountainChars { get; init; }
        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
        public bool HasHardFailure { get; init; }
    }

    /// <summary>
    /// Fallback body if <c>prompts/book_to_fountain.txt</c> is missing (tests / broken workspace).
    /// </summary>
    public const string FountainOutputOverride = """
        Act as an expert screenwriter. Adapt the book into Fountain 1.1 only (no JSON).
        Target runtime about {{TOTAL_RUNTIME_MINUTES}} minutes. Real INT./EXT. locations.
        No page numbers in the script. NARRATOR for narration; CHARACTER cues for speech.
        Closed cast. VO↔visual fidelity. No major invented plot.
        DIALOGUE: prefer the book’s actual spoken words — do not paraphrase iconic lines
        into generic modern dialogue (classics, verse, first-person monologues especially).
        """;

    private const string VisionMetaBegin = "---VISION_META---";
    private const string VisionMetaEnd = "---END_VISION_META---";
    private const string VisionMetaBeginNl = VisionMetaBegin + "\n";
    private const string VisionMetaEndNl = "\n" + VisionMetaEnd;
    private const string FountainJsonPath = "$.fountain";
    private const string UnusableScreenplayError =
        "Could not build a usable screenplay from the book. Try again or import a .fountain file.";

    private static readonly Regex VagueHeadingRegex = new(@"\b(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE)\b"
        + @"|\bDIFFERENT\s+(ROOMS?|PLACES?|LOCATIONS?)\b"
        + @"|\b(AROUND|THROUGHOUT)\s+THE\s+(HOUSE|HOME|BUILDING)\b"
        + @"|\b(VARIOUS|MULTIPLE|SEVERAL)\s+(ROOMS?|PLACES?|LOCATIONS?|AREAS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex CharacterNameSpaceRegex = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Generate Fountain from prepared book text and return its visual metadata as one explicit
    /// result. Single-shot first when the book fits the model budget; multi-chunk on budget miss
    /// or quality fail.
    /// </summary>
    public static async Task<AdaptationConversionResult> ConvertWithMetadataAsync(
        string title,
        string bookText,
        ChatCall chat,
        string? author = null,
        int? totalRuntimeMinutes = null,
        PromptBudget? budgetOverride = null,
        Action<string>? onHeuristicFallback = null,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure = null,
        IBookFileSession? bookSession = null,
        IFountainFileSession? fountainSession = null,
        string? visualMedium = null,
        AdaptationPromptTokens? promptTokens = null,
        ScreenplayIndex? index = null,
        string? indexFileId = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrWhiteSpace(bookText))
            throw new InvalidOperationException("Book text is empty");

        chat = chat with
        {
            Model = ProjectModelSelection.RequireExplicit(chat.Model, ModelCapability.Chat, "Screenplay generation"),
        };

        if (!chat.Chat.IsConfigured)
            throw new InvalidOperationException(
                "Connect service to build a screenplay draft from the book.");

        bookText = NormalizeBookText(bookText);
        var tokens = promptTokens ?? AdaptationPromptTokens.Default(totalRuntimeMinutes, visualMedium);
        var system = await BuildSystemPromptAsync(totalRuntimeMinutes, chat.Ct, tokens)
            .ConfigureAwait(false);
        var pageCount = CountPageMarkers(bookText);
        var budget = budgetOverride ?? ResolvePromptBudget(chat.Model);
        // null/≤0 = unlimited (prompt + soft quality); positive = artificial target.

        // Prefer xAI Files + Responses only when the book would NOT fit an inlined single-shot.
        // Small books (Yellow Wallpaper, short stories) go through chat/completions — the
        // Responses + file_id path was hanging for 10–20+ minutes with no progress, and
        // file_id is meant to avoid re-billing huge novels, not short texts.
        var bookFitsInline = FitsSingleShot(bookText, budget);

        var prevBook = Stage1BookSessionScope.Current;
        var prevFountain = Stage1FountainSessionScope.Current;
        Stage1BookSessionScope.Current = BeginBookFileSession(bookSession, bookFitsInline);
        Stage1FountainSessionScope.Current = fountainSession is { IsAvailable: true } ? fountainSession : null;
        try
        {
            await AnnounceBookSessionAsync(bookSession, bookFitsInline, chat.Progress)
                .ConfigureAwait(false);
            if (Stage1FountainSessionScope.Current is not null)
                chat.Report("Stage‑1 can attach the draft by file_id for merge and repairs.");

            var text = await AdaptFountainBodyAsync(
                system, title, author, pageCount, totalRuntimeMinutes, bookText,
                chat, budget, onStructuralGateFailure, onHeuristicFallback, bookFitsInline,
                index, indexFileId)
                .ConfigureAwait(false);

            var early = CaptureTrailerState(text);
            text = await ApplyGenerationRepairsAsync(
                system, early.Fountain, chat, onStructuralGateFailure).ConfigureAwait(false);

            text = FinalizeFountainText(text);
            WarnRemainingRepairIssues(text, bookText, chat.OnProgress);

            text = NormalizeFountainText(text);
            return await PackageConversionResultAsync(text, early, system, bookText, chat)
                .ConfigureAwait(false);
        }
        finally
        {
            Stage1BookSessionScope.Current = prevBook;
            Stage1FountainSessionScope.Current = prevFountain;
        }
    }

    private readonly record struct TrailerScan(bool BeginSeen, bool EndSeen)
    {
        public static TrailerScan Scan(string text, string begin, string end) => new(
            text.Contains(begin, StringComparison.OrdinalIgnoreCase),
            text.Contains(end, StringComparison.OrdinalIgnoreCase));

        public TrailerScan Merge(TrailerScan other) =>
            new(BeginSeen || other.BeginSeen, EndSeen || other.EndSeen);
    }

    private sealed class EarlyTrailerState
    {
        public required string Fountain { get; init; }
        public AdaptationVisionMeta? Vision { get; init; }
        public AdaptationReport? Report { get; init; }
        public required TrailerScan VisionMarkers { get; init; }
        public required TrailerScan ReportMarkers { get; init; }
    }

    private static IBookFileSession? BeginBookFileSession(IBookFileSession? bookSession, bool bookFitsInline)
    {
        var useFileSession = bookSession is { IsAvailable: true } && !bookFitsInline;
        return useFileSession ? bookSession : null;
    }

    private static async Task AnnounceBookSessionAsync(
        IBookFileSession? bookSession,
        bool bookFitsInline,
        ProgressCall progress)
    {
        if (Stage1BookSessionScope.Current is { } sess)
        {
            progress.Report("Stage‑1 using xAI file_id session (book uploaded once; follow-ups chain).");
            await sess.EnsureUploadedAsync(progress.Ct).ConfigureAwait(false);
        }
        else if (bookSession is { IsAvailable: true } && bookFitsInline)
        {
            progress.Report(
                "Book fits single-shot context — using chat (skipping file_id upload for speed/reliability).");
        }
    }

    private static async Task<string> AdaptFountainBodyAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int? totalRuntimeMinutes,
        string bookText,
        ChatCall chat,
        PromptBudget budget,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure,
        Action<string>? onHeuristicFallback,
        bool bookFitsInline,
        ScreenplayIndex? index = null,
        string? indexFileId = null)
    {
        try
        {
            var text = await AdaptViaPreferredPathAsync(
                system, title, author, pageCount, totalRuntimeMinutes, bookText,
                chat, budget, bookFitsInline, index, indexFileId)
                .ConfigureAwait(false);
            await EnforceMultiPathQualityAsync(
                text, bookText, totalRuntimeMinutes, chat.Model, onStructuralGateFailure, chat.Ct)
                .ConfigureAwait(false);
            return text;
        }
        catch (InvalidOperationException ex) when (LooksLikeGoodFountain(ConvertHeuristic(title, bookText, author)))
        {
            // Chat output failed structural gates — still give a usable draft from book text
            chat.Report("Model draft unusable — building structured draft from book text…");
            onHeuristicFallback?.Invoke(ex.Message);
            return ConvertHeuristic(title, bookText, author);
        }
    }

    private static async Task<string> AdaptViaPreferredPathAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int? totalRuntimeMinutes,
        string bookText,
        ChatCall chat,
        PromptBudget budget,
        bool bookFitsInline,
        ScreenplayIndex? index = null,
        string? indexFileId = null)
    {
        Task<string> ConvertMultiChunkFromBudgetAsync() => ConvertMultiChunkAsync(
            system, title, author, pageCount, totalRuntimeMinutes, bookText,
            chat, softMaxChars: budget.ChunkSoftMaxChars,
            maxChunks: ResolveMaxChunks(bookText, budget));

        if (index is not null && ShouldWriteFromIndex(bookText, chat.Model, index))
        {
            try
            {
                return await BookToIndexWriter.ConvertAsync(
                    system, title, author, index, indexFileId, chat, Stage1BookSessionScope.Current)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                chat.Report("Index write failed — falling back to multi-chunk: " + ex.Message);
                return await ConvertMultiChunkFromBudgetAsync().ConfigureAwait(false);
            }
        }

        // With a file session the full book is attached by id — single-shot is preferred
        // even when the inlined-token budget would force multi-chunk.
        var preferSingle = Stage1BookSessionScope.Current is not null || bookFitsInline;
        if (!preferSingle)
        {
            chat.Report("Book exceeds model budget — multi-chunk adapt…");
            return await ConvertMultiChunkFromBudgetAsync().ConfigureAwait(false);
        }

        chat.Report(
            Stage1BookSessionScope.Current is not null
                ? "Adapting book → Fountain (single pass, book via file_id)…"
                : "Adapting book → Fountain (single pass)…");
        var single = await TrySingleShotWithGateAsync(
            system, title, author, pageCount, totalRuntimeMinutes, bookText,
            chat, budget).ConfigureAwait(false);
        if (single is not null)
            return single;

        if (ShouldChunkFallback(bookText, budget) || Stage1BookSessionScope.Current is not null
            || bookFitsInline)
        {
            // Always offer multi-chunk after single-shot fail/timeout for short books too
            // (previously only when ShouldChunkFallback, which skips very short texts).
            chat.Report("Falling back to multi-chunk adapt…");
            return await ConvertMultiChunkFromBudgetAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException(UnusableScreenplayError);
    }

    public static bool ShouldWriteFromIndex(string bookText, string? model, ScreenplayIndex? index)
    {
        if (index is null) return false;
        var cards = ScreenplayIndexParser.EnumerateCards(index).Count();
        if (cards < 2) return false;
        return cards >= 8 || bookText.Length >= 60_000;
    }

    private static async Task EnforceMultiPathQualityAsync(
        string text,
        string bookText,
        int? totalRuntimeMinutes,
        string model,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure,
        CancellationToken ct)
    {
        // Multi path: soft coverage failures still accept a structurally good draft
        var multiGate = EvaluateQuality(text, bookText, totalRuntimeMinutes, AdaptPath.Multi);
        if (multiGate.Ok)
            return;

        // Visibility only — no automatic retry here (re-running multi-chunk adapt is
        // expensive, and we don't have real error-rate data yet to justify it). Hard
        // failures (structure/excerpt_marker) still throw below same as before; soft
        // failures (scene_count/missing_ending/suspiciously_short/runtime_short) previously shipped
        // silently with no log anywhere — now recorded for the admin panel.
        if (onStructuralGateFailure is not null)
        {
            await onStructuralGateFailure(new StructuralGateFailure
            {
                Stage = "book_to_fountain_chunk",
                Model = model,
                ErrorType = "structural_gate_failure",
                ErrorMessage = $"Multi-chunk quality gate failed: {multiGate.Reason} " +
                               $"(scenes={multiGate.SceneCount}, fountainChars={multiGate.FountainChars}, " +
                               $"hardFailure={multiGate.HasHardFailure})",
                ResponseSummary = text.Length > 500 ? text[..500] : text,
            }, ct).ConfigureAwait(false);
        }

        if (multiGate.HasHardFailure)
            throw new InvalidOperationException(UnusableScreenplayError);
    }

    private static EarlyTrailerState CaptureTrailerState(string text)
    {
        // Pull production / diagnostic sidecars before repairs (trailers are not Fountain body).
        var visionMarkers = TrailerScan.Scan(text, VisionMetaBegin, VisionMetaEnd);
        var reportMarkers = TrailerScan.Scan(
            text, AdaptationReportParser.StartMark, AdaptationReportParser.EndMark);
        var pulled = PullTrailersBeforeRepairs(text);
        return new EarlyTrailerState
        {
            Fountain = pulled.Fountain,
            Vision = pulled.Vision,
            Report = pulled.Report,
            VisionMarkers = visionMarkers,
            ReportMarkers = reportMarkers,
        };
    }

    private static async Task<string> ApplyGenerationRepairsAsync(
        string system,
        string text,
        ChatCall chat,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure)
    {
        // Generation repairs — no operator hand-edit path
        text = await RepairVagueLocationHeadingsAsync(system, text, chat).ConfigureAwait(false);
        text = NormalizeSceneHeadingWording(text);
        // NormalizeSceneHeadingWording only collapses a redundant-prefix alias ("OLD HOUSE -
        // HALL" -> "HALL"); it can't tell "SIONNA'S HOUSE" and "SIONNA'S DUPLEX" are the same
        // place — that's a judgment call, not a string shape, so ask the model.
        text = await RepairLocationDriftAsync(system, text, chat).ConfigureAwait(false);
        text = await RepairGenericNumberedSpeakersAsync(system, text, chat).ConfigureAwait(false);
        // Speaker naming repair only replaces unnamed placeholders such as FIRST OFFICER or MAN 2;
        // it has no concept of an already-named person's spelling drifting across mentions
        // (cues and prose) — separate problem, same "confirm then merge" shape as location drift.
        text = await RepairNameDriftAsync(system, text, chat).ConfigureAwait(false);
        // Continuous verse / V.O. narration split across a real blank line parses stanzas 2+
        // as silent Action (Fountain: a truly-empty line ends a dialogue block). Re-merge under
        // one cue with two-space stanza breaks so the narration is actually spoken.
        chat.Report("Checking narration continuity (split V.O. / verse)…");
        text = await RepairSplitNarrationAsync(system, text, chat, onStructuralGateFailure)
            .ConfigureAwait(false);
        chat.Report("Narration continuity checked.");
        return text;
    }

    private static string FinalizeFountainText(string text)
    {
        text = EnsureDraftDate(text);
        // Models invent wrong years (e.g. 3/25/2025) — stamp local today before save
        text = FixDraftDate(text);
        // Hard strip — models still emit tags even when the prompt forbids them
        text = StripBookPageTags(text);
        // Hard strip — models occasionally emit a Fountain page-break (===) right after
        // the title page; valid syntax, but nothing in the prompt asks for it here.
        text = StripFountainPageBreaks(text);
        // Belt-and-suspenders for the case above: at least once, the === was a straight
        // substitution for FADE IN: (not an addition alongside it), so stripping it left the
        // draft with no FADE IN: at all — observed in JungleBook's screenplay.fountain.
        text = EnsureFadeIn(text);
        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(UnusableScreenplayError);
        return text;
    }

    private static void WarnRemainingRepairIssues(string text, string bookText, Action<string>? onProgress)
    {
        var stillVague = FindVagueLocationHeadings(text);
        if (stillVague.Count > 0)
        {
            onProgress?.Invoke(
                $"Warning: vague location heading(s) remain after repair: {string.Join("; ", stillVague.Take(3))}");
        }

        var stillGeneric = FindGenericNumberedSpeakers(text);
        if (stillGeneric.Count > 0)
        {
            onProgress?.Invoke(
                $"Warning: generic numbered speaker(s) remain: {string.Join(", ", stillGeneric.Take(5))}");
        }

        var stillSplit = FindSplitNarrationBlocks(text);
        if (stillSplit.Count > 0)
        {
            onProgress?.Invoke(
                $"Warning: {stillSplit.Count} split narration block(s) remain after repair " +
                $"(verse under {string.Join(", ", stillSplit.Take(3).Select(s => s.CueDisplay))}).");
        }

        // Soft scene-count budget — warn only (Stage 2 clip cost), never block
        var analysis = BookTextAnalyzer.Analyze(bookText);
        var sceneCount = CountSceneHeadings(text);
        var softMax = SoftMaxSceneHeadings(analysis.BookKind.ToString());
        if (sceneCount > softMax)
        {
            onProgress?.Invoke(
                $"Note: {sceneCount} scene headings (soft target ≤{softMax} for {analysis.BookKind}) — " +
                "shot plan / clip count may be high. Consider merging same-location beats next pass.");
        }
    }

    private static async Task<AdaptationConversionResult> PackageConversionResultAsync(
        string text,
        EarlyTrailerState early,
        string system,
        string bookText,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        // In case a repair path re-introduced a trailer (should not), strip again.
        var lateVision = TrailerScan.Scan(text, VisionMetaBegin, VisionMetaEnd);
        var lateReport = TrailerScan.Scan(
            text, AdaptationReportParser.StartMark, AdaptationReportParser.EndMark);
        var (fountainOnly, visionLate, reportLate) = SplitAdaptationTrailers(text);
        var vision = visionLate ?? early.Vision;
        var report = reportLate ?? early.Report;
        (fountainOnly, vision, report) = await EnsureVisionMetaPresentAsync(
            fountainOnly, vision, report, system, bookText, chat)
            .ConfigureAwait(false);

        onProgress?.Invoke("Finalizing screenplay package…");
        if (vision is not null)
        {
            vision.DecidedBy = "adaptation";
            onProgress?.Invoke($"Visual medium from screenplay: {vision.VisualMedium}");
        }
        if (report is not null)
        {
            ReconcileReportRuntime(report, fountainOnly, onProgress);
            onProgress?.Invoke(
                $"Adaptation report: source_complete={report.SourceComplete}, " +
                $"issues={report.Issues.Count}, est_runtime={report.Metrics.EstRuntimeMin:0.#} min");
        }

        var visionMarkers = early.VisionMarkers.Merge(lateVision);
        var reportMarkers = early.ReportMarkers.Merge(lateReport);
        return new AdaptationConversionResult
        {
            Fountain = fountainOnly,
            VisionMeta = vision,
            VisionMetaStatus = ResolveVisionMetaStatus(vision, visionLate, early.Vision, visionMarkers.BeginSeen),
            VisionMetaError = ResolveVisionMetaError(vision, visionMarkers),
            AdaptationReport = report,
            AdaptationReportStatus = ResolveReportStatus(report, reportMarkers.BeginSeen),
            AdaptationReportError = ResolveReportError(report, reportMarkers),
        };
    }

    private static async Task<(string Fountain, AdaptationVisionMeta? Vision, AdaptationReport? Report)>
        EnsureVisionMetaPresentAsync(
            string fountainOnly,
            AdaptationVisionMeta? vision,
            AdaptationReport? report,
            string system,
            string bookText,
            ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        if (vision is not null)
            return (fountainOnly, vision, report);

        onProgress?.Invoke("Visual medium sidecar missing — asking model for VISION_META only (not re-writing the script)…");
        var repairedPackage = await RepairVisionMetaAsync(
            system, fountainOnly, bookText, chat).ConfigureAwait(false);
        var repairedSplit = SplitAdaptationTrailers(repairedPackage);
        fountainOnly = repairedSplit.Fountain;
        vision = repairedSplit.Vision;
        report ??= repairedSplit.Report;
        onProgress?.Invoke(vision is not null
            ? "Visual medium sidecar ready."
            : "Visual medium still missing — draft will save without it.");
        return (fountainOnly, vision, report);
    }

    private static AdaptationVisionMetaStatus ResolveVisionMetaStatus(
        AdaptationVisionMeta? vision,
        AdaptationVisionMeta? visionLate,
        AdaptationVisionMeta? visionEarly,
        bool visionMarkerSeen)
    {
        if (vision is not null)
        {
            if (visionLate is not null || visionEarly is not null)
                return AdaptationVisionMetaStatus.PrimaryResponse;
            return AdaptationVisionMetaStatus.RepairResponse;
        }
        if (visionMarkerSeen)
            return AdaptationVisionMetaStatus.Malformed;
        return AdaptationVisionMetaStatus.Missing;
    }

    private static string? ResolveVisionMetaError(AdaptationVisionMeta? vision, TrailerScan markers)
    {
        if (vision is not null)
            return null;
        if (markers.BeginSeen && !markers.EndSeen)
            return "VISION_META end delimiter is missing or its JSON is invalid.";
        if (markers.BeginSeen)
            return "VISION_META JSON is invalid.";
        return "VISION_META trailer is missing.";
    }

    private static AdaptationReportStatus ResolveReportStatus(AdaptationReport? report, bool markerSeen)
    {
        if (report is not null)
            return AdaptationReportStatus.Present;
        if (markerSeen)
            return AdaptationReportStatus.Malformed;
        return AdaptationReportStatus.Missing;
    }

    private static string? ResolveReportError(AdaptationReport? report, TrailerScan markers)
    {
        if (report is not null)
            return null;
        if (markers.BeginSeen && !markers.EndSeen)
            return "ADAPTATION_REPORT end delimiter is missing or its JSON is invalid.";
        if (markers.BeginSeen)
            return "ADAPTATION_REPORT JSON is invalid.";
        return null; // missing is normal for current production prompt
    }

    /// <summary>
    /// Ask only for the VISION_META JSON sidecar — never re-generate the full Fountain.
    /// Previous implementation re-sent the entire screenplay and hung for 10–20+ minutes after
    /// "Names checked" with no progress (especially on long drafts).
    /// </summary>
    private static async Task<string> RepairVisionMetaAsync(
        string system,
        string fountain,
        string bookText,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        var ct = chat.Ct;
        if (!chat.Chat.IsConfigured)
            return fountain;

        var user = BuildVisionMetaRepairUserPrompt(bookText, fountain);

        // Soft timeout — missing vision meta must not block draft save.
        const int softSeconds = 90;
        try
        {
            using var softCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            softCts.CancelAfter(TimeSpan.FromSeconds(softSeconds));
            using var heartbeat = StartProgressHeartbeat(
                onProgress,
                "Still fetching visual medium…",
                TimeSpan.FromSeconds(15));

            var result = await ExecuteStage1OperationAsync(
                chat with { Temperature = 0.1, Progress = new ProgressCall(softCts.Token, chat.OnProgress) },
                system, user,
                ChatCallModes.BookToFountainRetry,
                "VISION_META repair",
promptVersion: "stage1-vision-meta-repair-v2",
                correctionInstruction: $"Return only {VisionMetaBegin} JSON {VisionMetaEnd} with an allowed visual_medium.",
                validate: ValidateVisionMetaRepair,
                deterministicFallback: null,
                operationName: "stage1_vision_meta_repair").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(result))
                return fountain;

            return AttachRepairedVisionMeta(fountain, result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            onProgress?.Invoke($"Visual medium timed out after {softSeconds}s — saving draft without it.");
            return fountain;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke("Visual medium skipped: " + ex.Message);
            return fountain;
        }
    }

    private static string BuildVisionMetaRepairUserPrompt(string bookText, string fountain)
    {
        // Evidence only — not a full rewrite payload.
        var bookContext = bookText.Length <= 6_000 ? bookText : bookText[..6_000];
        var fountainSample = fountain.Length <= 4_000 ? fountain : fountain[..4_000];
        return $$"""
            VISION_META ONLY (do not rewrite the screenplay)
            Return ONLY the sidecar block below — no Fountain body, no markdown fences.

            {{VisionMetaBegin}}
            {"visual_medium":"live_action|illustrated_picture_book|mixed","render_style_lock":"specific reusable style lock","notes":"brief evidence"}
            {{VisionMetaEnd}}

            Allowed visual_medium values: live_action, illustrated_picture_book, mixed.
            Pick one based on the book excerpt and screenplay sample.

            Source-book excerpt:
            {{bookContext}}

            Screenplay sample (title page + opening only):
            {{fountainSample}}
            """;
    }

    private static IReadOnlyList<Stage1ValidationIssue> ValidateVisionMetaRepair(string value)
    {
        var split = SplitVisionMetaTrailer(
            value.Contains(VisionMetaBegin, StringComparison.OrdinalIgnoreCase)
                ? value
                : VisionMetaBeginNl + value.Trim() + VisionMetaEndNl);
        var issues = new List<Stage1ValidationIssue>();
        if (split.Vision is null)
            issues.Add(new("missing_vision_meta", "A valid VISION_META sidecar is required.", "$.vision_meta"));
        return issues;
    }

    private static string WrapVisionSidecarIfNeeded(string result) =>
        result.Contains(VisionMetaBegin, StringComparison.OrdinalIgnoreCase)
            ? result
            : VisionMetaBeginNl + result.Trim() + VisionMetaEndNl;

    private static string ChooseVisionMetaPackage(string fountain, string normalized)
    {
        if (!normalized.Contains(VisionMetaEnd, StringComparison.OrdinalIgnoreCase))
            return fountain.TrimEnd() + "\n\n" + normalized;
        if (normalized.Contains("Title:", StringComparison.OrdinalIgnoreCase))
            return normalized;
        return fountain.TrimEnd() + "\n\n" + normalized;
    }

    private static bool VisionMetaPackageIsUsable(
        string originalFountain,
        AdaptationVisionMeta? vision,
        string candidateFountain) =>
        vision is not null
        && LooksLikeGoodFountain(candidateFountain)
        && CountSceneHeadings(candidateFountain) >= Math.Max(1, CountSceneHeadings(originalFountain) / 2);

    private static string FormatVisionSidecar(string fountain, AdaptationVisionMeta vision) =>
        fountain.TrimEnd() + "\n\n" + VisionMetaBeginNl
        + System.Text.Json.JsonSerializer.Serialize(new
        {
            visual_medium = vision.VisualMedium,
            render_style_lock = vision.RenderStyleLock,
            notes = vision.Notes,
        })
        + VisionMetaEndNl + "\n";

    private static AdaptationVisionMeta? ParseVisionMetaOnly(string normalized, string result)
    {
        var metaOnly = SplitVisionMetaTrailer(
            normalized.Contains(VisionMetaBegin, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : VisionMetaBeginNl + normalized + VisionMetaEndNl);
        if (metaOnly.Vision is not null)
            return metaOnly.Vision;
        // Try treating whole result as JSON
        var wrapped = VisionMetaBeginNl + result.Trim() + VisionMetaEndNl;
        return SplitVisionMetaTrailer(wrapped).Vision;
    }

    private static string AttachRepairedVisionMeta(string fountain, string result)
    {
        var normalized = WrapVisionSidecarIfNeeded(result);
        var splitResult = SplitVisionMetaTrailer(ChooseVisionMetaPackage(fountain, normalized));
        if (VisionMetaPackageIsUsable(fountain, splitResult.Vision, splitResult.Fountain))
            return FormatVisionSidecar(splitResult.Fountain, splitResult.Vision!);

        var meta = ParseVisionMetaOnly(normalized, result);
        if (meta is null)
            return fountain;
        return FormatVisionSidecar(fountain, meta);
    }

    /// <summary>Periodic progress while a long Stage‑1 model call is in flight.</summary>
    private static IDisposable StartProgressHeartbeat(
        Action<string>? onProgress,
        string messagePrefix,
        TimeSpan period)
    {
        if (onProgress is null) return EmptyDisposable.Instance;
        var started = DateTimeOffset.UtcNow;
        var timer = new Timer(_ =>
        {
            try
            {
                var elapsed = DateTimeOffset.UtcNow - started;
                onProgress(
                    $"{messagePrefix} ({(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s)");
            }
            catch { /* ignore */ }
        }, null, period, period);
        return timer;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private static (string Fountain, AdaptationVisionMeta? Vision, AdaptationReport? Report)
        PullTrailersBeforeRepairs(string text)
    {
        var split = SplitAdaptationTrailers(text);
        return (split.Fountain, split.Vision, split.Report);
    }

    /// <summary>
    /// Strip trailing ---VISION_META--- JSON ---END_VISION_META--- written by book-to-Fountain LLM.
    /// Fountain body is the screenplay file; JSON is production medium metadata.
    /// </summary>
    /// <summary>
    /// Strip trailing VISION_META and optional ADAPTATION_REPORT sidecars from model output.
    /// Fountain body is the screenplay file only — diagnostics never leak into the draft.
    /// </summary>
    public static (string Fountain, AdaptationVisionMeta? Vision) SplitVisionMetaTrailer(string? text)
    {
        var (fountain, vision, _) = SplitAdaptationTrailers(text);
        return (fountain, vision);
    }

    /// <summary>
    /// Strip <c>---VISION_META---</c> and <c>---ADAPTATION_REPORT---</c> trailers.
    /// Order in the model response: Fountain, then vision meta, then adaptation report.
    /// Either sidecar may be absent (older prompts / heuristic path).
    /// </summary>
    public static (string Fountain, AdaptationVisionMeta? Vision, AdaptationReport? Report)
        SplitAdaptationTrailers(string? text)
    {
        text ??= "";
        AdaptationReport? report = null;
        AdaptationVisionMeta? vision = null;

        // Strip adaptation report first (last sidecar) so it never re-enters the fountain.
        text = ExtractSidecar(
            text,
            AdaptationReportParser.StartMark,
            AdaptationReportParser.EndMark,
            out var reportJson);
        if (!string.IsNullOrWhiteSpace(reportJson))
            report = AdaptationReportParser.ParseModelJson(reportJson);

        // Then vision meta.
        text = ExtractSidecar(
            text,
            VisionMetaBegin,
            VisionMetaEnd,
            out var visionJson);
        if (!string.IsNullOrWhiteSpace(visionJson))
        {
            vision = AdaptationVisionMetaParser.ParseModelJson(visionJson);
            if (vision is not null)
                vision.DecidedBy = "adaptation";
        }

        var fountain = text.TrimEnd();
        if (!fountain.EndsWith('\n'))
            fountain += "\n";
        return (fountain, vision, report);
    }

    /// <summary>
    /// Remove one marked sidecar block; returns remaining text and JSON body (or null).
    /// </summary>
    private static string ExtractSidecar(
        string text,
        string startMark,
        string endMark,
        out string? jsonBody)
    {
        jsonBody = null;
        var start = text.LastIndexOf(startMark, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return text;

        var jsonStart = start + startMark.Length;
        var end = text.IndexOf(endMark, jsonStart, StringComparison.OrdinalIgnoreCase);
        string json;
        string remaining;
        if (end < 0)
        {
            json = text[jsonStart..].Trim();
            remaining = text[..start].TrimEnd();
        }
        else
        {
            json = text[jsonStart..end].Trim();
            remaining = (text[..start] + text[(end + endMark.Length)..]).TrimEnd();
        }

        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            var fence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) json = json[..fence];
            json = json.Trim();
        }

        jsonBody = string.IsNullOrWhiteSpace(json) ? null : json;
        return remaining;
    }

    /// <summary>
    /// Scene headings that use multi-place filler language (VARIOUS, MULTIPLE, …).
    /// Detected on raw heading text so "HOUSE - VARIOUS ROOMS" is caught before sanitize.
    /// </summary>
    public static IReadOnlyList<string> FindVagueLocationHeadings(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<string>();

        return EnumerateSceneHeadingLines(fountain)
            .Where(h => h.Length > 0 && HeadingContainsVagueLocationLanguage(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True if a scene heading line contains non-filmable multi-place filler.</summary>
    public static bool HeadingContainsVagueLocationLanguage(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading)) return false;
        return VagueHeadingRegex.IsMatch(heading);
    }

    /// <summary>
    /// Executes a versioned Stage 1 request through the shared transport/correction lifecycle.
    /// </summary>
    private static async Task<string?> ExecuteStage1OperationAsync(
        ChatCall chat,
        string system,
        string user,
        string mode,
        string retryLabel,
        string promptVersion = "stage1-primary-v1",
        string correctionInstruction = "Fix the reported structural problems without changing book-faithful story content.",
        Func<string, IReadOnlyList<Stage1ValidationIssue>>? validate = null,
        string? deterministicFallback = null,
        string operationName = "stage1_book_to_fountain",
        string? fountainForFile = null)
    {
        validate ??= static value => string.IsNullOrWhiteSpace(value)
            ? [new Stage1ValidationIssue("empty_response", "The response was empty.")]
            : Array.Empty<Stage1ValidationIssue>();

        IFountainFileSession? fountainSession = null;
        var userToSend = user;
        if (!string.IsNullOrWhiteSpace(fountainForFile))
        {
            fountainSession = Stage1FountainSessionScope.Current;
            if (fountainSession is { IsAvailable: true })
            {
                try
                {
                    await fountainSession.EnsureUploadedAsync(fountainForFile, chat.Ct).ConfigureAwait(false);
                    chat.Report("Screenplay attached by file_id (no body resend).");
                    userToSend = user.TrimEnd() +
                        "\n\nThe attached file is the Fountain draft. Return the complete Fountain only.";
                }
                catch (Exception ex)
                {
                    chat.Report("Fountain file_id unavailable — inlining draft: " + ex.Message);
                    fountainSession = null;
                }
            }
            if (fountainSession is null)
            {
                userToSend = user.TrimEnd() +
                    "\n\n--- BEGIN FOUNTAIN ---\n" + fountainForFile + "\n--- END FOUNTAIN ---\n";
            }
        }

        using var heartbeat = Stage1ProgressHeartbeat.Start(chat.OnProgress, retryLabel);
        var result = await Stage1ChatExecutor.ExecuteAsync(
            chat.Chat,
            new Stage1ChatExecutor.Request(
                system, userToSend, chat.Model, chat.Temperature, mode, promptVersion,
                correctionInstruction, chat.ReasoningEffort, deterministicFallback, operationName),
            validate,
            chat.Ct,
            Stage1BookSessionScope.Current,
            fountainSession).ConfigureAwait(false);
        if (result.Source == Stage1ResultSource.CorrectiveResponse)
            chat.Report($"{retryLabel} corrected after validation.");
        else if (!result.Success)
            chat.Report($"{retryLabel} failed validation.");
        return result.Value?.FountainPackage;
    }

    /// <summary>
    /// One automatic rewrite pass when the draft still has vague multi-place headings.
    /// Generation path — do not require operator hand-edits.
    /// </summary>
    private static async Task<string> RepairVagueLocationHeadingsAsync(
        string system,
        string fountain,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        var bad = FindVagueLocationHeadings(fountain);
        if (bad.Count == 0 || !chat.Chat.IsConfigured)
            return fountain;

        onProgress?.Invoke(
            $"Repairing {bad.Count} vague location heading(s) (must be concrete rooms)…");

        var listed = string.Join("\n", bad.Select(h => "  - " + h));
        var user = $"""
            LOCATION HEADING REPAIR (HARD)
            The Fountain draft below is almost ready, but these scene headings use vague
            multi-place language that cannot be filmed as a single location:

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - Rewrite ONLY those bad headings (and adjust Action if a heading is removed).
            - Every heading must name 1–2 concrete, filmable places a crew can light/dress.
            - Forbidden in headings: VARIOUS, VARIOUS ROOMS, MULTIPLE, MULTIPLE LOCATIONS,
              SEVERAL, SEVERAL ROOMS, ELSEWHERE, DIFFERENT ROOMS/PLACES/LOCATIONS,
              AROUND THE HOUSE, THROUGHOUT THE HOUSE.
            - Good replacements: INT. HALL AND SITTING ROOM - NIGHT, INT. STAIRS AND HALL - NIGHT.
              Or drop the heading and fold a brief walk into the Action of the following scene.
            - Do not change plot, cast tokens, or dialogue wording except as needed for heading fixes.
            - No markdown fences. Fountain only.
            """;

        try
        {
            var raw = await ExecuteStage1OperationAsync(
                    chat with { Temperature = 0.1 }, system, user,
                    ChatCallModes.BookToFountainLocationsRetry,
                    "Location repair",
promptVersion: "stage1-location-heading-repair-v1",
                    correctionInstruction: "Rewrite every remaining vague scene heading as one or two concrete filmable locations.",
                    validate: value => ValidateFountainRepair(value, FindVagueLocationHeadings, "vague_heading"),
                    deterministicFallback: fountain,
                    operationName: "stage1_location_heading_repair",
                    fountainForFile: fountain).ConfigureAwait(false);
            if (raw is null)
            {
                onProgress?.Invoke("Location repair failed twice — keeping prior draft.");
                return fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                onProgress?.Invoke("Location repair unusable — keeping prior draft.");
                return fountain;
            }

            var remaining = FindVagueLocationHeadings(repaired);
            if (remaining.Count < bad.Count)
            {
                onProgress?.Invoke(
                    remaining.Count == 0
                        ? "Location headings repaired."
                        : $"Location repair partial — {remaining.Count} vague heading(s) left.");
                return repaired;
            }

            onProgress?.Invoke("Location repair did not clear vague headings — keeping prior draft.");
            return fountain;
        }
        catch (Exception)
        {
            onProgress?.Invoke("Location repair failed — keeping prior draft.");
            return fountain;
        }
    }

    // Location drift: same place, different wording — a judgment call, not a string shape.
    // The wording normalizer above only collapses a redundant-prefix alias.

    /// <summary>
    /// Groups unique scene-heading location names that share a distinguishing first word (4+
    /// chars) — candidates the model might confirm are the same place ("SIONNA'S HOUSE" /
    /// "SIONNA'S DUPLEX") or reject as genuinely different. Not a verdict, just a cheap filter
    /// so the retry below only fires when there's something to check.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindLocationDriftCandidateGroups(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<IReadOnlyList<string>>();

        var locNames = EnumerateSceneHeadingLines(fountain)
            .Select(h => SplitSceneHeadingParts(h).LocName)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return locNames
            .GroupBy(
                l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } w ? w[0] : l,
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length >= 4 && g.Count() >= 2)
            .Select(g => (IReadOnlyList<string>)g.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            .Where(g => g.Count >= 2)
            // Members that differ ONLY by a trailing number ("ROOM 1" / "ROOM 3") are a
            // deliberately-enumerated set of distinct locations, not spelling drift — skip the
            // group entirely rather than asking the model to referee an intentional naming scheme.
            .Where(g => g.Select(StripTrailingLocationNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();
    }

    private static readonly Regex TrailingLocationNumberRegex = new(@"\s*#?\d+\s*$", RegexOptions.Compiled, CommonRegex.Timeout);

    private static string StripTrailingLocationNumber(string locName) =>
        TrailingLocationNumberRegex.Replace(locName, "").Trim();

    /// <summary>
    /// Chat pass: confirm which candidate location groups are truly the same place and unify
    /// their wording; leave genuinely-different places untouched. Fires only when the cheap
    /// pre-check above finds a candidate — most books never trigger this call.
    /// </summary>
    private static async Task<string> RepairLocationDriftAsync(
        string system,
        string fountain,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        var groups = FindLocationDriftCandidateGroups(fountain);
        if (groups.Count == 0 || !chat.Chat.IsConfigured)
            return fountain;

        onProgress?.Invoke($"Checking {groups.Count} possible duplicate location name(s)…");

        var listed = string.Join("\n\n", groups.Select((g, i) =>
            $"  Group {i + 1}:\n" + string.Join("\n", g.Select(h => "    - " + h))));
        var user = $"""
            LOCATION NORMALIZATION (CONFIRM, THEN FIX)
            The Fountain draft below may describe the SAME physical place with different wording
            across scenes. Each group below shares a location word and MIGHT be one place
            described inconsistently — or might genuinely be different places. Decide each group
            on its own.

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - For a group that IS the same place, rewrite every heading in it to one canonical
              wording (keep each scene's own DAY/NIGHT/time-of-day suffix unchanged).
            - For a group that is genuinely different places, leave every heading in it exactly
              as written — do not merge unrelated locations just because they share a word.
            - Do not change plot, cast tokens, dialogue wording, or any heading outside these
              groups.
            - No markdown fences. Fountain only.
            """;

        return await ExecuteNormalizationPassAsync(new(
            system, fountain, user, chat with { Temperature = 0.1 },
            ChatCallModes.BookToFountainLocationNormalizeRetry,
            "Location normalization",
            "stage1-location-normalize-v1",
            "stage1_location_normalize",
            "Location names checked.")).ConfigureAwait(false);
    }

    private readonly record struct NormalizationPass(
        string System,
        string Fountain,
        string User,
        ChatCall Chat,
        string Mode,
        string RetryLabel,
        string PromptVersion,
        string OperationName,
        string SuccessMessage);

    /// <summary>
    /// Shared execute/validate/fallback for location-name and character-name normalization.
    /// Both passes ask the model to unify aliases or leave genuine differences; neither can
    /// re-run its candidate finder as a pass/fail signal.
    /// </summary>
    private static async Task<string> ExecuteNormalizationPassAsync(NormalizationPass pass)
    {
        try
        {
            var raw = await ExecuteStage1OperationAsync(
                    pass.Chat, pass.System, pass.User,
                    pass.Mode,
                    pass.RetryLabel,
                    promptVersion: pass.PromptVersion,
                    correctionInstruction: "Return the complete Fountain screenplay again — valid Fountain formatting throughout.",
                    validate: ValidateNormalizationRepair,
                    deterministicFallback: pass.Fountain,
                    operationName: pass.OperationName,
                    fountainForFile: pass.Fountain).ConfigureAwait(false);
            if (raw is null)
            {
                pass.Chat.Report($"{pass.RetryLabel} failed twice — keeping prior draft.");
                return pass.Fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                pass.Chat.Report($"{pass.RetryLabel} unusable — keeping prior draft.");
                return pass.Fountain;
            }

            pass.Chat.Report(pass.SuccessMessage);
            return repaired;
        }
        catch (Exception)
        {
            pass.Chat.Report($"{pass.RetryLabel} failed — keeping prior draft.");
            return pass.Fountain;
        }
    }

    /// <summary>
    /// Structural-only validation for the normalize passes below: unlike vague-heading/generic-
    /// speaker repair, a "candidate" here isn't necessarily wrong — the model may correctly
    /// decide two candidates are different and leave both alone, so re-running the same
    /// candidate finder after repair can't be the pass/fail signal.
    /// </summary>
    private static IReadOnlyList<Stage1ValidationIssue> ValidateNormalizationRepair(string fountain)
    {
        if (!LooksLikeGoodFountain(fountain))
            return [new Stage1ValidationIssue("invalid_fountain", "The response is not a usable Fountain screenplay.", FountainJsonPath)];
        return Array.Empty<Stage1ValidationIssue>();
    }

    /// <summary>
    /// Character cues that are ordinal/numbered role placeholders
    /// (FIRST OFFICER, SECOND MERCHANT, BUSINESSMAN 2) — unstable cast keys.
    /// </summary>
    public static IReadOnlyList<string> FindGenericNumberedSpeakers(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<string>();

        return EnumerateCharacterCueNames(fountain)
            .Where(n => n.Length > 0 && IsGenericNumberedSpeaker(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly Regex RoleNounPrefixRegex = new(@"^(FIRST|SECOND|THIRD|FOURTH|FIFTH|SIXTH|SEVENTH|EIGHTH|NINTH|TENTH|1ST|2ND|3RD|4TH|5TH)\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex RoleNumberRegex = new(@"^(OFFICER|POLICE|POLICEMAN|POLICE\s+OFFICER|GUARD|SOLDIER|DETECTIVE|AGENT|COP|DEPUTY|TROOPER|"
        + @"BUSINESSMAN|BUSINESS\s*MAN|MERCHANT|GENTLEMAN|GENTLEMEN|LADY|GUEST|SERVANT|CLERK|PORTER|"
        + @"WAITER|MAID|NURSE|DOCTOR|LAWYER|SAILOR|CREWMAN|SOLDIER|CITIZEN|MAN|WOMAN|BOY|GIRL|"
        + @"ATTENDANT|MESSENGER|COURIER|DRIVER|COACHMAN|FOOTMAN|BUTLER|COOK|WORKMAN|LABORER|"
        + @"VILLAGER|TOWNSMAN|SHOPKEEPER|CUSTOMER|PATIENT|PRISONER|INMATE|SOLDIER|SAILOR|"
        + @"SUITOR|SUITORS|CREW|SAILORS|SOLDIERS|GUARDS|ELDER|ELDERS|MAIDEN|MAIDS)\s*[#]?\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex RoleWordNumberRegex = new(@"^(OFFICER|POLICE|POLICE\s+OFFICER|GUARD|SOLDIER|DETECTIVE|AGENT|DEPUTY|BUSINESSMAN|"
        + @"MERCHANT|GENTLEMAN|GUEST|SERVANT|CLERK|MAN|WOMAN|SUITOR|CREW|SAILOR)\s+"
        + @"(ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TrailingDigitRegex = new(@"\b\d{1,2}$", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex RoleNounMatchRegex = new(@"\b(OFFICER|MERCHANT|BUSINESS|GENTLEMAN|GUEST|SERVANT|MAN|WOMAN|CLERK)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ScenePrefixRegex = new(@"^(INT\./EXT|INT/EXT|I\./E|I/E|INT\.?|EXT\.?|EST\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DraftDateRegex = new(@"(?im)^(Draft date:)\s*.*$", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TruncatMarkerRegex = new(@"\[\[.*(truncat|omitted for length|cut off|excerpted).*\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex FadeOutEndingRegex = new(@"(?im)(FADE OUT|THE END)\b", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// True for ordinal/numbered role placeholders: FIRST BUSINESSMAN, SECOND MERCHANT,
    /// OFFICER 1, GUEST #2, MAN 3, etc. Named people (SCROOGE, OFFICER REYNOLDS) are false.
    /// </summary>
    public static bool IsGenericNumberedSpeaker(string? characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return false;
        var n = CharacterNameSpaceRegex.Replace(characterName.Trim(), " ");

        // FIRST/SECOND/… + any role noun (OFFICER, BUSINESSMAN, MERCHANT, GUEST, …)
        if (RoleNounPrefixRegex.IsMatch(n))
            return true;

        // Role + number / #number (broad role list)
        if (RoleNumberRegex.IsMatch(n))
            return true;

        // Role + ONE/TWO/THREE…
        if (RoleWordNumberRegex.IsMatch(n))
            return true;

        // Trailing digit on multi-word ALL-CAPS role: "STOCK EXCHANGE MAN 1"
        if (TrailingDigitRegex.IsMatch(n) && RoleNounMatchRegex.IsMatch(n))
            return true;

        return false;
    }

    /// <summary>
    /// Soft scene-heading budget for operator warnings (not a hard fail).
    /// picture_book ≤20, short ≤22, novel ≤45 for a short-film cut.
    /// </summary>
    public static int SoftMaxSceneHeadings(string? bookKind) =>
        (bookKind ?? "").ToLowerInvariant() switch
        {
            "picture_book" => 20,
            "short" => 22,
            "novel" => 45,
            _ => 30,
        };

    /// <summary>
    /// Chat repair: replace generic numbered speakers with stable proper-name tokens.
    /// </summary>
    private static async Task<string> RepairGenericNumberedSpeakersAsync(
        string system,
        string fountain,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        var bad = FindGenericNumberedSpeakers(fountain);
        if (bad.Count == 0 || !chat.Chat.IsConfigured)
            return fountain;

        onProgress?.Invoke(
            $"Naming {bad.Count} generic numbered speaker(s) (stable cast tokens)…");

        var listed = string.Join("\n", bad.Select(n => "  - " + n));
        var user = $"""
            SPEAKER NAMING REPAIR (HARD)
            The Fountain draft below uses generic numbered / ordinal role cues that make
            unstable cast keys for production (portraits, continuity, shot plans):

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - Replace EVERY occurrence of those cues (including CONT'D / V.O. / O.S. lines)
              with a proper ALL-CAPS name token. Examples:
                FIRST OFFICER → OFFICER REYNOLDS
                SECOND MERCHANT → MERCHANT HALES
                FIRST BUSINESSMAN → MR. TOPPER (or a period surname)
                MAN 2 / GUEST #3 → named people, not numbers
            - Invent period-appropriate given names or surnames if the book is silent.
            - Same person = same token every time. Distinct people = distinct tokens.
            - Do not leave FIRST/SECOND/THIRD, OFFICER 1, BUSINESSMAN 2, MERCHANT #1, etc.
            - Do not change plot, locations, or book-faithful dialogue wording except the cue names.
            - No markdown fences. Fountain only.
            """;

        try
        {
            var raw = await ExecuteStage1OperationAsync(
                    chat with { Temperature = 0.15 }, system, user,
                    ChatCallModes.BookToFountainSpeakersRetry,
                    "Speaker naming repair",
promptVersion: "stage1-generic-speaker-repair-v1",
                    correctionInstruction: "Replace every remaining generic numbered or ordinal character cue with stable proper-name tokens.",
                    validate: value => ValidateFountainRepair(value, FindGenericNumberedSpeakers, "generic_speaker"),
                    deterministicFallback: fountain,
                    operationName: "stage1_generic_speaker_repair",
                    fountainForFile: fountain)
                .ConfigureAwait(false);
            if (raw is null)
            {
                onProgress?.Invoke("Speaker naming repair failed twice — keeping prior draft.");
                return fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                onProgress?.Invoke("Speaker naming repair unusable — keeping prior draft.");
                return fountain;
            }

            var remaining = FindGenericNumberedSpeakers(repaired);
            if (remaining.Count < bad.Count)
            {
                onProgress?.Invoke(
                    remaining.Count == 0
                        ? "Generic speakers named."
                        : $"Speaker naming partial — {remaining.Count} generic cue(s) left.");
                return repaired;
            }

            onProgress?.Invoke("Speaker naming did not clear generic cues — keeping prior draft.");
            return fountain;
        }
        catch (Exception)
        {
            onProgress?.Invoke("Speaker naming repair failed — keeping prior draft.");
            return fountain;
        }
    }

    private static IReadOnlyList<Stage1ValidationIssue> ValidateFountainRepair(
        string fountain,
        Func<string?, IReadOnlyList<string>> findRemaining,
        string issueCode)
    {
        var issues = new List<Stage1ValidationIssue>();
        if (!LooksLikeGoodFountain(fountain))
            issues.Add(new("invalid_fountain", "The response is not a usable Fountain screenplay.", FountainJsonPath));
        foreach (var remaining in findRemaining(fountain))
            issues.Add(new(issueCode, $"Unresolved value: {remaining}", FountainJsonPath));
        return issues;
    }

    // ── name drift (same person, spelling drifted across mentions — cues AND prose; distinct
    // from RepairGenericNumberedSpeakersAsync above, which only names *unnamed* placeholders
    // like "MAN 2" and has no concept of an already-named person's spelling drifting) ───────

    private static readonly Regex ProperNounWordRegex = new(@"\b[A-Z][a-z]{2,}\b", RegexOptions.Compiled, CommonRegex.Timeout);

    // Common capitalized sentence-openers/pronouns — excluded so the prose scan below isn't
    // dominated by ordinary sentence-initial words that aren't anyone's name.
    private static readonly HashSet<string> ProseNameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "He", "She", "It", "They", "We", "You", "I",
        "But", "And", "So", "Then", "When", "If", "There", "Here", "Her", "His", "Its", "Their",
        "Mom", "Dad", "Ma", "Pa", "Mr", "Mrs", "Ms", "Dr", "Sir", "Madam", "Ok", "Okay", "Yes", "No",
    };

    /// <summary>
    /// Distinct proper-noun-shaped words (Title-case, 3+ letters) appearing in Action/dialogue
    /// prose — not scene headings or character cue lines. Cheap stand-in for named-entity
    /// recognition: real names recur; a handful of false positives are fine since the model
    /// confirms membership before merging anything.
    /// </summary>
    internal static IEnumerable<string> EnumerateProperNounsInProse(string fountain)
    {
        var headingSet = new HashSet<string>(EnumerateSceneHeadingLines(fountain), StringComparer.OrdinalIgnoreCase);
        var cueSet = new HashSet<string>(EnumerateCharacterCueNames(fountain), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in (fountain ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (!IsProseNarrativeLine(raw, headingSet, cueSet))
                continue;
            foreach (var word in ProperNounsOnLine(raw.Trim(), seen))
                yield return word;
        }
    }

    private static bool IsProseNarrativeLine(
        string raw,
        HashSet<string> headingSet,
        HashSet<string> cueSet)
    {
        var line = raw.Trim();
        if (line.Length == 0) return false;
        if (headingSet.Contains(line) || cueSet.Contains(line)) return false;
        return !SceneHeadingLineRegex.IsMatch(line);
    }

    private static IEnumerable<string> ProperNounsOnLine(string line, HashSet<string> seen)
    {
        foreach (var word in ProperNounWordRegex.Matches(line).Select(m => m.Value))
        {
            if (ProseNameStopWords.Contains(word)) continue;
            if (seen.Add(word))
                yield return word;
        }
    }

    /// <summary>Two names are spelling-drift candidates when close but not identical.</summary>
    public static bool IsNameSpellingDriftCandidate(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Length < 4 || b.Length < 4) return false;
        if (Math.Abs(a.Length - b.Length) > 2) return false;
        return NameLevenshteinDistance(a, b) <= 2;
    }

    private static int NameLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = char.ToUpperInvariant(s[i - 1]) == char.ToUpperInvariant(t[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[s.Length, t.Length];
    }

    /// <summary>
    /// Groups character-cue names and prose proper nouns that are near-duplicates of each other
    /// (edit distance ≤2) — candidates the model might confirm are the same person spelled
    /// inconsistently ("Olsen"/"Olson") or reject as genuinely different people.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindNameDriftCandidateGroups(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<IReadOnlyList<string>>();

        var names = EnumerateCharacterCueNames(fountain)
            .Concat(EnumerateProperNounsInProse(fountain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ClusterSpellingDriftGroups(names);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ClusterSpellingDriftGroups(IReadOnlyList<string> names)
    {
        var groups = new List<IReadOnlyList<string>>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            if (used.Contains(names[i])) continue;
            var cluster = CollectDriftCluster(names, i, used);
            if (cluster is null) continue;
            foreach (var n in cluster) used.Add(n);
            groups.Add(cluster);
        }
        return groups;
    }

    private static List<string>? CollectDriftCluster(
        IReadOnlyList<string> names, int i, HashSet<string> used)
    {
        List<string>? cluster = null;
        for (var j = i + 1; j < names.Count; j++)
        {
            if (used.Contains(names[j])) continue;
            if (!IsNameSpellingDriftCandidate(names[i], names[j])) continue;
            cluster ??= [names[i]];
            cluster.Add(names[j]);
        }
        return cluster;
    }

    /// <summary>
    /// Chat pass: confirm which candidate name groups are the same person with drifted
    /// spelling and unify to one canonical spelling everywhere (cues and prose); leave
    /// genuinely-different people untouched. Fires only when the cheap pre-check finds a
    /// candidate — most books never trigger this call.
    /// </summary>
    private static async Task<string> RepairNameDriftAsync(
        string system,
        string fountain,
        ChatCall chat)
    {
        var onProgress = chat.OnProgress;
        var groups = FindNameDriftCandidateGroups(fountain);
        if (groups.Count == 0 || !chat.Chat.IsConfigured)
            return fountain;

        onProgress?.Invoke($"Checking {groups.Count} possible name-spelling drift group(s)…");

        var listed = string.Join("\n\n", groups.Select((g, i) =>
            $"  Group {i + 1}:\n" + string.Join("\n", g.Select(n => "    - " + n))));
        var user = $"""
            NAME NORMALIZATION (CONFIRM, THEN FIX)
            The Fountain draft below may spell the SAME person's name inconsistently across
            mentions (character cues and prose/dialogue alike) — e.g. a typo carried over from
            the source book. Each group below is a near-spelling-match and MIGHT be one person
            — or might genuinely be different people. Decide each group on its own.

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - For a group that IS the same person, unify every occurrence (cues, action lines,
              dialogue, parentheticals) to one canonical spelling — pick whichever spelling
              appears more often, or the more standard spelling if it's a tie.
            - For a group that is genuinely different people, leave every occurrence exactly as
              written.
            - Do not change plot, locations, or dialogue wording except the spelling itself.
            - No markdown fences. Fountain only.
            """;

        return await ExecuteNormalizationPassAsync(new(
            system, fountain, user, chat with { Temperature = 0.1 },
            ChatCallModes.BookToFountainNameNormalizeRetry,
            "Name normalization",
            "stage1-name-normalize-v1",
            "stage1_name_normalize",
            "Names checked.")).ConfigureAwait(false);
    }

    // ── split narration (continuous V.O./verse broken by a real blank line) ───────────────

    /// <summary>Longest line (trimmed) still treated as verse. Prose action sentences run much longer.</summary>
    private const int VerseMaxLineChars = 72;

    // Camera / transition / structural words that must NOT appear in a verse-shaped Action block —
    // these mark real staging, not orphaned narration, so their presence blocks a false merge.
    private static readonly Regex CameraOrTransitionLineRegex = new(@"\b(ANGLE|CLOSE|WIDE|WIDER|PAN|TILT|ZOOM|DOLLY|CRANE|TRACK(?:ING)?|POV|INSERT|"
        + @"MATCH\s+CUT|SMASH\s+CUT|CUT\s+TO|DISSOLVE|FADE|CAMERA|MONTAGE|INTERCUT|"
        + @"SUPER|TITLE\s+CARD|EST\.)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// A verse/V.O. narration block that a real blank line has split so its later stanzas parse as
    /// silent Action (the FountainParser correctly ends a dialogue block on a truly-empty line).
    /// </summary>
    public sealed record SplitNarrationSpan
    {
        /// <summary>Narration cue name, e.g. <c>NARRATOR</c>.</summary>
        public required string CueName { get; init; }
        /// <summary>Cue extension, e.g. <c>(V.O.)</c>.</summary>
        public required string CueExtension { get; init; }
        /// <summary>Verse lines that are (correctly) still dialogue under the cue.</summary>
        public required IReadOnlyList<string> DialogueLines { get; init; }
        /// <summary>Verse lines that were demoted to Action by the real blank line.</summary>
        public required IReadOnlyList<string> OrphanActionLines { get; init; }

        public string CueDisplay =>
            string.IsNullOrWhiteSpace(CueExtension) ? CueName : $"{CueName} {CueExtension}";
    }

    private readonly record struct FountainBlock(int Start, int End);

    /// <summary>
    /// Detect verse/V.O. narration split into silent Action by a real blank line. Flags a voice-over
    /// Dialogue block that is verse-shaped (≥2 short lines) immediately followed by a verse-shaped
    /// Action block (≥2 short lines; no scene-heading / transition / camera-directive lines).
    /// Requiring BOTH sides to be verse keeps ordinary prose Action after a V.O. cue from flagging.
    /// A correctly-written poem (all stanzas under one cue, two-space stanza breaks) is a single
    /// block, so its following block is not verse Action and it is never flagged.
    /// </summary>
    public static IReadOnlyList<SplitNarrationSpan> FindSplitNarrationBlocks(string? fountain)
    {
        var spans = new List<SplitNarrationSpan>();
        if (string.IsNullOrWhiteSpace(fountain))
            return spans;

        var lines = SplitPhysicalLines(fountain);
        var blocks = ScanFountainBlocks(lines);

        for (var b = 0; b < blocks.Count - 1; b++)
        {
            if (!TryReadVoVerseDialogue(lines, blocks[b], out var name, out var ext, out var dialogueLines))
                continue;

            var action = ContentLines(lines, blocks[b + 1]);
            if (!IsVerseShaped(action) || action.Any(IsStructuralOrCameraLine))
                continue;

            spans.Add(new SplitNarrationSpan
            {
                CueName = name,
                CueExtension = ext,
                DialogueLines = dialogueLines,
                OrphanActionLines = action,
            });
        }

        return spans;
    }

    /// <summary>
    /// Deterministic fallback used only when the model's correction still trips the detector:
    /// pull the orphaned verse Action stanzas back into the preceding V.O. Dialogue block, joined
    /// by the Fountain two-space line-break convention as the stanza break, so the whole passage is
    /// one continuous narration again. Conservative — absorbs only the consecutive verse Action
    /// blocks that immediately follow a flagged V.O. verse block; stops at the first non-verse block.
    /// Every other line is preserved byte-for-byte; only the offending blank separators change.
    /// </summary>
    public static string RemergeSplitNarration(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return fountain ?? "";

        var lines = SplitPhysicalLines(fountain);
        var blocks = ScanFountainBlocks(lines);
        if (blocks.Count < 2)
            return fountain;

        var mergeGaps = CollectNarrationMergeGaps(lines, blocks);
        if (mergeGaps.Count == 0)
            return fountain;

        return string.Join("\n", EmitMergedNarrationLines(lines, blocks, mergeGaps));
    }

    private static HashSet<int> CollectNarrationMergeGaps(string[] lines, List<FountainBlock> blocks)
    {
        var mergeGaps = new HashSet<int>();
        for (var b = 0; b < blocks.Count - 1; b++)
        {
            if (!TryReadVoVerseDialogue(lines, blocks[b], out _, out _, out _))
                continue;
            MarkFollowingVerseActionGaps(lines, blocks, b, mergeGaps);
        }
        return mergeGaps;
    }

    private static void MarkFollowingVerseActionGaps(
        string[] lines, List<FountainBlock> blocks, int startBlock, HashSet<int> mergeGaps)
    {
        var k = startBlock;
        while (k + 1 < blocks.Count)
        {
            var action = ContentLines(lines, blocks[k + 1]);
            if (!IsVerseShaped(action) || action.Any(IsStructuralOrCameraLine))
                break;
            mergeGaps.Add(k);
            k++;
        }
    }

    private static List<string> EmitMergedNarrationLines(
        string[] lines, List<FountainBlock> blocks, HashSet<int> mergeGaps)
    {
        var outLines = new List<string>();
        for (var k = 0; k < blocks[0].Start; k++)
            outLines.Add(lines[k]);

        for (var b = 0; b < blocks.Count; b++)
            AppendBlockAndGap(outLines, lines, blocks, b, mergeGaps);

        for (var k = blocks[^1].End + 1; k < lines.Length; k++)
            outLines.Add(lines[k]);

        return outLines;
    }

    private static void AppendBlockAndGap(
        List<string> outLines,
        string[] lines,
        List<FountainBlock> blocks,
        int b,
        HashSet<int> mergeGaps)
    {
        for (var k = blocks[b].Start; k <= blocks[b].End; k++)
            outLines.Add(lines[k]);

        if (b >= blocks.Count - 1)
            return;

        if (mergeGaps.Contains(b))
        {
            outLines.Add("  "); // single Fountain two-space line = stanza break inside dialogue
        }
        else
        {
            for (var k = blocks[b].End + 1; k < blocks[b + 1].Start; k++)
                outLines.Add(lines[k]);
        }
    }

    private static string[] SplitPhysicalLines(string fountain) =>
        fountain.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Group physical lines into content blocks separated by real blank lines. A whitespace-only
    /// line with two+ spaces is a Fountain line break (dialogue continuation), NOT a separator, so
    /// it stays inside its block — the same distinction the parser makes in ConsumeDialogueBlock.
    /// </summary>
    private static List<FountainBlock> ScanFountainBlocks(string[] lines)
    {
        var blocks = new List<FountainBlock>();
        var i = 0;
        while (i < lines.Length)
        {
            if (IsBlockSeparator(lines[i])) { i++; continue; }
            var start = i;
            while (i < lines.Length && !IsBlockSeparator(lines[i])) i++;
            blocks.Add(new FountainBlock(start, i - 1));
        }
        return blocks;
    }

    /// <summary>True for a real blank line (ends a dialogue block). A two-space line breaks nothing.</summary>
    private static bool IsBlockSeparator(string raw)
    {
        if (raw.Trim().Length != 0) return false;
        return !FountainLexer.IsTwoSpaceContinue(raw);
    }

    private static List<string> ContentLines(string[] lines, FountainBlock block)
    {
        var result = new List<string>();
        for (var k = block.Start; k <= block.End; k++)
        {
            var t = lines[k].Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// True when a block is a voice-over Dialogue block whose spoken lines are verse-shaped.
    /// Outputs the cue name, its extension, and the dialogue (verse) lines.
    /// </summary>
    private static bool TryReadVoVerseDialogue(
        string[] lines, FountainBlock block, out string name, out string ext,
        out IReadOnlyList<string> dialogueLines)
    {
        name = "";
        ext = "";
        dialogueLines = Array.Empty<string>();

        var content = ContentLines(lines, block);
        if (content.Count < 3) // cue + at least two verse lines
            return false;

        if (!TryParseCharacterCue(content[0], out name, out ext))
            return false;
        if (!FountainLexer.IsVoiceOverExtension(ext))
            return false;

        var spoken = content.Skip(1).ToList();
        if (!IsVerseShaped(spoken))
            return false;

        dialogueLines = spoken;
        return true;
    }

    /// <summary>≥2 short lines — the shape of verse stanzas, not flowing prose action.</summary>
    private static bool IsVerseShaped(IReadOnlyList<string> content)
    {
        if (content.Count < 2) return false;
        return content.All(l => l.Length is > 0 and <= VerseMaxLineChars);
    }

    /// <summary>Parse an ALL-CAPS character cue line into name + parenthetical extension.</summary>
    private static bool TryParseCharacterCue(string line, out string name, out string ext)
    {
        name = "";
        ext = "";
        var t = line.Trim();
        if (t.Length == 0 || t.Length > 60) return false;
        // Forced/structural leaders are never plain cues.
        if ("(!>~#=".IndexOf(t[0]) >= 0) return false;
        if (SceneHeadingLineRegex.IsMatch(t)) return false;

        var core = t;
        if (core.StartsWith('@')) core = core[1..].Trim();
        core = core.TrimEnd('^', ' ', '\t');

        var paren = core.IndexOf('(');
        var namePart = (paren > 0 ? core[..paren] : core).Trim();
        ext = paren > 0 ? core[paren..].Trim() : "";
        if (namePart.Length < 2) return false;

        var letters = namePart.Where(char.IsLetter).ToArray();
        if (letters.Length < 2 || letters.Any(char.IsLower)) return false; // must be ALL CAPS

        name = namePart;
        return true;
    }

    /// <summary>True when a line is a scene heading, transition, or camera directive (not verse).</summary>
    private static bool IsStructuralOrCameraLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith('.') && t.Length > 1 && char.IsLetterOrDigit(t[1])) return true; // forced heading
        if (SceneHeadingLineRegex.IsMatch(t)) return true;
        if (t.EndsWith("TO:", StringComparison.OrdinalIgnoreCase)) return true;
        if (CameraOrTransitionLineRegex.IsMatch(t)) return true;
        return false;
    }

    /// <summary>
    /// One automatic rewrite pass when continuous verse / V.O. narration was split by a real blank
    /// line into silent Action. Mirrors the vague-heading / generic-speaker repair lifecycle:
    /// detector → repair prompt → <see cref="ExecuteStage1OperationAsync"/> with a re-run validator
    /// and a deterministic re-merge fallback. No-op when nothing is flagged.
    /// </summary>
    private static async Task<string> RepairSplitNarrationAsync(
        string system,
        string fountain,
        ChatCall chat,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure = null)
    {
        var model = chat.Model;
        var onProgress = chat.OnProgress;
        var ct = chat.Ct;
        const string operationName = "stage1_narration_split_repair";
        const string promptVersion = "stage1-narration-split-repair-v1";

        var split = FindSplitNarrationBlocks(fountain);
        if (split.Count == 0 || !chat.Chat.IsConfigured)
            return fountain;

        onProgress?.Invoke(
            $"Repairing {split.Count} split narration block(s) (continuous verse must stay one V.O. cue)…");

        // Learning-loop sink — same route the multi-chunk structural gate uses (GenerationErrorLogger).
        if (onStructuralGateFailure is not null)
        {
            var summary = string.Join(
                " | ",
                split.Take(3).Select(s => $"{s.CueDisplay}: {FirstNonEmpty(s.OrphanActionLines)}"));
            try
            {
                await onStructuralGateFailure(new StructuralGateFailure
                {
                    Stage = operationName,
                    Model = model,
                    ErrorType = "structural_gate_failure",
                    ErrorMessage =
                        $"Split narration ({promptVersion}): {split.Count} voice-over verse block(s) " +
                        $"broken by a real blank line into silent action. {summary}",
                    ResponseSummary = fountain.Length > 500 ? fountain[..500] : fountain,
                }, ct).ConfigureAwait(false);
            }
            catch { /* logging must never break the repair it observes */ }
        }

        var listed = string.Join(
            "\n",
            split.Select(s => $"  - {s.CueDisplay} — orphaned verse begins: \"{FirstNonEmpty(s.OrphanActionLines)}\""));
        var user = $"""
            NARRATION CONTINUITY REPAIR (HARD)
            In the Fountain draft below, continuous verse / voice-over narration was broken by a
            real blank line, so every stanza after the first parses as silent Action instead of
            spoken narration. Affected cue(s):

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - Keep each continuous verse / V.O. passage as ONE dialogue block under ONE cue.
            - Separate stanzas with a Fountain two-space line break (a line holding exactly two
              spaces), NEVER a real blank line — a real blank line ends the narration and drops
              the rest to silent action.
            - Attribute any bare standalone narration or verse with no cue (a closing moral, an
              epigraph, a floating stanza) to NARRATOR (V.O.) so it is actually spoken.
            - Do not change plot, cast tokens, locations, or book-faithful wording — only re-join
              the split narration and attribute bare narration.
            - No markdown fences. Fountain only.
            """;

        try
        {
            using var heartbeat = StartProgressHeartbeat(
                onProgress,
                "Still repairing split narration…",
                TimeSpan.FromSeconds(20));
            var raw = await ExecuteStage1OperationAsync(
                    chat with { Temperature = 0.15 }, system, user,
                    ChatCallModes.BookToFountainNarrationRetry,
                    "Narration split repair",
promptVersion: promptVersion,
                    correctionInstruction:
                        "Keep each continuous verse / V.O. passage as one dialogue block; separate "
                        + "stanzas with a two-space line, never a real blank line.",
                    validate: value => ValidateFountainRepair(
                        value,
                        f => FindSplitNarrationBlocks(f).Select(s => s.CueDisplay).ToList(),
                        "split_narration"),
                    deterministicFallback: RemergeSplitNarration(fountain),
                    operationName: operationName,
                    fountainForFile: fountain)
                .ConfigureAwait(false);
            if (raw is null)
            {
                onProgress?.Invoke("Narration split repair failed twice — keeping prior draft.");
                return fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                onProgress?.Invoke("Narration split repair unusable — keeping prior draft.");
                return fountain;
            }

            var remaining = FindSplitNarrationBlocks(repaired);
            if (remaining.Count < split.Count)
            {
                onProgress?.Invoke(
                    remaining.Count == 0
                        ? "Split narration merged into continuous voice-over."
                        : $"Narration split repair partial — {remaining.Count} block(s) left.");
                return repaired;
            }

            onProgress?.Invoke("Narration split repair did not clear splits — keeping prior draft.");
            return fountain;
        }
        catch (Exception)
        {
            onProgress?.Invoke("Narration split repair failed — keeping prior draft.");
            return fountain;
        }
    }

    private static string FirstNonEmpty(IReadOnlyList<string> lines) =>
        lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

    /// <summary>
    /// Deterministic: when two scene headings name the same place with drifted wording
    /// (e.g. "OLD HOUSE - HALL OUTSIDE CHAMBER" vs "HALL OUTSIDE CHAMBER"), rewrite
    /// all visits to one canonical location phrase so location_seed_tokens stay unified.
    /// Public for tests and SaveDraft.
    /// </summary>
    public static string NormalizeSceneHeadingWording(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return fountain ?? "";

        fountain = fountain.Replace("\r\n", "\n").Replace('\r', '\n');
        var headings = EnumerateSceneHeadingLines(fountain)
            .Where(h => h.Length > 0)
            .ToList();
        if (headings.Count < 2)
            return fountain;

        // Unique heading forms
        var forms = headings
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var locByHeading = BuildLocByHeading(forms);
        var canonicalLoc = BuildCanonicalLocMap(locByHeading.Values);

        // Only rewrite if at least one alias collapsed
        if (!canonicalLoc.Any(kv =>
                !kv.Key.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)))
            return fountain;

        var map = BuildHeadingRewriteMap(forms, locByHeading, canonicalLoc);
        return ApplyHeadingRewrites(fountain, map);
    }

    private static Dictionary<string, string> BuildLocByHeading(IReadOnlyList<string> forms)
    {
        var locByHeading = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in forms)
        {
            var (_, loc, _) = SplitSceneHeadingParts(h);
            locByHeading[h] = loc;
        }
        return locByHeading;
    }

    private static Dictionary<string, string> BuildCanonicalLocMap(IEnumerable<string> locValues)
    {
        var locNames = locValues
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Alias longer "PREFIX - CORE" names to shorter CORE when CORE is also used
        var canonicalLoc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locNames)
            canonicalLoc[loc] = loc;

        foreach (var longer in locNames.OrderByDescending(l => l.Length))
        {
            foreach (var shorter in locNames
                         .Where(s => s.Length < longer.Length)
                         .OrderByDescending(s => s.Length))
            {
                if (!IsLocationNameAlias(longer, shorter)) continue;
                // Prefer shorter core as canonical (stable key, less drift)
                var root = canonicalLoc.TryGetValue(shorter, out var c) ? c : shorter;
                canonicalLoc[longer] = root;
                break;
            }
        }
        return canonicalLoc;
    }

    private static Dictionary<string, string> BuildHeadingRewriteMap(
        IReadOnlyList<string> forms,
        Dictionary<string, string> locByHeading,
        Dictionary<string, string> canonicalLoc)
    {
        // Preferred loc phrase = canonical; rebuild each heading with original time-of-day
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in forms)
        {
            var loc = locByHeading[h];
            var canon = canonicalLoc.TryGetValue(loc, out var c) ? c : loc;
            if (loc.Equals(canon, StringComparison.OrdinalIgnoreCase))
            {
                map[h] = h;
                continue;
            }

            var (prefix, _, time) = SplitSceneHeadingParts(h);
            map[h] = string.IsNullOrEmpty(time)
                ? $"{prefix}{canon}"
                : $"{prefix}{canon} - {time}";
        }
        return map;
    }

    private static string ApplyHeadingRewrites(string fountain, Dictionary<string, string> map)
    {
        // Prefer most frequent original form's casing for the same rebuilt target? use as-is
        var lines = fountain.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd('\r');
            var key = t.Trim();
            if (map.TryGetValue(key, out var repl) &&
                !key.Equals(repl, StringComparison.Ordinal))
            {
                // preserve leading whitespace if any
                var lead = t.Length - t.TrimStart().Length;
                lines[i] = (lead > 0 ? t[..lead] : "") + repl;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// True when <paramref name="longer"/> is the same place as <paramref name="shorter"/>
    /// with a redundant building/site prefix (e.g. "OLD HOUSE - HALL…" vs "HALL…").
    /// </summary>
    public static bool IsLocationNameAlias(string longer, string shorter)
    {
        longer = (longer ?? "").Trim();
        shorter = (shorter ?? "").Trim();
        if (longer.Length <= shorter.Length || shorter.Length < 4)
            return false;
        if (!longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = longer[..^shorter.Length];
        return prefix.EndsWith(" - ", StringComparison.Ordinal)
               || prefix.EndsWith(" – ", StringComparison.Ordinal);
    }

    private static (string Prefix, string LocName, string Time) SplitSceneHeadingParts(string heading)
    {
        heading = (heading ?? "").Trim();
        var m = ScenePrefixRegex.Match(heading);
        var prefix = m.Success ? m.Value : "INT. ";
        if (!prefix.EndsWith(' ') && prefix.Length > 0)
            prefix += " ";
        var rest = m.Success ? heading[m.Length..].Trim() : heading;
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) dash = rest.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dash > 0)
            return (prefix, rest[..dash].Trim(), rest[(dash + 3)..].Trim());
        return (prefix, rest, "");
    }

    /// <summary>
    /// Overwrite any existing Draft date: line with today's local date (M/d/yyyy).
    /// Call after <see cref="EnsureDraftDate"/> so a missing key is inserted first.
    /// </summary>
    public static string FixDraftDate(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";
        var today = DateTime.Now.ToString("M/d/yyyy");
        return DraftDateRegex.Replace(fountain, $"$1 {today}");
    }

    /// <summary>
    /// Resolve single-shot / chunk budgets for a chat model id, using the model's real context
    /// window from <see cref="SupportedModelCatalog"/> when known. Unlisted/unknown ids (a model
    /// not yet added to the catalog, or a typo) fall back to <see cref="DefaultSingleShotBookMaxChars"/>
    /// — a conservative guess for a model we have no real data on — rather than assuming a large
    /// window they may not have. A model WITH a verified catalog window is trusted up to
    /// <see cref="AbsoluteSingleShotCeiling"/> instead of being clamped down to that same
    /// conservative default: the whole point of tracking real per-model windows is that a
    /// 1M-token model should get to single-shot books the 120k-char default would have forced
    /// into needless multi-chunk fallback (which the class doc calls out as a quality compromise).
    /// </summary>
    public static PromptBudget ResolvePromptBudget(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException(
                "Screenplay generation: model is required. Open Settings and choose a Script & planning model.");
        var id = modelId.Trim();
        // ~3.2 chars/token heuristic; leave headroom for system + user scaffolding.
        var catalogInputTokens = SupportedModelCatalog.Find(id, ModelCapability.Chat)?.MaxInputTokens;
        var inputTokens = catalogInputTokens ?? 128_000;

        var reserved = DefaultReservedOverheadChars;
        var tokenDerivedBookMax = Math.Clamp(
            (int)(inputTokens * 3.2) - reserved,
            8_000,
            AbsoluteSingleShotCeiling);
        // Known model → trust its real window (up to the absolute ceiling). Unknown model → stay
        // under the conservative product default, since tokenDerivedBookMax there is built from
        // a guess, not a verified number.
        var bookMax = catalogInputTokens.HasValue
            ? tokenDerivedBookMax
            : Math.Min(DefaultSingleShotBookMaxChars, tokenDerivedBookMax);

        var chunkSoft = Math.Clamp(
            Math.Min(DefaultChunkSoftMaxChars, Math.Max(4_000, bookMax / 2)),
            4_000,
            Math.Min(bookMax, 120_000));

        return new PromptBudget
        {
            ModelId = id,
            SingleShotBookMaxChars = bookMax,
            ChunkSoftMaxChars = chunkSoft,
            MaxChunks = MaxAdaptChunks,
            ReservedOverheadChars = reserved,
        };
    }

    /// <summary>
    /// Chunk count actually needed for this book at the budget's soft-max chunk size,
    /// floored at <paramref name="budget"/>.MaxChunks (usually <see cref="MaxAdaptChunks"/>)
    /// and capped at <see cref="AbsoluteMaxAdaptChunks"/> for cost/latency safety.
    /// Without this, ChunkBookForAdaptation silently packs everything past the flat chunk
    /// cap into the LAST chunk — e.g. an 838K-char book at a 40K soft-max and an 8-chunk
    /// cap produced 7 normal ~40K chunks and one ~660K-char final chunk, which measurably
    /// lost adaptation density (much lower response/prompt ratio) versus the earlier ones.
    /// </summary>
    public static int ResolveMaxChunks(string? bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var normalized = NormalizeBookText(bookText ?? "");
        if (normalized.Length == 0)
            return budget.MaxChunks;

        var softMax = Math.Max(1, budget.ChunkSoftMaxChars);
        // Unit packing rarely fills soft-max exactly; size as if each pack holds ~85%.
        var effectiveCapacity = Math.Max(1, (int)(softMax * 0.85));
        var needed = (int)Math.Ceiling(normalized.Length / (double)effectiveCapacity);
        return Math.Clamp(Math.Max(budget.MaxChunks, needed), budget.MaxChunks, AbsoluteMaxAdaptChunks);
    }

    /// <summary>True when the full book fits one adapt call under <paramref name="budget"/>.</summary>
    public static bool FitsSingleShot(string bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        bookText ??= "";
        return bookText.Length <= budget.SingleShotBookMaxChars;
    }

    /// <summary>
    /// Whether multi-chunk is worth attempting (book large enough and ≥2 chunks possible).
    /// </summary>
    public static bool ShouldChunkFallback(string bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        bookText = NormalizeBookText(bookText ?? "");
        if (bookText.Length < MinBookCharsForChunkFallback)
            return false;

        var soft = Math.Min(budget.ChunkSoftMaxChars, Math.Max(MinBookCharsForChunkFallback, bookText.Length / 2));
        var chunks = ChunkBookForAdaptation(bookText, budget.MaxChunks, soft);
        return chunks.Count >= 2;
    }

    /// <summary>
    /// Structural + coverage gate. Soft failures fail single-shot (trigger chunk fallback);
    /// multi-chunk only hard-fails on structure / excerpt markers.
    /// </summary>
    public static QualityResult EvaluateQuality(
        string? fountain,
        string bookText,
        int? totalRuntimeMinutes,
        AdaptPath path)
    {
        fountain = StripBookPageTags(fountain ?? "");
        bookText = NormalizeBookText(bookText ?? "");
        var minutes = totalRuntimeMinutes is > 0 ? Math.Clamp(totalRuntimeMinutes.Value, 1, 180) : 0;
        var hasTarget = minutes > 0;

        var fails = CollectQualityFailures(fountain, bookText, hasTarget, minutes, path);
        var hard = fails.Contains("structure") || fails.Contains("excerpt_marker");
        var ok = path is AdaptPath.Multi or AdaptPath.Indexed
            ? !hard && LooksLikeGoodFountain(fountain)
            : fails.Count == 0;

        return new QualityResult
        {
            Ok = ok,
            Reason = fails.Count == 0 ? "ok" : string.Join(",", fails),
            SceneCount = CountSceneHeadings(fountain),
            FountainChars = fountain.Length,
            Failures = fails,
            HasHardFailure = hard,
        };
    }

    private static List<string> CollectQualityFailures(
        string fountain, string bookText, bool hasTarget, int minutes, AdaptPath path)
    {
        var fails = new List<string>();
        if (!LooksLikeGoodFountain(fountain))
            fails.Add("structure");
        if (TruncatMarkerRegex.IsMatch(fountain))
            fails.Add("excerpt_marker");
        AddMissingEndingIfNeeded(fails, fountain, bookText, path);
        AddSceneCountFailureIfNeeded(fails, fountain, bookText, hasTarget, minutes);
        AddSuspiciouslyShortIfNeeded(fails, fountain, bookText, path);
        AddRuntimeShortIfNeeded(fails, fountain, bookText);
        return fails;
    }

    private static void AddMissingEndingIfNeeded(
        List<string> fails, string fountain, string bookText, AdaptPath path)
    {
        // Soft: long books should resolve
        if (bookText.Length > 40_000 &&
            path == AdaptPath.Single &&
            fountain.Length >= 80 &&
            !FadeOutEndingRegex.IsMatch(fountain))
            fails.Add("missing_ending");
    }

    private static void AddSceneCountFailureIfNeeded(
        List<string> fails, string fountain, string bookText, bool hasTarget, int minutes)
    {
        var scenes = CountSceneHeadings(fountain);
        var minScenes = ResolveMinSceneFloor(bookText.Length, hasTarget, minutes);
        if (scenes < minScenes && bookText.Length >= MinBookCharsForChunkFallback)
            fails.Add($"scene_count:{scenes}<{minScenes}");
    }

    private static int ResolveMinSceneFloor(int bookLength, bool hasTarget, int minutes)
    {
        // Scene floor: only enforce a runtime-derived band when an artificial target is set.
        // Unlimited (default) uses book-length soft floors only — never pad short stories.
        int minScenes;
        if (hasTarget)
        {
            minScenes = Math.Clamp(minutes / 2, 3, 40);
            if (bookLength > 50_000)
                minScenes = Math.Max(minScenes, 8);
        }
        else
        {
            minScenes = bookLength > 50_000 ? 8 : (bookLength > 20_000 ? 3 : 1);
        }
        if (bookLength < 8_000)
            minScenes = Math.Min(minScenes, 2);
        return minScenes;
    }

    private static void AddSuspiciouslyShortIfNeeded(
        List<string> fails, string fountain, string bookText, AdaptPath path)
    {
        if (path == AdaptPath.Single &&
            bookText.Length > 60_000 &&
            fountain.Length < Math.Min(8_000, Math.Max(500, bookText.Length / 40)))
            fails.Add("suspiciously_short");
    }

    private static void AddRuntimeShortIfNeeded(List<string> fails, string fountain, string bookText)
    {
        // Soft: draft runtime estimate << book natural length (max-then-trim expects a long base).
        // Fails single-shot so multi-chunk fallback can try; multi path still accepts structure-ok drafts.
        var naturalMin = NaturalRuntime.EstimateNaturalMinutes(bookText);
        if (naturalMin < 45)
            return;
        var draftMin = EstimateDraftRuntimeMinutes(fountain);
        // Floor: at least 40% of natural, and not under 25 min when natural is feature-scale.
        var floor = Math.Max(25, (int)Math.Round(naturalMin * 0.40));
        if (draftMin > 0 && draftMin < floor)
            fails.Add($"runtime_short:{draftMin:0}<{floor}(natural~{naturalMin})");
    }

    /// <summary>
    /// Finished-film minutes from draft body words. Trailer <c>est_runtime_min</c> is used only
    /// when it agrees with the word count within 2× — last-chunk sidecars often lie (e.g. 17 min
    /// on a 400-minute draft).
    /// </summary>
    public static double EstimateDraftRuntimeMinutes(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain)) return 0;
        TryReadReportedRuntime(fountain, out var reported, out var body);
        var fromWords = EstimateMinutesFromBodyWords(body);
        if (reported > 0 && fromWords > 0 && IsRuntimeSidecarSuspect(reported, fromWords))
            return fromWords;
        if (reported > 0)
            return reported;
        return fromWords;
    }

    /// <summary>True when the model's runtime trailer disagrees with body-word minutes by more than 2×.</summary>
    public static bool IsRuntimeSidecarSuspect(double reportedMinutes, double bodyWordMinutes) =>
        reportedMinutes > 0
        && bodyWordMinutes > 0
        && (reportedMinutes / bodyWordMinutes > 2.0 || bodyWordMinutes / reportedMinutes > 2.0);

    internal static void ReconcileReportRuntime(
        AdaptationReport? report, string fountain, Action<string>? onProgress)
    {
        if (report?.Metrics is null) return;
        TryReadReportedRuntime(fountain, out _, out var body);
        var fromWords = EstimateMinutesFromBodyWords(string.IsNullOrWhiteSpace(body) ? fountain : body);
        if (fromWords <= 0) return;
        var reported = report.Metrics.EstRuntimeMin;
        if (reported <= 0 || IsRuntimeSidecarSuspect(reported, fromWords))
        {
            if (reported > 0)
                onProgress?.Invoke(
                    $"Runtime sidecar looked wrong ({reported:0.#} vs {fromWords:0.#} min from pages) — using page count.");
            report.Metrics.EstRuntimeMin = Math.Round(fromWords, 1);
        }
    }

    private static double EstimateMinutesFromBodyWords(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain)) return 0;
        var words = CountDraftBodyWords(fountain);
        if (words <= 0) return 0;
        return words / 155.0;
    }

    private static void TryReadReportedRuntime(string fountain, out double minutes, out string body)
    {
        minutes = 0;
        body = fountain;
        try
        {
            var split = SplitAdaptationTrailers(fountain);
            if (split.Report?.Metrics?.EstRuntimeMin is > 0)
            {
                minutes = split.Report.Metrics.EstRuntimeMin;
                return;
            }
            body = split.Fountain;
        }
        catch
        {
            /* trailer parse optional */
        }
    }

    private static int CountDraftBodyWords(string fountain)
    {
        var body = new StringBuilder(fountain.Length);
        foreach (var raw in fountain.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.TrimEnd().Trim();
            if (IsNonBodyFountainLine(t)) continue;
            body.Append(t).Append(' ');
        }
        return TextMetrics.CountWords(body.ToString());
    }

    private static bool IsNonBodyFountainLine(string t)
    {
        if (t.Length == 0) return true;
        if (IsTitlePageKeyLine(t)) return true;
        if (CommonRegex.IsMatch(t, @"^(INT\.|EXT\.|INT\./EXT\.|EST\.)", RegexOptions.IgnoreCase))
            return true;
        if (CommonRegex.IsMatch(t, @"^(FADE (IN|OUT)|CUT TO|DISSOLVE TO|SMASH CUT|THE END)\b", RegexOptions.IgnoreCase))
            return true;
        if (t.StartsWith('(') && t.EndsWith(')'))
            return true; // parentheticals / (SOUND:…)
        return IsShortAllCapsCueLine(t);
    }

    private static bool IsTitlePageKeyLine(string t) =>
        t.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Credit:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Author:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Draft date:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("Notes:", StringComparison.OrdinalIgnoreCase);

    private static bool IsShortAllCapsCueLine(string t) =>
        t.Length <= 40
        && CommonRegex.IsMatch(t, @"^[A-Z0-9][A-Z0-9 \.'\-]{0,38}(\s*\([^)]+\))?$")
        && !t.Contains('.') // avoid "Mr. Smith" edge — still ok as dialogue body later
        && t == t.ToUpperInvariant();

    /// <summary>
    /// Remove operator-facing book page tags from Fountain
    /// (<c>= page N</c>, <c>[[page N]]</c>). Book linkage uses text/order match in the UI.
    /// </summary>
    public static string StripBookPageTags(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";

        // Whole-line synopsis tags: = page 2  /  = pages 2-4
        fountain = CommonRegex.Replace(
            fountain,
            @"(?im)^[ \t]*=\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\r?\n?",
            "");

        // Standalone note lines: [[page 2]] or [[pages 2-3]]
        fountain = CommonRegex.Replace(
            fountain,
            @"(?im)^[ \t]*\[\[\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\]\]\s*\r?\n?",
            "");

        // Inline notes left in a line of other text
        fountain = CommonRegex.Replace(
            fountain,
            @"\[\[\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\]\]",
            "",
            RegexOptions.IgnoreCase);

        // Collapse excess blank lines left behind
        fountain = CommonRegex.Replace(fountain, @"\n{3,}", "\n\n");
        return fountain.TrimEnd() + (fountain.EndsWith('\n') || fountain.Length == 0 ? "" : "\n");
    }

    /// <summary>
    /// Remove standalone Fountain page-break markers (a line of three or more `=`,
    /// optionally with a page number, e.g. <c>===</c> or <c>===13===</c>). Nothing in
    /// the prompt asks for these; the model still emits one after the title page on
    /// roughly a third of runs. Valid Fountain syntax, but an unrequested artifact here —
    /// stripped rather than banned-only-by-prompt, same reasoning as StripBookPageTags.
    /// </summary>
    public static string StripFountainPageBreaks(string? fountain) =>
        FountainLexer.StripFountainPageBreaks(fountain);

    /// <summary>Load <c>prompts/book_to_fountain.txt</c>.</summary>
    /// <param name="totalRuntimeMinutes">
    /// Artificial target minutes, or null/≤0 for <see cref="AdaptationPromptPack.UnlimitedRuntimeDirective"/> (default).
    /// </param>
    public static Task<string> BuildSystemPromptAsync(
        int? totalRuntimeMinutes = null,
        CancellationToken ct = default,
        AdaptationPromptTokens? tokens = null) =>
        AdaptationPromptPack.LoadBookToFountainSystemPromptAsync(
            totalRuntimeMinutes,
            fallbackBody: FountainOutputOverride,
            ct: ct,
            tokens: tokens ?? AdaptationPromptTokens.Default(totalRuntimeMinutes));

    /// <summary>
    /// Split book into ordered chunks for multi-pass adaptation (public for tests).
    /// </summary>
    public static IReadOnlyList<string> ChunkBookForAdaptation(
        string bookText,
        int maxChunks = MaxAdaptChunks,
        int softMaxChars = DefaultFallbackChunkSoftMaxChars)
    {
        bookText = NormalizeBookText(bookText);
        maxChunks = Math.Clamp(maxChunks, 1, AbsoluteMaxAdaptChunks);
        softMaxChars = Math.Clamp(softMaxChars, 4_000, 120_000);

        if (bookText.Length <= softMaxChars)
            return new[] { bookText };

        var units = SplitIntoUnits(bookText);
        if (units.Count == 0)
            return new[] { bookText };

        // Pack units into ≤ maxChunks buckets without exceeding softMax when possible
        var targetChunks = Math.Min(
            maxChunks,
            Math.Max(2, (int)Math.Ceiling(bookText.Length / (double)softMaxChars)));

        var chunks = new List<string>();
        var current = new StringBuilder();
        var idealSize = Math.Max(softMaxChars / 2, bookText.Length / targetChunks);

        foreach (var unit in units)
            PackUnitIntoChunks(unit, chunks, current, maxChunks, targetChunks, softMaxChars, idealSize);

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        MergeOverflowChunks(chunks, maxChunks);
        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static bool ShouldFlushChunk(
        StringBuilder current,
        int chunkCount,
        int unitLength,
        int maxChunks,
        int targetChunks,
        int softMaxChars,
        int idealSize) =>
        current.Length > 0 &&
        chunkCount < maxChunks - 1 &&
        (current.Length + unitLength > softMaxChars ||
         (current.Length >= idealSize && chunkCount < targetChunks - 1));

    private static void PackUnitIntoChunks(
        string unit,
        List<string> chunks,
        StringBuilder current,
        int maxChunks,
        int targetChunks,
        int softMaxChars,
        int idealSize)
    {
        if (ShouldFlushChunk(current, chunks.Count, unit.Length, maxChunks, targetChunks, softMaxChars, idealSize))
        {
            chunks.Add(current.ToString().Trim());
            current.Clear();
        }

        if (unit.Length > softMaxChars)
        {
            PackLongUnit(unit, chunks, current, maxChunks, softMaxChars);
            return;
        }

        if (current.Length > 0) current.Append("\n\n");
        current.Append(unit);
    }

    private static void PackLongUnit(
        string unit,
        List<string> chunks,
        StringBuilder current,
        int maxChunks,
        int softMaxChars)
    {
        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
            current.Clear();
        }
        foreach (var slice in SliceLongUnit(unit, softMaxChars))
        {
            if (chunks.Count >= maxChunks - 1)
                current.AppendLine(slice);
            else
                chunks.Add(slice);
        }
    }

    private static void MergeOverflowChunks(List<string> chunks, int maxChunks)
    {
        while (chunks.Count > maxChunks)
        {
            var last = chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] + "\n\n" + last;
        }
    }

    /// <summary>Stitch partial Fountain scripts (title page from first only). Public for tests.</summary>
    public static string StitchFountainParts(IReadOnlyList<string>? parts)
    {
        if (parts is null || parts.Count == 0)
            return "";

        if (parts.Count == 1) return NormalizeFountainText(parts[0]);

        var merged = ConcatFountainParts(parts);
        if (!CommonRegex.IsMatch(merged, @"(?im)^(FADE OUT\.|THE END)\s*$"))
            merged += "\n\nFADE OUT.\n\nTHE END\n";
        return NormalizeFountainText(merged);
    }

    private static string ConcatFountainParts(IReadOnlyList<string> parts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
            AppendFountainPart(sb, parts[i], i, parts.Count);
        return sb.ToString().Trim();
    }

    private static void AppendFountainPart(StringBuilder sb, string? raw, int i, int count)
    {
        var part = StripFences(raw ?? "");
        if (string.IsNullOrWhiteSpace(part)) return;

        if (i == 0)
        {
            sb.Append(StripTrailingEndMarkers(part).TrimEnd());
            return;
        }

        part = PrepareContinuationPart(part, i, count);
        if (string.IsNullOrWhiteSpace(part)) return;
        sb.Append("\n\n");
        sb.Append(part.Trim());
    }

    private static string PrepareContinuationPart(string part, int i, int count)
    {
        part = StripTitlePage(part);
        part = StripTrailingEndMarkers(part);
        if (i < count - 1)
            part = StripTrailingEndMarkers(part);
        return part;
    }

    /// <summary>
    /// Minimal offline stub (tests / emergency). Production always uses chat.
    /// </summary>
    public static string ConvertHeuristic(string title, string bookText, string? author = null)
    {
        var pages = ParseBookPagesForHeuristic(bookText);
        var sb = new StringBuilder();
        sb.Append("Title: ").Append(string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(author))
        {
            sb.Append("Credit: Written by\nAuthor: ").Append(author.Trim()).Append('\n');
        }
        sb.Append("Source: Adapted from book\n");
        sb.Append("Draft date: ").Append(DateTime.Now.ToString("M/d/yyyy")).Append("\n\n");

        if (pages.Count == 0)
        {
            sb.Append("INT. ROOM - DAY\n\nNARRATOR\n[[No book text.]]\n");
            return NormalizeFountainText(sb.ToString());
        }

        foreach (var page in pages)
        {
            var body = (page.Text ?? "").Trim();
            if (body.Length < 12) continue;
            if (CommonRegex.IsMatch(body, @"^\(illustration", RegexOptions.IgnoreCase)) continue;

            sb.Append("INT. ROOM - DAY\n\n");
            sb.Append("NARRATOR\n");
            var line = CommonRegex.Replace(body, @"\s+", " ").Trim();
            if (line.Length > 400) line = line[..400] + "…";
            sb.Append(line).Append("\n\n");
        }

        return NormalizeFountainText(StripBookPageTags(sb.ToString()));
    }

    /// <summary>
    /// Structural check for usable Fountain. Page tags are never required (stripped for operators).
    /// </summary>
    public static bool LooksLikeGoodFountain(string text, bool requirePageTags = false)
    {
        _ = requirePageTags; // unused; page tags are not part of the product gate
        text = StripBookPageTags(text ?? "");
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80) return false;

        var hasScene = CommonRegex.IsMatch(text, @"(?im)^(INT|EXT|EST|I/E)[\./ ]");
        var dumpCount = CommonRegex.Matches(text, @"(?im)^INT\.\s+STORY\s+-\s+PAGE\s+\d+").Count;
        if (dumpCount >= 2) return false;

        // Prefer real locations; INT. SCENE is ok if there is dialogue/narration body
        var realLoc = CommonRegex.IsMatch(text, @"(?im)^(INT|EXT)\.\s+(?!SCENE\b)[A-Z0-9]");
        var hasNarratorOrDialogue =
            CommonRegex.IsMatch(text, @"(?im)^NARRATOR\s*$") ||
            CommonRegex.IsMatch(text, @"(?m)^[A-Z][A-Z0-9 &'.\-]{1,40}\s*$");
        var hasActionBody = CommonRegex.IsMatch(text, @"(?m)^[a-zA-Z].{20,}");

        if (!hasScene) return false;
        return realLoc || hasNarratorOrDialogue || hasActionBody;
    }

    // ── single / multi paths ─────────────────────────────────────────────

    /// <summary>
    /// Single-shot with structure retry + quality gate + optional coverage retry.
    /// Returns null when the draft is not acceptable and multi-chunk fallback should be considered.
    /// </summary>
    private static async Task<string?> TrySingleShotWithGateAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int? totalMinutes,
        string bookText,
        ChatCall chat,
        PromptBudget budget)
    {
        var onProgress = chat.OnProgress;
        var ct = chat.Ct;
        // Bound the primary call so a hung provider cannot sit forever. File_id novels
        // (Odyssey-scale) routinely need 15–20+ min on Grok 4.6; 8 and 15 both cancelled
        // a still-writing single pass. Keep this under the Responses HttpClient (30 min).
        const int singleShotSoftMinutes = 25;
        try
        {
            using var softCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            softCts.CancelAfter(TimeSpan.FromMinutes(singleShotSoftMinutes));

            // Heartbeat so operators see elapsed time while the model is still thinking.
            var started = DateTimeOffset.UtcNow;
            using var heartbeat = new Timer(_ =>
            {
                try
                {
                    var mins = (int)(DateTimeOffset.UtcNow - started).TotalMinutes;
                    var secs = (int)(DateTimeOffset.UtcNow - started).TotalSeconds % 60;
                    onProgress?.Invoke(
                        $"Still writing screenplay… ({mins}m {secs:D2}s — single pass can take several minutes)");
                }
                catch { /* ignore */ }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

            var draft = await ConvertSingleShotAsync(
                system, title, author, pageCount, totalMinutes, bookText,
                chat with { Progress = new ProgressCall(softCts.Token, chat.OnProgress) },
                bookMaxChars: budget.SingleShotBookMaxChars).ConfigureAwait(false);

            return EvaluateQuality(draft, bookText, totalMinutes, AdaptPath.Single).Ok ? draft : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            onProgress?.Invoke(
                $"Single-pass timed out after {singleShotSoftMinutes} minutes — will try multi-chunk…");
            return null;
        }
        catch (InvalidOperationException)
        {
            // Structure failed after retries, or transport/API wrapped as InvalidOperationException
            return null;
        }
    }

    private static async Task<string> ConvertSingleShotAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int? totalMinutes,
        string bookText,
        ChatCall chat,
        int bookMaxChars = DefaultSingleShotBookMaxChars,
        string? extraUserSuffix = null)
    {
        // Happy path: full book. Trim only if somehow over the call budget (prefer multi-chunk instead).
        var bookForPrompt = bookText.Length <= bookMaxChars
            ? bookText
            : TrimBookForPrompt(bookText, bookMaxChars);
        var user = BuildUserPrompt(title, author, pageCount, totalMinutes, bookForPrompt, chunkIndex: 0, chunkTotal: 1);
        if (!string.IsNullOrEmpty(extraUserSuffix))
            user += extraUserSuffix;

        var firstMode = string.IsNullOrEmpty(extraUserSuffix)
            ? ChatCallModes.BookToFountain
            : ChatCallModes.BookToFountainCoverage;
        var text = await ExecuteStage1OperationAsync(
                chat with { Progress = new ProgressCall(chat.Ct) }, system, user,
                firstMode,
                "Book adapt",
promptVersion: "stage1-book-to-fountain-v2",
                correctionInstruction: CoverageRetrySuffix,
                validate: value => ValidatePrimaryPackage(
                    value, bookText, totalMinutes, AdaptPath.Single))
            .ConfigureAwait(false);
        if (text is null)
            throw new InvalidOperationException(
                "Book adapt timed out or failed after retry. Try again or import a .fountain file.");

        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(UnusableScreenplayError);

        return text;
    }

    private static async Task<string> ConvertMultiChunkAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int? totalMinutes,
        string bookText,
        ChatCall chat,
        int softMaxChars = DefaultFallbackChunkSoftMaxChars,
        int maxChunks = MaxAdaptChunks)
    {
        var onProgress = chat.OnProgress;
        var ct = chat.Ct;
        var chunks = ChunkBookForAdaptation(bookText, maxChunks, softMaxChars);
        onProgress?.Invoke($"Book split into {chunks.Count} chunk(s) for adaptation…");

        var parts = new List<string>();
        string? continuity = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Adapting chunk {i + 1}/{chunks.Count}…");

            var user = BuildUserPrompt(
                title, author, pageCount, totalMinutes, chunks[i],
                chunkIndex: i, chunkTotal: chunks.Count, continuity: continuity);

            // One transport retry on timeout/cancel (chunk calls can exceed short proxies)
            var part = await ExecuteStage1OperationAsync(
                    chat, system, user,
                    ChatCallModes.BookToFountainChunk,
                    $"Chunk {i + 1}/{chunks.Count}",
promptVersion: "stage1-book-chunk-v2",
                    correctionInstruction: RetrySuffix(),
                    validate: ValidateChunk)
                .ConfigureAwait(false);
            if (part is null)
                throw new InvalidOperationException(
                    $"Book adapt chunk {i + 1}/{chunks.Count} failed after retry (timeout or network). Try again.");
            part = StripBookPageTags(StripFences(part));

            parts.Add(part);
            continuity = BuildContinuityBrief(part, i + 1, chunks.Count);
        }

        onProgress?.Invoke("Stitching chunk screenplays…");
        var stitched = StripBookPageTags(StitchFountainParts(parts));

        // Merge pass: unify cast tokens, cut duplicate setups, one ending
        if (parts.Count >= 2)
        {
            onProgress?.Invoke("Merge pass — unifying full-novel screenplay…");
            try
            {
                var merged = StripBookPageTags(await MergeFountainPartsAsync(
                    system, title, author, totalMinutes, parts, stitched, chat)
                    .ConfigureAwait(false));
                if (LooksLikeGoodFountain(merged) &&
                    CountSceneHeadings(merged) >= Math.Max(2, CountSceneHeadings(stitched) / 3))
                {
                    return merged;
                }

                onProgress?.Invoke("Merge pass weak — using stitched chunks…");
            }
            catch (Exception)
            {
                onProgress?.Invoke("Merge pass failed — using stitched chunks…");
            }
        }

        if (!LooksLikeGoodFountain(stitched))
            throw new InvalidOperationException(
                "Could not build a usable multi-chunk screenplay from the book.");

        return stitched;
    }

    private static async Task<string> MergeFountainPartsAsync(
        string system,
        string title,
        string? author,
        int? totalMinutes,
        IReadOnlyList<string> parts,
        string stitched,
        ChatCall chat)
    {
        var header = new StringBuilder();
        header.AppendLine("MULTI-CHUNK MERGE TASK");
        header.AppendLine($"Project title hint: {title}");
        header.AppendLine($"Author hint: {author ?? "(unknown)"}");
        header.AppendLine(totalMinutes is > 0
            ? $"TOTAL_RUNTIME_MINUTES = {totalMinutes}"
            : "TOTAL_RUNTIME_MINUTES = unlimited (natural length — do not pad)");
        header.AppendLine();
        header.AppendLine("Merge into ONE complete Fountain 1.1 screenplay:");
        header.AppendLine("- Single title page only (start of file).");
        header.AppendLine("- Consistent CHARACTER tokens (same person = same ALL-CAPS name).");
        header.AppendLine("- Real INT./EXT. locations; no INT. STORY / PAGE headings.");
        header.AppendLine("- Full story arc (do not drop the ending).");
        header.AppendLine("- Remove duplicate cold opens / repeated setups when chunks overlap.");
        header.AppendLine("- One FADE OUT / THE END at the finish.");
        header.AppendLine("- Preserve book-faithful dialogue; do not re-paraphrase iconic lines.");
        header.AppendLine("- No markdown fences, no JSON, no commentary.");
        header.AppendLine("- Do not include = page N or [[page N]] tags.");

        var useFile = Stage1FountainSessionScope.Current is { IsAvailable: true };
        string user;
        string? fountainForFile = null;
        if (useFile)
        {
            header.AppendLine();
            header.AppendLine(
                "The attached file is the full stitched Fountain (all chunks concatenated). Unify it.");
            header.AppendLine("Return the merged Fountain screenplay only.");
            user = header.ToString();
            fountainForFile = stitched;
        }
        else
        {
            header.AppendLine();
            header.AppendLine("You are given ordered Fountain partials adapted from successive book chunks.");
            const int budget = 60_000;
            var per = Math.Max(4_000, budget / Math.Max(1, parts.Count));
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i] ?? "";
                if (p.Length > per)
                    p = p[..per] + "\n\n[[… partial truncated for merge prompt …]]\n";
                header.AppendLine($"===== FOUNTAIN_PART {i + 1}/{parts.Count} =====");
                header.AppendLine(p.Trim());
                header.AppendLine();
            }
            header.AppendLine("===== END PARTS =====");
            header.AppendLine("Return the merged Fountain screenplay only.");
            user = header.ToString();
        }

        var mergeSystem = system + """


            ================================================================================
            MERGE MODE (HARD)
            ================================================================================
            You are merging multi-chunk Fountain partials into one final screenplay.
            Prefer story completeness and cast/location consistency over preserving every line.
            """;

        var text = await ExecuteStage1OperationAsync(
                    chat with { Temperature = 0.15 }, mergeSystem, user,
                    ChatCallModes.BookToFountainMerge,
                    "Merge pass",
promptVersion: "stage1-multi-chunk-merge-v1",
                correctionInstruction: "Return one complete, structurally valid Fountain screenplay with a single ending.",
                validate: ValidateChunk,
                fountainForFile: fountainForFile).ConfigureAwait(false);
        return text ?? throw new InvalidOperationException("The multi-chunk merge did not produce usable Fountain.");
    }

    private static IReadOnlyList<Stage1ValidationIssue> ValidatePrimaryPackage(
        string value,
        string bookText,
        int? totalMinutes,
        AdaptPath path)
    {
        var gate = EvaluateQuality(value, bookText, totalMinutes, path);
        return gate.Ok
            ? Array.Empty<Stage1ValidationIssue>()
            : gate.Failures.Select(failure =>
                new Stage1ValidationIssue("stage1_quality", failure, FountainJsonPath)).ToArray();
    }

    private static IReadOnlyList<Stage1ValidationIssue> ValidateChunk(string value) =>
        LooksLikeGoodFountain(value)
            ? Array.Empty<Stage1ValidationIssue>()
            : [new Stage1ValidationIssue("invalid_fountain", "The response is not usable Fountain.", FountainJsonPath)];

    // ── prompts / continuity ─────────────────────────────────────────────

    private static string BuildUserPrompt(
        string title,
        string? author,
        int pageCount,
        int? totalMinutes,
        string bookForPrompt,
        int chunkIndex,
        int chunkTotal,
        string? continuity = null)
    {
        var attachBookAsFile = Stage1BookSessionScope.Current is { IsAvailable: true };
        var lines = new List<string>
        {
            totalMinutes is > 0
                ? $"TOTAL_RUNTIME_MINUTES = {totalMinutes}"
                : "TOTAL_RUNTIME_MINUTES = unlimited (natural length — do not pad)",
            $"BOOK_CHUNK {chunkIndex + 1}/{chunkTotal}",
            "",
            $"Project title hint: {title}",
            $"Author hint: {author ?? "(unknown — infer from book if present)"}",
            $"Book page markers (approx): {pageCount}",
            "",
        };

        AppendChunkRoleLines(lines, chunkIndex, chunkTotal, continuity);
        lines.Add("");
        AppendBookTextLines(lines, attachBookAsFile, chunkTotal, bookForPrompt);
        return string.Join("\n", lines);
    }

    private static void AppendChunkRoleLines(
        List<string> lines, int chunkIndex, int chunkTotal, string? continuity)
    {
        if (chunkTotal <= 1)
        {
            lines.Add("Write the complete Fountain screenplay only (see system prompt).");
            lines.Add("Do not emit page numbers or page tags.");
            return;
        }

        if (chunkIndex == 0)
        {
            lines.Add("This is chunk 1 of a multi-chunk novel adaptation.");
            lines.Add("Write Fountain with a full title page + scenes for THIS chunk only.");
            lines.Add("Establish cast tokens and locations you will reuse later.");
            lines.Add("Do NOT write FADE OUT / THE END yet — more story follows.");
            lines.Add("Do not emit page numbers or page tags.");
            return;
        }

        lines.Add($"This is chunk {chunkIndex + 1} of {chunkTotal} of a multi-chunk novel adaptation.");
        lines.Add("Continue the SAME screenplay — NO title page.");
        lines.Add("Reuse established CHARACTER tokens and location heading wording.");
        lines.Add("Output only new INT./EXT. scenes for this chunk's story.");
        if (chunkIndex < chunkTotal - 1)
            lines.Add("Do NOT write FADE OUT / THE END yet — more story follows.");
        else
            lines.Add("This is the FINAL chunk — include resolution and FADE OUT / THE END.");
        lines.Add("Do not emit page numbers or page tags.");
        if (string.IsNullOrWhiteSpace(continuity))
            return;
        lines.Add("");
        lines.Add("CONTINUITY FROM PRIOR CHUNKS:");
        lines.Add(continuity.Trim());
    }

    private static void AppendBookTextLines(
        List<string> lines, bool attachBookAsFile, int chunkTotal, string bookForPrompt)
    {
        if (!attachBookAsFile)
        {
            lines.Add("BOOK_TEXT:");
            lines.Add(bookForPrompt);
            return;
        }

        lines.Add("BOOK_TEXT: (attached as input_file by file_id — do not expect the full book inline below.)");
        lines.Add("Use the complete attached book as the sole source of story, dialogue, and cast.");
        if (chunkTotal > 1 && !string.IsNullOrWhiteSpace(bookForPrompt))
            AppendPortionAnchors(lines, bookForPrompt);
    }

    private static void AppendPortionAnchors(List<string> lines, string bookForPrompt)
    {
        // Short anchors so multi-chunk knows which portion to adapt without re-billing full text.
        var start = bookForPrompt.Length <= 360 ? bookForPrompt : bookForPrompt[..360];
        var end = bookForPrompt.Length <= 360 ? "" : bookForPrompt[^Math.Min(360, bookForPrompt.Length)..];
        lines.Add("");
        lines.Add("PORTION ANCHOR (start of this chunk's source text):");
        lines.Add(start.Trim());
        if (string.IsNullOrWhiteSpace(end))
            return;
        lines.Add("PORTION ANCHOR (end of this chunk's source text):");
        lines.Add(end.Trim());
    }

    private static string BuildContinuityBrief(string fountainPart, int chunkDone, int chunkTotal)
    {
        var heads = CommonRegex.Matches(fountainPart, @"(?im)^(INT|EXT|EST|I/E)[^\n]+")
            .Select(m => m.Value.Trim())
            .Where(h => h.Length > 0)
            .ToList();
        var chars = CommonRegex.Matches(fountainPart, @"(?m)^([A-Z][A-Z0-9 &'.\-]{1,40})\s*$")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(c => !CommonRegex.IsMatch(c, @"^(INT|EXT|EST|I/E|FADE|CUT|TITLE|THE)\b", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Chunks completed: {chunkDone}/{chunkTotal}");
        if (chars.Count > 0)
            sb.AppendLine("Cast tokens so far: " + string.Join(", ", chars));
        if (heads.Count > 0)
        {
            sb.AppendLine("Recent scene headings:");
            foreach (var h in heads.TakeLast(4))
                sb.AppendLine("  " + h);
        }

        // Short body sample for voice continuity
        var sample = fountainPart.Length > 1200 ? fountainPart[^1200..] : fountainPart;
        sb.AppendLine("Tail of prior Fountain (do not repeat; continue after):");
        sb.AppendLine(sample.Trim());
        return sb.ToString();
    }

    private static string RetrySuffix() => """


        IMPORTANT: Previous output was not valid Fountain for our pipeline.
        Re-output Fountain only.
        - Every scene: INT./EXT. real location (not STORY, not PAGE in the heading).
        - Do not emit = page N or [[page N]] tags.
        - Use NARRATOR and CHARACTER dialogue where the book has narration or speech.
        """;

    private const string CoverageRetrySuffix = """


        IMPORTANT: Previous draft was too short or incomplete for the full book.
        Re-output a complete Fountain screenplay covering the full arc present in BOOK_TEXT.
        - Include enough INT./EXT. scenes for the target runtime.
        - Carry the story through resolution; end with FADE OUT / THE END.
        - Do not stop after the opening chapters only.
        - Do not emit = page N or [[page N]] tags.
        """;

    // ── chunking helpers ─────────────────────────────────────────────────

    private static List<string> SplitIntoUnits(string bookText)
    {
        // 1) Page markers
        var pageParts = CommonRegex.Split(bookText, @"(?=---\s*PAGE\s+\d+\s*---)", RegexOptions.IgnoreCase)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (pageParts.Count >= 3)
            return pageParts;

        // 2) Chapters
        var chapterParts = CommonRegex.Split(
                bookText,
                @"(?m)(?=^(?:CHAPTER|Chapter|BOOK|Book|PART|Part)\s+([IVXLCDM\d]+|[A-Z][A-Z\s]{0,40})\b)",
                RegexOptions.Multiline)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (chapterParts.Count >= 3)
            return chapterParts;

        // 3) Double-newline paragraphs packed later
        var paras = CommonRegex.Split(bookText, @"\n\s*\n+")
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (paras.Count >= 4)
            return paras;

        return new List<string> { bookText };
    }

    private static IEnumerable<string> SliceLongUnit(string unit, int softMax)
    {
        if (unit.Length <= softMax)
        {
            yield return unit;
            yield break;
        }

        var i = 0;
        while (i < unit.Length)
        {
            var len = Math.Min(softMax, unit.Length - i);
            if (i + len < unit.Length)
            {
                var window = unit.AsSpan(i, len);
                var breakAt = window.LastIndexOf("\n\n");
                if (breakAt < softMax / 3)
                    breakAt = window.LastIndexOf('\n');
                if (breakAt >= softMax / 3)
                    len = breakAt;
            }

            yield return unit.Substring(i, len).Trim();
            i += Math.Max(1, len);
        }
    }

    /// <summary>
    /// Exposed (not just <c>ConvertAsync</c>-internal) so callers that need to predict the exact
    /// text <see cref="ConvertHeuristic"/> will fall back to — e.g. detecting a poisoned cache —
    /// normalize the book text identically before calling it.
    /// </summary>
    public static string NormalizeBookText(string bookText)
    {
        var cleaned = PageToMovie.Core.Utils.GutenbergCleaner.StripHeaderAndFooter(bookText ?? "");
        return cleaned.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static int CountPageMarkers(string bookText) =>
        CommonRegex.Matches(bookText, @"---\s*PAGE\s+\d+\s*---", RegexOptions.IgnoreCase).Count;

    public static int CountSceneHeadings(string? fountain) =>
        CommonRegex.Matches(fountain ?? "", @"(?im)^(INT|EXT|EST|I/E)[\./ ]").Count;

    /// <summary>
    /// Split a Fountain draft into title-page/preamble + one string per scene heading.
    /// Headings match <see cref="CountSceneHeadings"/> so counts stay aligned.
    /// </summary>
    public static (string Preamble, IReadOnlyList<string> Scenes) SplitFountainByScenes(string? fountain)
    {
        var text = (fountain ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        var preamble = new StringBuilder();
        var scenes = new List<StringBuilder>();
        StringBuilder? current = null;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('.')) t = t[1..].Trim();
            if (CommonRegex.IsMatch(t, @"^(INT|EXT|EST|I/E)[\./ ]", RegexOptions.IgnoreCase))
            {
                current = new StringBuilder();
                scenes.Add(current);
                current.Append(line).Append('\n');
            }
            else if (current is not null)
                current.Append(line).Append('\n');
            else
                preamble.Append(line).Append('\n');
        }
        return (preamble.ToString(), scenes.ConvertAll(s => s.ToString()));
    }

    // ── fountain text surgery ────────────────────────────────────────────

    private static string StripTitlePage(string fountain)
    {
        fountain = fountain.Replace("\r\n", "\n").Trim();
        // Drop leading title-page key lines until first scene heading
        var lines = fountain.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) { i++; continue; }
            if (CommonRegex.IsMatch(t, @"^(Title|Credit|Author|Authors|Source|Draft date|Contact|Notes)\s*:", RegexOptions.IgnoreCase))
            {
                i++;
                continue;
            }
            if (t is "===" or "---")
            {
                i++;
                continue;
            }
            break;
        }

        // If no scene heading yet, scan forward to first INT./EXT.
        var rest = string.Join("\n", lines.Skip(i));
        var m = CommonRegex.Match(rest, @"(?im)^(INT|EXT|EST|I/E)[\./ ]");
        if (m.Success && m.Index > 0)
            rest = rest[m.Index..];
        return rest.Trim();
    }

    private static string StripTrailingEndMarkers(string fountain)
    {
        fountain = fountain.TrimEnd();
        fountain = CommonRegex.Replace(
            fountain,
            @"\n(?:FADE OUT\.?\s*\n+)?THE END\s*$",
            "",
            RegexOptions.IgnoreCase);
        fountain = CommonRegex.Replace(
            fountain,
            @"\nFADE OUT\.?\s*$",
            "",
            RegexOptions.IgnoreCase);
        return fountain.TrimEnd();
    }

    private static string EnsureDraftDate(string text)
    {
        if (CommonRegex.IsMatch(text, @"(?im)^Draft date:"))
            return text;
        var m = CommonRegex.Match(text, @"(?im)^Title:\s*.+$");
        if (m.Success)
        {
            return text.Insert(
                m.Index + m.Length,
                $"\nDraft date: {DateTime.Now:M/d/yyyy}");
        }
        return text;
    }

    /// <summary>
    /// Insert <c>FADE IN:</c> before the first scene heading if the model omitted it.
    /// Idempotent — no-op if a FADE IN: line already exists anywhere in the draft. Needed
    /// because StripFountainPageBreaks only removes an unwanted === marker; if that === was
    /// a straight substitution for FADE IN: rather than an addition alongside it, stripping
    /// it leaves nothing behind (observed in JungleBook's screenplay.fountain). Public so
    /// tests can exercise it directly, same as StripFountainPageBreaks.
    /// </summary>
    public static string EnsureFadeIn(string text)
    {
        if (CommonRegex.IsMatch(text, @"(?im)^FADE IN\s*:"))
            return text;
        var m = CommonRegex.Match(text, @"(?im)^(INT\.|EXT\.|INT\./EXT\.|I/E\.|EST\.)");
        return m.Success ? text.Insert(m.Index, "FADE IN:\n\n") : text;
    }

    /// <summary>
    /// Fit oversize books into a single-shot window (start/middle/end).
    /// Prefer multi-chunk when the book exceeds the model budget; this is a last-resort trim.
    /// </summary>
    private static string TrimBookForPrompt(string bookText, int maxChars = DefaultSingleShotBookMaxChars)
    {
        bookText = NormalizeBookText(bookText);
        var max = Math.Clamp(maxChars, 4_000, AbsoluteSingleShotCeiling);
        if (bookText.Length <= max) return bookText;

        var pages = CommonRegex.Split(bookText, @"(?=---\s*PAGE\s+\d+\s*---)", RegexOptions.IgnoreCase)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (pages.Count >= 6)
        {
            var take = Math.Max(2, pages.Count / 3);
            var head = string.Concat(pages.Take(take));
            var midStart = Math.Max(0, (pages.Count - take) / 2);
            var mid = string.Concat(pages.Skip(midStart).Take(take));
            var tail = string.Concat(pages.Skip(Math.Max(0, pages.Count - take)));
            var assembled = string.Join(
                "\n\n[[… middle of book omitted for length …]]\n\n",
                new[] { head.Trim(), mid.Trim(), tail.Trim() }.Where(s => s.Length > 0));
            if (assembled.Length <= max)
                return assembled + "\n\n[[Book excerpted (start/middle/end) — adapt a complete short film from these parts.]]\n";
            bookText = assembled;
        }

        var headBudget = (int)(max * 0.40);
        var midBudget = (int)(max * 0.28);
        var tailBudget = max - headBudget - midBudget - 200;
        if (tailBudget < 2000)
        {
            return bookText[..max] +
                   "\n\n[[Book text truncated for length — adapt what is above.]]\n";
        }

        var headPart = bookText[..headBudget];
        var midCenter = bookText.Length / 2;
        var midStartIdx = Math.Clamp(midCenter - midBudget / 2, 0, bookText.Length - midBudget);
        var midPart = bookText.Substring(midStartIdx, midBudget);
        var tailPart = bookText[^tailBudget..];
        return headPart.TrimEnd() +
               "\n\n[[… middle of book omitted for length …]]\n\n" +
               midPart.Trim() +
               "\n\n[[… later chapters omitted for length …]]\n\n" +
               tailPart.TrimStart() +
               "\n\n[[Book excerpted (start/middle/end) — adapt a complete short film covering the full arc present across these parts. Do not invent missing chapters.]]\n";
    }


    /// <summary>Normalize newlines and ensure trailing newline (Engine ScreenplayService.NormalizeText equivalent).</summary>
    public static string NormalizeFountainText(string text)
    {
        text ??= "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = StripBookPageTags(text);
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += "\n";
        return text;
    }

    private static readonly Regex SceneHeadingLineRegex = new(@"^(INT\./EXT|INT/EXT|I\./E|I/E|INT\.?|EXT\.?|EST\.?)[\./\s]", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex BookPageMarkerLine = BookTextAnalyzer.PageMarkerLine;

    /// <summary>Line-scan scene headings (no Engine FountainParser dependency).</summary>
    internal static IEnumerable<string> EnumerateSceneHeadingLines(string fountain)
    {
        foreach (var raw in (fountain ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith('.')) t = t[1..].Trim(); // forced heading
            if (SceneHeadingLineRegex.IsMatch(t))
                yield return t;
        }
    }

    /// <summary>
    /// Line-scan character cues: blank line before, non-blank after, mostly uppercase name.
    /// Close enough to FountainParser character detection for Stage‑1 repair gates.
    /// </summary>
    internal static IEnumerable<string> EnumerateCharacterCueNames(string fountain)
    {
        var lines = (fountain ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryReadCharacterCueName(lines, i, out var core))
                yield return core;
        }
    }

    private static bool TryReadCharacterCueName(string[] lines, int i, out string core)
    {
        core = "";
        var t = lines[i].Trim();
        if (!IsCueLength(t) || !HasDialogueNeighbors(lines, i) || IsNonCueLeader(t))
            return false;
        if (!TryNormalizeCueCore(t, out core))
            return false;
        return IsMostlyUppercaseName(core);
    }

    private static bool IsCueLength(string t) => t.Length is > 0 and <= 60;

    private static bool HasDialogueNeighbors(string[] lines, int i)
    {
        var prevBlank = i == 0 || string.IsNullOrWhiteSpace(lines[i - 1]);
        var nextBlank = i + 1 >= lines.Length || string.IsNullOrWhiteSpace(lines[i + 1]);
        return prevBlank && !nextBlank;
    }

    private static bool IsNonCueLeader(string t) =>
        SceneHeadingLineRegex.IsMatch(t)
        || t.StartsWith('(') || t.StartsWith('!') || t.StartsWith('>')
        || CommonRegex.IsMatch(t, @"^(FADE |CUT TO|DISSOLVE|THE END|SMASH CUT)", RegexOptions.IgnoreCase);

    private static bool TryNormalizeCueCore(string t, out string core)
    {
        core = t.TrimEnd('^', ' ', '\t');
        var paren = core.IndexOf('(');
        if (paren > 0) core = core[..paren].Trim();
        return core.Length >= 2;
    }

    private static bool IsMostlyUppercaseName(string core)
    {
        var letters = core.Where(char.IsLetter).ToArray();
        if (letters.Length < 2) return false;
        if (letters.Count(char.IsUpper) < letters.Length * 0.85) return false;
        if (core.Any(char.IsLower) && letters.Count(char.IsLower) > letters.Length * 0.15)
            return false;
        return true;
    }

    private sealed class HeuristicBookPage
    {
        public int PageNumber { get; init; }
        public string Text { get; init; } = "";
    }

    /// <summary>Local page split for ConvertHeuristic (mirrors Engine BookContextService.ParseBookPages).</summary>
    private static List<HeuristicBookPage> ParseBookPagesForHeuristic(string bookText)
    {
        bookText ??= "";
        bookText = bookText.Replace("\r\n", "\n").Replace('\r', '\n');
        var pages = new List<HeuristicBookPage>();

        var matches = BookPageMarkerLine.Matches(bookText);
        if (matches.Count > 0)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var num = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var start = m.Index + m.Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : bookText.Length;
                var body = bookText[start..end].Trim();
                pages.Add(new HeuristicBookPage { PageNumber = num, Text = body });
            }
            return pages;
        }

        var paras = CommonRegex.Split(bookText.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0 && bookText.Trim().Length > 0)
            paras.Add(bookText.Trim());

        for (var i = 0; i < paras.Count; i++)
            pages.Add(new HeuristicBookPage { PageNumber = i + 1, Text = paras[i] });
        return pages;
    }

    public static string StripFences(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = CommonRegex.Replace(text, @"^```(?:fountain|text|markdown)?\s*", "", RegexOptions.IgnoreCase);
            text = CommonRegex.Replace(text, @"\s*```\s*$", "");
        }
        return text.Trim();
    }
}
