using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Single source of truth for a clip's duration in seconds, read from a blueprint
/// <c>veo_clips[*]</c> node. Two layers, because callers want different things:
/// <list type="bullet">
///   <item><see cref="TryReadNumericSeconds"/> — the tolerant <c>duration_seconds</c> read
///   (JSON number int/double, or numeric string), value &gt; 0. The pacing estimator and the
///   prompt builder only want Stage 2's <i>planned</i> number, with no timestamp/default fallback.</item>
///   <item><see cref="Resolve"/> — the <i>effective</i> duration used for cost: planned numeric
///   seconds, else a <c>mm:ss - mm:ss</c> timestamp span, else the supplied default.</item>
/// </list>
/// Extracted so the "duration_seconds numeric, else timestamp span, else default" rule (and the
/// tolerant number read under it) is implemented exactly once rather than re-derived per call site.
/// </summary>
internal static class ClipDuration
{
    private static readonly Regex TimestampSpan = new(@"^\s*(\d+):(\d{2})\s*-\s*(\d+):(\d{2})\s*$", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// True when the clip carries a positive <c>duration_seconds</c> — a JSON number (int or
    /// double) or a numeric string. <paramref name="seconds"/> is the parsed value (unrounded).
    /// </summary>
    public static bool TryReadNumericSeconds(JsonElement clipEl, out double seconds)
    {
        seconds = 0;
        if (clipEl.ValueKind != JsonValueKind.Object ||
            !clipEl.TryGetProperty("duration_seconds", out var el))
            return false;

        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out var i) && i > 0) { seconds = i; return true; }
            if (el.TryGetDouble(out var d) && d > 0) { seconds = d; return true; }
            return false;
        }
        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
        {
            seconds = s;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Effective clip duration: positive numeric <c>duration_seconds</c>, else a
    /// <c>mm:ss - mm:ss</c> <c>timestamp</c> span (when the end is after the start), else
    /// <paramref name="defaultSeconds"/>.
    /// </summary>
    public static double Resolve(JsonElement clipEl, double defaultSeconds)
    {
        if (TryReadNumericSeconds(clipEl, out var numeric))
            return numeric;

        if (clipEl.ValueKind == JsonValueKind.Object &&
            clipEl.TryGetProperty("timestamp", out var ts) && ts.GetString() is { } tss)
        {
            var m = TimestampSpan.Match(tss);
            if (m.Success)
            {
                var inv = CultureInfo.InvariantCulture;
                var a = int.Parse(m.Groups[1].Value, inv) * 60 + int.Parse(m.Groups[2].Value, inv);
                var b = int.Parse(m.Groups[3].Value, inv) * 60 + int.Parse(m.Groups[4].Value, inv);
                if (b > a) return b - a;
            }
        }
        return defaultSeconds;
    }
}
