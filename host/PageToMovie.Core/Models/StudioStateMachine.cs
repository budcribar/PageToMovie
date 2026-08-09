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
/// Derived from project status — not a separately stored workflow column.
/// </summary>
public enum StudioPhase
{
    /// <summary>Step 0: Personal studio API keys missing (BYOK setup required).</summary>
    SetupRequired = 0,

    /// <summary>Step 1a: No source file (Fountain, TXT, or PDF) imported yet.</summary>
    ImportRequired = 1,

    /// <summary>Step 1b: PDF uploaded but raw text / OCR extraction is still needed before Stage 1.</summary>
    TextExtractionPending = 2,

    /// <summary>Step 1c: Source present; screenplay draft not yet signed off for cast/shots.</summary>
    ScreenplayDraft = 3,

    /// <summary>Step 1d: Screenplay signed off. Unlocks Cast & Estimate.</summary>
    ScreenplayApproved = 4,

    /// <summary>Step 2: Shot plan (Stage 2) built and up to date. Unlocks Film generation (with cast).</summary>
    ShotPlanReady = 5,

    /// <summary>
    /// Step 3: Production media ready for review/export.
    /// Reserved until <see cref="AdaptationStatus"/> exposes clip-completeness rollup;
    /// <see cref="StudioStateMachine.DeterminePhase"/> does not return this yet.
    /// Navigation to Review currently opens at <see cref="ShotPlanReady"/> (same as legacy strip).
    /// </summary>
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
/// Phase is derived from <see cref="AdaptationStatus"/>; mutations stay event-driven (sign-off, stage2, generate).
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

        if (book?.BookKind?.Equals("fountain", StringComparison.OrdinalIgnoreCase) == true)
            return SourceDocumentType.Fountain;

        if (book?.PdfExists == true)
            return SourceDocumentType.Pdf;

        if (book?.BookTextExists == true)
            return SourceDocumentType.Text;

        // Draft or sign-off implies a Fountain source even when DraftExists was omitted from a partial DTO.
        if (screenplay?.DraftExists == true
            || screenplay?.Signed == true
            || screenplay?.ReadyForShots == true)
            return SourceDocumentType.Fountain;

        return SourceDocumentType.None;
    }

    /// <summary>
    /// True when the operator has signed off a Fountain draft for cast / shot planning.
    /// Stage 1 package alone does <b>not</b> count — sign-off is the product gate
    /// (<see cref="ScreenplayStatus.Signed"/> / <see cref="ScreenplayStatus.ReadyForShots"/>).
    /// </summary>
    public static bool IsScreenplayApproved(ScreenplayStatus? screenplay)
    {
        if (screenplay is null)
            return false;
        return screenplay.ReadyForShots || screenplay.Signed;
    }

    /// <summary>
    /// True when Stage 2 blueprint is present, has clips, and is not stale vs the screenplay.
    /// </summary>
    public static bool IsShotPlanReady(Stage2PlanStatus? stage2)
    {
        if (stage2 is null)
            return false;
        return stage2.Stage2Ready
            && stage2.Stage2Clips > 0
            && !stage2.Stage2Stale;
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

        // PDF imported but text/OCR extraction not completed yet (no book text and no screenplay draft/sign-off).
        if (sourceType == SourceDocumentType.Pdf
            && status.Book is { BookTextExists: false }
            && !IsScreenplayApproved(status.Screenplay)
            && status.Screenplay is not { DraftExists: true })
        {
            return StudioPhase.TextExtractionPending;
        }

        if (!IsScreenplayApproved(status.Screenplay))
            return StudioPhase.ScreenplayDraft;

        if (!IsShotPlanReady(status.Stage2))
            return StudioPhase.ScreenplayApproved;

        // Clip-completeness is not yet on AdaptationStatus — stay on ShotPlanReady.
        // When a rollup lands, return ReviewReady here without changing call sites.
        return StudioPhase.ShotPlanReady;
    }

    /// <summary>
    /// Determines whether navigation to a target step is permitted under the current phase.
    /// Returns (allowed, blockedReason).
    /// </summary>
    /// <param name="targetStep">Studio chrome step.</param>
    /// <param name="currentPhase">From <see cref="DeterminePhase"/>.</param>
    /// <param name="castReady">Every cast member has approved voice + locked look when required.</param>
    /// <param name="isStage2Stale">Shot plan out of date vs screenplay (Film blocked even if phase lags).</param>
    public static (bool Allowed, string BlockedReason) CanNavigateTo(
        StudioStep targetStep,
        StudioPhase currentPhase,
        bool castReady = true,
        bool isStage2Stale = false)
    {
        switch (targetStep)
        {
            case StudioStep.Setup:
                return (true, string.Empty);

            case StudioStep.Book:
                if (currentPhase == StudioPhase.SetupRequired)
                    return (false, "Connect your API keys in Setup first");
                return (true, string.Empty);

            case StudioStep.Cast:
                if (currentPhase == StudioPhase.TextExtractionPending)
                    return (false, "PDF text / OCR extraction in progress. Complete import first");
                if (currentPhase < StudioPhase.ScreenplayApproved)
                    return (false, "Approve the screenplay first");
                return (true, string.Empty);

            case StudioStep.Estimate:
                if (currentPhase < StudioPhase.ScreenplayApproved)
                    return (false, "Finish importing the book and approve the screenplay first");
                return (true, string.Empty);

            case StudioStep.Film:
                if (currentPhase < StudioPhase.ScreenplayApproved)
                    return (false, "Approve the screenplay first");
                if (isStage2Stale)
                    return (false, "Update the shot plan first");
                if (currentPhase < StudioPhase.ShotPlanReady)
                    return (false, "Finish the shot plan first");
                if (!castReady)
                    return (false, "Approve every character voice + locked image before generating video");
                return (true, string.Empty);

            case StudioStep.Review:
                // Legacy strip unlocked Review with the shot plan (same as Film phase).
                // Cast is not required to open Review chrome; Film generation still checks castReady.
                if (currentPhase < StudioPhase.ShotPlanReady)
                    return (false, "Finish the shot plan first");
                return (true, string.Empty);

            default:
                return (true, string.Empty);
        }
    }
}
