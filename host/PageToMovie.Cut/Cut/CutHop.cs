using System.Globalization;
using System.Text.Json;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Hop / video-extend slice inside a take MP4. The file may be a combined
/// provider chain (previous clip head + this clip tail). User-facing in/out
/// is the slice, not t=0 of the file.
/// </summary>
public readonly record struct CutHop(
    double LeadInSeconds,
    double? ClipStartSeconds,
    double? ClipStopSeconds,
    double? DurationSeconds)
{
    public const string LeadInProperty = "provider_lead_in_seconds";
    public const string ClipStartProperty = "provider_clip_start_seconds";
    public const string ClipStopProperty = "provider_clip_stop_seconds";
    public const string DurationProperty = "duration_seconds";

    /// <summary>Same threshold as Film's combined-extend lead-in.</summary>
    public const double CombinedThresholdSeconds = 0.1;

    public static CutHop None { get; } = new(0, null, null, null);

    public bool HasSlice =>
        LeadInSeconds > CombinedThresholdSeconds
        || ClipStartSeconds is > 0
        || ClipStopSeconds is > CombinedThresholdSeconds;

    public static CutHop Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return None;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new CutHop(
                Num(r, LeadInProperty) ?? 0,
                Num(r, ClipStartProperty),
                Num(r, ClipStopProperty),
                Num(r, DurationProperty));
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>
    /// Mark-in / mark-out in file time. Prefer start/stop; else lead-in →
    /// (lead-in + sidecar duration) or file duration; else 0…file.
    /// </summary>
    public static (double MarkIn, double MarkOut) SeedInOut(CutHop hop, double fileDurationSec)
    {
        if (!TrySlice(hop, fileDurationSec, out var inn, out var outt))
        {
            if (fileDurationSec > 0)
                return ClipInOut.Clamp(0, fileDurationSec, fileDurationSec);
            if (hop.DurationSeconds is > 0 sidecar)
                return ClipInOut.Clamp(0, sidecar, sidecar);
            return (0, 0);
        }

        if (fileDurationSec > 0)
            return ClipInOut.Clamp(inn, outt, fileDurationSec);
        return (inn, outt);
    }

    public static bool TrySlice(CutHop hop, double fileDurationSec, out double markIn, out double markOut)
    {
        markIn = 0;
        markOut = 0;
        var start = SliceStart(hop);
        var stop = SliceEnd(hop, fileDurationSec);
        if (stop <= start)
            return false;
        markIn = start;
        markOut = stop;
        return true;
    }

    public static double SliceStart(CutHop hop)
    {
        if (hop.ClipStartSeconds is { } start && start >= 0 && !double.IsNaN(start) && !double.IsInfinity(start))
            return start;
        if (hop.LeadInSeconds > CombinedThresholdSeconds)
            return hop.LeadInSeconds;
        return 0;
    }

    public static double SliceEnd(CutHop hop, double fileDurationSec)
    {
        if (hop.ClipStopSeconds is { } stop && stop > CombinedThresholdSeconds
            && !double.IsNaN(stop) && !double.IsInfinity(stop))
        {
            return fileDurationSec > 0 ? Math.Min(stop, fileDurationSec) : stop;
        }

        var start = SliceStart(hop);
        if (hop.DurationSeconds is { } slice && slice > 0
            && hop.LeadInSeconds > CombinedThresholdSeconds)
        {
            var inferred = start + slice;
            return fileDurationSec > 0 ? Math.Min(inferred, fileDurationSec) : inferred;
        }

        return fileDurationSec > 0 ? fileDurationSec : 0;
    }

    private static double? Num(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && !double.IsNaN(d) && !double.IsInfinity(d))
            return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            return parsed;
        return null;
    }
}
