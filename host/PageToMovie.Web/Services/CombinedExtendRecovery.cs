using System.Globalization;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Combined video-extend recovery. When extend is chained from the combined
/// provider file, clip N's copy is clip-1+…+N (C3 = C1+C2+C3). Each sidecar
/// hop is one previous clip: <c>provider_lead_in_seconds</c> is how much of
/// THIS file is the previous clip (for C3 that is the full C1+C2 chain).
/// Walk backward: save the tail as the current clip, then peel the next hop
/// from the head (C2 tail + C1 head). A sliced C2 hop stops the walk — C1
/// is not in that C3. Never save a combined file as a clip.
/// </summary>
internal static class CombinedExtendRecovery
{
    /// <summary>Same threshold as <c>ClipProviderSource.IsCombined</c> / live extend saves.</summary>
    internal const double CombinedLeadInThresholdSeconds = 0.1;

    private static readonly Regex ClipRelPathRx = new(
        @"assets/video/scene_(\d{2})_clip_(\d{2})\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    internal static bool IsCombined(double leadInSeconds) =>
        leadInSeconds > CombinedLeadInThresholdSeconds;

    /// <summary>True when a local file at the current-clip path is still the combined video
    /// (longer than the previous-clip lead-in), not an already-sliced tail.</summary>
    internal static bool IsLocalDurationCombined(double durationSeconds, double leadInSeconds) =>
        durationSeconds > leadInSeconds + CombinedLeadInThresholdSeconds;

    /// <summary>
    /// Predecessor hops that still apply to this file's head. Peel the largest sidecar
    /// hop that is still strictly inside the remaining head (nearest previous clip), then
    /// repeat. Order of <paramref name="predecessorSidecarLeadIns"/> does not matter.
    /// A sliced C2 hop (head ≈ that hop) stops — C1 is not in that C3. A full previous
    /// chain (C3 lead-in = C1+C2) walks C2's hop and splits C1 out of the head.
    /// </summary>
    internal static IReadOnlyList<double> PlanPredecessorHops(
        double currentLeadInSeconds, IEnumerable<double>? predecessorSidecarLeadIns)
    {
        var planned = new List<double>();
        if (predecessorSidecarLeadIns is null || !IsCombined(currentLeadInSeconds))
            return planned;

        var remaining = predecessorSidecarLeadIns.Where(IsCombined).ToList();
        var remainingHead = currentLeadInSeconds;
        while (remaining.Count > 0)
        {
            var idx = -1;
            var best = 0.0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var hop = remaining[i];
                if (IsLocalDurationCombined(remainingHead, hop) && hop >= best)
                {
                    best = hop;
                    idx = i;
                }
            }
            if (idx < 0)
                break;
            planned.Add(best);
            remaining.RemoveAt(idx);
            remainingHead = best;
        }
        return planned;
    }

    internal static bool TryParseClipRelativePath(string? relativePath, out int scene, out int clip)
    {
        scene = 0;
        clip = 0;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var m = ClipRelPathRx.Match(relativePath.Replace('\\', '/'));
        if (!m.Success)
            return false;
        scene = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        clip = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return scene > 0 && clip > 0;
    }

    /// <summary>
    /// Path format matches <c>MediaRegistryService.ClipRelativePath</c> (Engine) —
    /// Web cannot reference Engine, so the format string lives here next to the only parser.
    /// </summary>
    internal static string ClipRelativePath(int scene, int clip) =>
        $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";

    internal static bool TryGetNthPreviousClipRelativePath(
        string? relativePath, int steps, out string previousRelativePath)
    {
        previousRelativePath = "";
        if (steps <= 0 || !TryParseClipRelativePath(relativePath, out var scene, out var clip))
            return false;
        if (clip <= steps)
            return false;
        previousRelativePath = ClipRelativePath(scene, clip - steps);
        return true;
    }

    internal static bool TryGetPreviousClipRelativePath(string? relativePath, out string previousRelativePath) =>
        TryGetNthPreviousClipRelativePath(relativePath, 1, out previousRelativePath);

    /// <summary>Prefer a local combined file; fall back to the provider URL; null if neither.</summary>
    internal static string? PreferCombinedSource(string? localCombinedUrl, string? providerUrl)
    {
        if (!string.IsNullOrWhiteSpace(localCombinedUrl))
            return localCombinedUrl;
        return string.IsNullOrWhiteSpace(providerUrl) ? null : providerUrl;
    }
}
