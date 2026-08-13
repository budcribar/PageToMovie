namespace PageToMovie.Core.Models;

/// <summary>
/// Source document format imported into the project.
/// </summary>
public enum SourceDocumentType
{
    None = 0,
    Fountain = 1,  // Already formatted screenplay (.fountain / .spmd) -> skips Stage 1 book conversion
    Text = 2,      // Plain text document (.txt) -> converted via Stage 1 to Fountain
    Pdf = 3        // PDF document (.pdf) -> extracted (native text or OCR) then converted via Stage 1
}

/// <summary>
/// Discrete phases in the PageToMovie studio production pipeline.
/// </summary>
public enum StudioPhase
{
    /// <summary>Step 0: Personal studio API keys missing (BYOK setup required).</summary>
    SetupRequired = 0,

    /// <summary>Step 1a: No source file (Fountain, TXT, or PDF) imported yet.</summary>
    ImportRequired = 1,

    /// <summary>Step 1b: PDF uploaded but raw text / OCR extraction is still needed before Stage 1.</summary>
    TextExtractionPending = 2,

    /// <summary>Step 1c: Source text ready, Fountain screenplay draft created but unapproved.</summary>
    ScreenplayDraft = 3,

    /// <summary>Step 1d: Screenplay signed off/approved. Unlocks Cast & Estimate.</summary>
    ScreenplayApproved = 4,

    /// <summary>Step 2: Shot plan (Stage 2) built and up to date. Unlocks Film / Scenes generation.</summary>
    ShotPlanReady = 5,

    /// <summary>Step 3: All scene clips generated and ready for review, playback, and export.</summary>
    ReviewReady = 6
}

/// <summary>
/// Top-level Studio navigation steps.
/// </summary>
public enum StudioStep
{
    Setup,
    Book,
    Cast,
    Estimate,
    Film,
    Review
}

/// <summary>
/// Single source of truth for studio pipeline state evaluation and step transition gating.
/// </summary>
public static class StudioStateMachine
{
    /// <summary>
    /// Detects the type of source document currently present in the project status.
    /// </summary>
    public static SourceDocumentType DetectSourceType(BookSourceStatus? book, ScreenplayStatus? screenplay)
    {
        if (book is null && screenplay is null)
            return SourceDocumentType.None;

        if (book is { BookKind: SourceDocumentType.Fountain })
            return SourceDocumentType.Fountain;

        if (book is { BookKind: SourceDocumentType.Pdf } || book?.PdfExists == true)
            return SourceDocumentType.Pdf;

        if (book is { BookKind: SourceDocumentType.Text } || book?.BookTextExists == true)
            return SourceDocumentType.Text;

        if (screenplay?.DraftExists == true)
            return SourceDocumentType.Fountain;

        return SourceDocumentType.None;
    }

    /// <summary>
    /// Evaluates the discrete pipeline phase given the project's <see cref="AdaptationStatus"/>.
    /// Handles Fountain (direct screenplay), TXT (plain text), and PDF (native or OCR text extraction).
    /// </summary>
    public static StudioPhase DeterminePhase(AdaptationStatus? status)
    {
        if (status is null)
            return StudioPhase.ImportRequired;

        if (!status.XaiConfigured)
            return StudioPhase.SetupRequired;

        var sourceType = DetectSourceType(status.Book, status.Screenplay);
        if (sourceType == SourceDocumentType.None)
            return StudioPhase.ImportRequired;

        // PDF or TXT source imported but text/OCR extraction not completed yet
        if (sourceType == SourceDocumentType.Pdf && !status.Book.BookTextExists && !status.Screenplay.DraftExists)
        {
            return StudioPhase.TextExtractionPending;
        }

        // Direct Fountain import bypasses Stage 1 book conversion.
        // TXT / PDF sources require Stage 1 conversion to produce a screenplay draft.
        var screenplayApproved = status.Screenplay.Signed
            || status.Screenplay.ReadyForShots
            || (status.Stage1.Present && status.Stage1.SceneCount > 0);

        if (!screenplayApproved)
            return StudioPhase.ScreenplayDraft;

        var shotPlanReady = status.Stage2.Stage2Ready
            && status.Stage2.Stage2Clips > 0
            && !status.Stage2.Stage2Stale;

        if (!shotPlanReady)
            return StudioPhase.ScreenplayApproved;

        return status.Stage2.Stage2Ready ? StudioPhase.ShotPlanReady : StudioPhase.ScreenplayApproved;
    }

    /// <summary>
    /// Determines whether navigation to a target step is permitted under the current phase.
    /// Returns (allowed, blockedReason).
    /// </summary>
    public static (bool Allowed, string BlockedReason) CanNavigateTo(
        StudioStep targetStep,
        StudioPhase currentPhase,
        bool castReady = true,
        bool isStage2Stale = false)
    {
        return targetStep switch
        {
            StudioStep.Setup => (true, string.Empty),
            StudioStep.Book => CanNavigateToBook(currentPhase),
            StudioStep.Cast => CanNavigateToCast(currentPhase),
            StudioStep.Estimate => CanNavigateToEstimate(currentPhase),
            StudioStep.Film => CanNavigateToFilm(currentPhase, castReady, isStage2Stale),
            StudioStep.Review => CanNavigateToReview(currentPhase),
            _ => (true, string.Empty),
        };
    }

    private static (bool Allowed, string BlockedReason) CanNavigateToBook(StudioPhase currentPhase)
    {
        if (currentPhase == StudioPhase.SetupRequired)
            return (false, "Connect your API keys in Setup first");
        return (true, string.Empty);
    }

    private static (bool Allowed, string BlockedReason) CanNavigateToCast(StudioPhase currentPhase)
    {
        if (currentPhase == StudioPhase.TextExtractionPending)
            return (false, "PDF text / OCR extraction in progress. Complete import first");
        if (currentPhase < StudioPhase.ScreenplayApproved)
            return (false, "Approve the screenplay first");
        return (true, string.Empty);
    }

    private static (bool Allowed, string BlockedReason) CanNavigateToEstimate(StudioPhase currentPhase)
    {
        if (currentPhase < StudioPhase.ScreenplayApproved)
            return (false, "Finish importing the book and approve the screenplay first");
        return (true, string.Empty);
    }

    private static (bool Allowed, string BlockedReason) CanNavigateToFilm(
        StudioPhase currentPhase,
        bool castReady,
        bool isStage2Stale)
    {
        if (currentPhase < StudioPhase.ScreenplayApproved)
            return (false, "Approve the screenplay first");
        if (isStage2Stale)
            return (false, "Update the shot plan first");
        if (currentPhase < StudioPhase.ShotPlanReady)
            return (false, "Finish the shot plan first");
        if (!castReady)
            return (false, "Approve every character voice + locked image before generating video");
        return (true, string.Empty);
    }

    private static (bool Allowed, string BlockedReason) CanNavigateToReview(StudioPhase currentPhase)
    {
        if (currentPhase < StudioPhase.ShotPlanReady)
            return (false, "Finish the shot plan first");
        return (true, string.Empty);
    }
}
