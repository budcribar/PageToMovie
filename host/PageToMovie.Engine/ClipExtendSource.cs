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

    /// <summary>
    /// Duration of the predecessor's provider file — the next extend INPUT and the new clip's
    /// <c>provider_lead_in_seconds</c>. Combined: lead-in + this clip in that file (or explicit
    /// stop = end of the file). Standalone: the clip duration. No edit-input cap.
    /// </summary>
    public static double ProviderInputDurationSeconds(
        double leadInSeconds, double? durationSeconds, double? clipStopSeconds = null)
    {
        if (clipStopSeconds is { } stop && stop > 0.1)
            return stop;
        var slice = durationSeconds ?? 0;
        if (leadInSeconds > 0.1)
            return leadInSeconds + slice;
        return slice;
    }

    public static double ProviderInputDurationSeconds(ClipProviderSource src) =>
        ProviderInputDurationSeconds(src.LeadInSeconds, src.DurationSeconds, src.ClipStopSeconds);

    /// <summary>
    /// Prefer the predecessor's provider <c>file_id</c> (combined is OK). Marker / local upload
    /// are fallbacks when there is no provider file. Never refuses because of the VideoEdit cap.
    /// </summary>
    public static Choice Select(
        string? predecessorFileId,
        double predecessorLeadInSeconds,
        double? predecessorDurationSeconds,
        double? predecessorClipStopSeconds,
        string? markerFileId,
        double? markerSeconds,
        string? explicitLocalPath,
        double? explicitLocalDuration,
        string? predecessorLocalPath,
        double? predecessorLocalDuration)
    {
        if (!string.IsNullOrWhiteSpace(predecessorFileId))
        {
            var input = ProviderInputDurationSeconds(
                predecessorLeadInSeconds, predecessorDurationSeconds, predecessorClipStopSeconds);
            return new Choice(predecessorFileId, null, input > 0.1 ? input : predecessorDurationSeconds);
        }

        if (!string.IsNullOrWhiteSpace(markerFileId))
            return new Choice(markerFileId, null, markerSeconds);

        if (!string.IsNullOrWhiteSpace(explicitLocalPath))
            return new Choice(null, explicitLocalPath, explicitLocalDuration);

        if (!string.IsNullOrWhiteSpace(predecessorLocalPath))
            return new Choice(null, predecessorLocalPath, predecessorLocalDuration);

        return new Choice(null, null, null);
    }

    public static Choice SelectFromPredecessor(ClipProviderSource? src) =>
        src is null
            ? new Choice(null, null, null)
            : Select(
                src.SourceFileId,
                src.LeadInSeconds,
                src.DurationSeconds,
                src.ClipStopSeconds,
                markerFileId: null,
                markerSeconds: null,
                explicitLocalPath: null,
                explicitLocalDuration: null,
                predecessorLocalPath: null,
                predecessorLocalDuration: null);

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
