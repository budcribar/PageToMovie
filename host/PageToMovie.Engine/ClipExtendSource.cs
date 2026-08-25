using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Video-extend input selection and saved-slice math.
/// Extend chains the predecessor's provider file (<c>source_file_id</c>), including when that
/// file is already combined (C1+C2+…). <see cref="SupportedModelEntry.MaxEditInputDurationSeconds"/>
/// caps the user-facing saved / AI-Edit slice only — never the extend INPUT.
/// </summary>
public static class ClipExtendSource
{
    public readonly record struct Choice(string? FileId, string? LocalPath, double? InputDurationSeconds)
    {
        public bool HasInput => !string.IsNullOrWhiteSpace(FileId) || !string.IsNullOrWhiteSpace(LocalPath);
    }

    public readonly record struct PredecessorOffer(
        string? FileId,
        double LeadInSeconds,
        double? DurationSeconds,
        double? ClipStopSeconds = null,
        string? LocalPath = null,
        double? LocalDuration = null,
        double? MeasuredDurationSeconds = null);

    public readonly record struct FallbackOffer(
        string? MarkerFileId = null,
        double? MarkerSeconds = null,
        string? ExplicitLocalPath = null,
        double? ExplicitLocalDuration = null);

    /// <summary>
    /// Duration of the predecessor's provider file — the next extend INPUT and the new clip's
    /// <c>provider_lead_in_seconds</c>. Combined: lead-in + this clip in that file (or explicit
    /// stop = end of the file). Standalone: the clip duration. No edit-input cap.
    /// </summary>
    public static double ProviderInputDurationSeconds(
        double leadInSeconds,
        double? durationSeconds,
        double? clipStopSeconds = null,
        double? measuredDurationSeconds = null)
    {
        // A measured length beats a recorded stop. Both describe the same end of the same file,
        // but the recorded stop was computed from the REQUESTED duration, so it inherits exactly
        // the rounding this parameter exists to correct — and it is the value that put the seam
        // past the start of the new footage, clipping the first word off every extended clip.
        if (measuredDurationSeconds is { } measured && measured > 0.1)
            return leadInSeconds > 0.1 ? leadInSeconds + measured : measured;

        if (clipStopSeconds is { } stop && stop > 0.1)
            return stop;
        var slice = durationSeconds ?? 0;
        if (leadInSeconds > 0.1)
            return leadInSeconds + slice;
        return slice;
    }

    public static double ProviderInputDurationSeconds(ClipProviderSource src) =>
        ProviderInputDurationSeconds(
            src.LeadInSeconds, src.DurationSeconds, src.ClipStopSeconds, src.MeasuredDurationSeconds);

    /// <summary>
    /// Prefer the predecessor's provider <c>file_id</c> (combined is OK). Marker / local upload
    /// are fallbacks when there is no provider file. Never refuses because of the VideoEdit cap.
    /// </summary>
    public static Choice Select(PredecessorOffer predecessor, FallbackOffer fallback = default)
    {
        if (!string.IsNullOrWhiteSpace(predecessor.FileId))
        {
            var input = ProviderInputDurationSeconds(
                predecessor.LeadInSeconds, predecessor.DurationSeconds, predecessor.ClipStopSeconds,
                predecessor.MeasuredDurationSeconds);
            return new Choice(predecessor.FileId, null, input > 0.1 ? input : predecessor.DurationSeconds);
        }

        if (!string.IsNullOrWhiteSpace(fallback.MarkerFileId))
            return new Choice(fallback.MarkerFileId, null, fallback.MarkerSeconds);

        if (!string.IsNullOrWhiteSpace(fallback.ExplicitLocalPath))
            return new Choice(null, fallback.ExplicitLocalPath, fallback.ExplicitLocalDuration);

        if (!string.IsNullOrWhiteSpace(predecessor.LocalPath))
            return new Choice(null, predecessor.LocalPath, predecessor.LocalDuration);

        return new Choice(null, null, null);
    }

    /// <summary>
    /// Predecessor was regenerated in this same job (C1+C2 selected together).
    /// Chain only the new take's provider <c>file_id</c>, or its current-take MP4
    /// when fakes still keep bytes on disk. Leftover local takes and browser
    /// extend-source markers are the previous take — using them after C1's
    /// transient MP4 was deleted re-uploads a stale (often combined) file and
    /// can OOM the host.
    /// </summary>
    public static Choice SelectAfterPredecessorRegen(PredecessorOffer predecessor)
    {
        if (!string.IsNullOrWhiteSpace(predecessor.FileId))
        {
            var input = ProviderInputDurationSeconds(
                predecessor.LeadInSeconds, predecessor.DurationSeconds, predecessor.ClipStopSeconds,
                predecessor.MeasuredDurationSeconds);
            return new Choice(predecessor.FileId, null, input > 0.1 ? input : predecessor.DurationSeconds);
        }

        if (!string.IsNullOrWhiteSpace(predecessor.LocalPath))
            return new Choice(null, predecessor.LocalPath, predecessor.LocalDuration);

        return new Choice(null, null, null);
    }

    public static Choice SelectFromPredecessor(ClipProviderSource? src) =>
        src is null
            ? new Choice(null, null, null)
            : Select(new PredecessorOffer(
                src.SourceFileId,
                src.LeadInSeconds,
                src.DurationSeconds,
                src.ClipStopSeconds,
                MeasuredDurationSeconds: src.MeasuredDurationSeconds));

    /// <summary>Lead-in written on the new clip: how much of THIS provider file is previous.</summary>
    public static double? NewClipLeadInSeconds(double? previousInputDurationSeconds) =>
        previousInputDurationSeconds is { } d && d > 0.1 ? d : null;

    /// <summary>
    /// Window of this screenplay clip inside the provider file: lead-in → end
    /// (end = lead-in + generated duration). Saved/edit duration is capped separately.
    /// </summary>
    public static (double Start, double Stop) ClipWindowInProviderFile(
        double leadInSeconds, double generatedDurationSeconds)
    {
        var start = Math.Max(0, leadInSeconds);
        return (start, start + Math.Max(0, generatedDurationSeconds));
    }

    /// <summary>User-facing saved / AI-Edit slice: screenplay tail, capped by the VideoEdit catalog.</summary>
    public static double SavedSliceDurationSeconds(double screenplayClipSeconds) =>
        SupportedModelCatalog.CapToVideoEditInput(screenplayClipSeconds);

    /// <summary>
    /// Duration to save from a combined provider file. Null when the file is not longer than
    /// the lead-in (must not save the raw combined file). Otherwise the screenplay tail,
    /// capped by <see cref="SupportedModelCatalog.CapToVideoEditInput"/>.
    /// </summary>
    public static double? SavedSliceDurationFromCombined(double combinedSeconds, double leadInSeconds)
    {
        if (combinedSeconds <= leadInSeconds + 0.1)
            return null;
        return SavedSliceDurationSeconds(combinedSeconds - leadInSeconds);
    }
}
