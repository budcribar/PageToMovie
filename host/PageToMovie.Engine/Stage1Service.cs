using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Fountain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;

namespace PageToMovie.Engine;

/// <summary>
/// Book / Fountain → approved screenplay build for shot planning.
/// Operator source of truth is <c>source/screenplay.fountain</c> (via
/// <c>prompts/book_to_fountain.txt</c>). Internal Stage 1 JSON is materialised
/// only from Fountain so existing shot tools keep working — no scene-bible LLM prompt.
/// </summary>
public sealed class Stage1Service
{
    private readonly ProjectStore _projects;
    private readonly IChatClient _chat;
    private readonly BookPrepareService _books;
    private readonly CharacterBookPlateService _plates;
    private readonly IOptions<PageToMovieOptions> _opts;
    private readonly ILogger<Stage1Service> _log;
    private readonly BookTextRegistryService? _bookRegistry;
    private readonly IUserContext? _user;
    private readonly PageToMovie.Core.Abstractions.IBookFileSessionFactory? _bookFileSessionFactory;
    private readonly PageToMovie.Core.Abstractions.IFountainFileSessionFactory? _fountainFileSessionFactory;
    private readonly XaiResponsesClient? _xaiResponses;
    private readonly CastFromScreenplayService _castExtract;

    public Stage1Service(
        ProjectStore projects,
        IChatClient chat,
        BookPrepareService books,
        CharacterBookPlateService plates,
        IOptions<PageToMovieOptions> opts,
        ILogger<Stage1Service> log,
        CastFromScreenplayService castExtract,
        BookTextRegistryService? bookRegistry = null,
        IUserContext? user = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessionFactory = null,
        XaiResponsesClient? xaiResponses = null,
        PageToMovie.Core.Abstractions.IFountainFileSessionFactory? fountainFileSessionFactory = null)
    {
        _projects = projects;
        _chat = chat;
        _books = books;
        _plates = plates;
        _opts = opts;
        _log = log;
        _bookRegistry = bookRegistry;
        _user = user;
        _bookFileSessionFactory = bookFileSessionFactory;
        _xaiResponses = xaiResponses;
        _fountainFileSessionFactory = fountainFileSessionFactory;
        _castExtract = castExtract;
    }

    /// <summary>
    /// Ensure a Fountain draft exists (from book when needed), then materialise the
    /// approved build from that Fountain. Does not use a book→JSON scene-bible prompt.
    /// </summary>
    public async Task<Stage1Result> RunAsync(
        string projectId,
        int chunkPages = 10,
        int? totalMinutes = null,
        string? model = null,
        bool resume = false,
        int maxChunks = 0,
        double temperature = 0.2,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        _ = (chunkPages, resume, maxChunks, temperature);

        if (!_chat.IsConfigured)
            throw new InvalidOperationException(
                "Connect service (API key) to build a screenplay draft from the book.");

        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        model = string.IsNullOrWhiteSpace(model)
            ? ProjectModelSelection.RequirePlanning(cfg, "Screenplay draft")
            : ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Screenplay draft");

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        var draftPath = ScreenplayService.GetDraftPath(_projects, projectId);

        var book = await EnsurePreparedBookTextAsync(projectId, bookPath, model, onProgress, ct)
            .ConfigureAwait(false);
        var analysis = BookTextAnalyzer.Analyze(book);
        ThrowIfBookTextGarbled(analysis);

        var minutes = totalMinutes is > 0
            ? BookTextAnalyzer.ResolveStage1RuntimeMinutes(book, totalMinutes)
            : BookTextAnalyzer.ResolveStage1RuntimeMinutes(book);

        onProgress?.Invoke(
            $"Target runtime {minutes} min · building Fountain from book (prompts/book_to_fountain.txt)…");

        await CreateAndApproveFountainDraftAsync(projectId, model, onProgress, ct).ConfigureAwait(false);
        await AttachBookPlatesBestEffortAsync(projectId, onProgress, ct).ConfigureAwait(false);

        var fountainText = File.Exists(draftPath)
            ? await File.ReadAllTextAsync(draftPath, ct).ConfigureAwait(false)
            : "";
        var result = BuildStage1Result(projectId, draftPath, minutes, fountainText);
        AddDraftQualityNotes(result, fountainText, analysis, onProgress);
        AnnounceScreenplayReady(result, draftPath, onProgress);
        await PersistStage1ArtifactsAsync(projectId, projectDir, model, book, fountainText, result, ct)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<string> EnsurePreparedBookTextAsync(
        string projectId,
        string bookPath,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        onProgress?.Invoke("Checking book text…");
        if (!File.Exists(bookPath))
        {
            onProgress?.Invoke("No book_full.txt — running book prepare…");
            var prep = await _books.PrepareAsync(
                projectId,
                forceExtract: true,
                forceVision: false,
                autoVision: true,
                visionModel: model,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (!prep.ReadyForStage1 && !File.Exists(bookPath))
                throw new InvalidOperationException(
                    prep.StrategyReason ?? "Book text is not ready. Prepare the book first.");
        }

        if (!File.Exists(bookPath))
            throw new InvalidOperationException("No prepared book text yet.");

        return await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
    }

    private static void ThrowIfBookTextGarbled(BookTextAnalysis analysis)
    {
        if (analysis.TextQuality is TextQuality.Poor or TextQuality.Empty || analysis.GarbageScore >= 0.45)
            throw new InvalidOperationException(
                "book_full.txt is still garbled OCR. Prepare the book with vision first.");
    }

    private async Task CreateAndApproveFountainDraftAsync(
        string projectId, string model, Action<string>? onProgress, CancellationToken ct)
    {
        var draft = await ScreenplayService.CreateDraftFromBookAsync(
            _projects,
            projectId,
            _chat,
            model,
            onProgress: onProgress,
            ct: ct,
            bookRegistry: _bookRegistry,
            cacheUserId: _user?.UserId,
            bookFileSessionFactory: _bookFileSessionFactory,
            adaptationDefaults: _opts.Value.AdaptationDefaults,
            responses: _xaiResponses,
            useFakes: _opts.Value.UseFakes,
            fountainFileSessionFactory: _fountainFileSessionFactory).ConfigureAwait(false);
        if (!draft.Ok)
            throw new InvalidOperationException(draft.Error ?? "Could not create Fountain draft from book.");

        // Same Cast extract as Screenplay approve. Do not mark signed until the lock is persisted —
        // otherwise book_import auto-sign-off skips the only extract the operator would have clicked.
        onProgress?.Invoke("Building cast from screenplay…");
        var cast = await _castExtract.ExtractRequiringPerformanceLockAsync(
            projectId, force: false, onProgress: onProgress, ct: ct).ConfigureAwait(false);
        if (!cast.Ok)
            throw new InvalidOperationException(
                cast.Error ?? CastFromScreenplayService.SignOffMissingPerformanceLockMessage);

        onProgress?.Invoke("Fountain draft saved — approving screenplay…");
        var sign = ScreenplayService.SignOffIfPerformanceLockPresent(_projects, projectId);
        if (!sign.Ok)
            throw new InvalidOperationException(sign.Error ?? "Could not approve screenplay from Fountain.");
    }

    private async Task AttachBookPlatesBestEffortAsync(
        string projectId, Action<string>? onProgress, CancellationToken ct)
    {
        // Book plate attach → cast_seeds.json (Stage 2 reads Fountain + overlay)
        try
        {
            onProgress?.Invoke("Attaching book plate candidates to cast…");
            var plates = await _plates.AttachAsync(
                projectId,
                force: true,
                copyIntoAssets: true,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (plates.Ok)
                onProgress?.Invoke(
                    $"Book plates ({plates.Method}): updated={plates.CharactersUpdated} " +
                    $"skipped={plates.CharactersSkipped}");
            else
                onProgress?.Invoke($"Book plate attach skipped: {plates.Reason}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Book plate attach after Fountain approve failed");
            onProgress?.Invoke($"Book plate attach failed (non-fatal): {ex.Message}");
        }
    }

    private Stage1Result BuildStage1Result(
        string projectId, string draftPath, int minutes, string fountainText)
    {
        var stage1 = ScreenplayService.ReadStage1Lite(_projects, projectId);
        var (voCues, totalCues) = FountainParser.CountVoiceoverCues(fountainText);
        var voPct = totalCues > 0 ? voCues * 100 / totalCues : 0;
        var result = new Stage1Result
        {
            Ok = stage1.Present && stage1.SceneCount > 0,
            OutPath = draftPath,
            SceneCount = stage1.SceneCount,
            CharacterCount = stage1.CharacterCount,
            LocationCount = stage1.LocationCount,
            RuntimeSeconds = (int)(stage1.RuntimeSeconds ?? 0),
            TotalMinutes = minutes,
            VoCueCount = voCues,
            TotalDialogueCues = totalCues,
            VoPercent = voPct,
            VerifyErrors = new List<string>(),
            HardErrors = new List<string>(),
            Warnings = new List<string>(),
        };
        if (!result.Ok)
            result.HardErrors.Add("Fountain approved but no scenes were found.");
        return result;
    }

    private static void AddDraftQualityNotes(
        Stage1Result result, string fountainText, BookTextAnalysis analysis, Action<string>? onProgress)
    {
        // Re-check for issues the generation-time auto-repair may have failed to clear
        // (e.g. a transient API failure on the repair call itself). Checked from the
        // saved draft every run, not just once at generation time, so it doesn't rely
        // on catching a one-off progress message.
        WarnUnresolvedItems(
            result, onProgress,
            AdaptationFountain.FindVagueLocationHeadings(fountainText),
            count => $"{count} vague location heading(s) unresolved");
        WarnUnresolvedItems(
            result, onProgress,
            AdaptationFountain.FindGenericNumberedSpeakers(fountainText),
            count => $"{count} generic numbered speaker(s) unresolved");
        NoteHighVoiceoverShare(result, onProgress);
        NoteHighSceneCount(result, analysis, onProgress);
    }

    private static void WarnUnresolvedItems(
        Stage1Result result,
        Action<string>? onProgress,
        IReadOnlyList<string> items,
        Func<int, string> headline)
    {
        if (items.Count == 0)
            return;
        var msg = $"{headline(items.Count)}: " + string.Join("; ", items.Take(3));
        result.Warnings.Add(msg);
        onProgress?.Invoke($"Warning: {msg}");
    }

    private static void NoteHighVoiceoverShare(Stage1Result result, Action<string>? onProgress)
    {
        // Surface-only: high V.O. share is fine for confessional prose but leans clip gen on narration
        if (result.TotalDialogueCues > 0 && result.VoPercent >= 45)
        {
            onProgress?.Invoke(
                $"Note: {result.VoCueCount}/{result.TotalDialogueCues} dialogue cues are V.O. ({result.VoPercent}%) — " +
                "clip gen will lean on narration. Prefer on-camera frame cutbacks where possible.");
        }
    }

    private static void NoteHighSceneCount(
        Stage1Result result, BookTextAnalysis analysis, Action<string>? onProgress)
    {
        var softMaxScenes = AdaptationFountain.SoftMaxSceneHeadings(analysis.BookKind.ToString());
        if (result.SceneCount > softMaxScenes)
        {
            onProgress?.Invoke(
                $"Note: {result.SceneCount} scenes (soft target ≤{softMaxScenes} for {analysis.BookKind.ToApiString()}) — " +
                "shot plan / clip count may be high.");
        }
    }

    private static void AnnounceScreenplayReady(
        Stage1Result result, string draftPath, Action<string>? onProgress)
    {
        var warningsSuffix = result.Warnings.Count > 0 ? $" · {result.Warnings.Count} warning(s)" : "";
        onProgress?.Invoke(
            $"Screenplay ready · {result.SceneCount} scenes · " +
            $"{result.CharacterCount} cast · {result.LocationCount} locations · " +
            $"V.O. {result.VoCueCount}/{result.TotalDialogueCues} ({result.VoPercent}%){warningsSuffix} · {Path.GetFileName(draftPath)}");
    }

    private async Task PersistStage1ArtifactsAsync(
        string projectId,
        string projectDir,
        string model,
        string book,
        string fountainText,
        Stage1Result result,
        CancellationToken ct)
    {
        var stageIssues = new List<ModelValidationIssue>();
        if (string.IsNullOrWhiteSpace(fountainText))
            stageIssues.Add(new("missing_fountain", "Stage 1 produced no Fountain text.", "$.fountain"));
        if (result.SceneCount <= 0)
            stageIssues.Add(new("missing_scenes", "Stage 1 produced no scenes.", "$.sceneCount"));
        await StructuredOperationArtifacts.WriteAsync(
            projectDir, "stage1_adaptation", model, new { projectId, book }, result, stageIssues, ct)
            .ConfigureAwait(false);
        if (stageIssues.Count > 0)
            throw new InvalidOperationException(string.Join(" ", stageIssues.Select(i => i.Message)));
        if (result.Ok)
            _projects.TriggerAutoGitCommit(projectId, "Stage: screenplay created");
    }
}

public sealed class Stage1Result
{
    public bool Ok { get; set; }
    public string OutPath { get; set; } = "";
    public int SceneCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public int RuntimeSeconds { get; set; }
    public int TotalMinutes { get; set; }
    /// <summary>Character cues tagged V.O. (from FountainParser).</summary>
    public int VoCueCount { get; set; }
    /// <summary>All character dialogue cues.</summary>
    public int TotalDialogueCues { get; set; }
    /// <summary>0–100 integer percent of cues that are V.O.</summary>
    public int VoPercent { get; set; }
    public List<string> VerifyErrors { get; set; } = new();
    public List<string> HardErrors { get; set; } = new();
    /// <summary>
    /// Non-fatal issues that survived generation-time auto-repair (e.g. a repair call
    /// that failed both attempts). Re-checked from the saved draft on every Stage1 run,
    /// so it resurfaces here even if the original progress message was missed.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
