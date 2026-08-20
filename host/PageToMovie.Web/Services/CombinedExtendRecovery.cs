using System.Globalization;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Combined video-extend recovery. Each sidecar hop is one previous clip:
/// <c>provider_lead_in_seconds</c> is how much of THIS file is the previous clip.
/// Walk backward: save the tail as the current clip, then if the extracted head's
/// sidecar also has lead-in &gt; 0.1 and the head is longer than that hop, split
/// again (clip-2 tail + clip-1 head). Never save a combined file as a clip.
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
    /// Predecessor hops that still apply to this file's head. Each sidecar is one hop;
    /// stop when the remaining head is no longer than that hop (sliced C2: C1 is not in C3).
    /// <paramref name="predecessorSidecarLeadIns"/> is clip-1, then clip-2, … raw sidecar values.
    /// </summary>
    internal static IReadOnlyList<double> PlanPredecessorHops(
        double currentLeadInSeconds, IEnumerable<double>? predecessorSidecarLeadIns)
    {
        var planned = new List<double>();
        if (predecessorSidecarLeadIns is null || !IsCombined(currentLeadInSeconds))
            return planned;

        var remainingHead = currentLeadInSeconds;
        foreach (var prevLead in predecessorSidecarLeadIns)
        {
            if (!IsCombined(prevLead) || !IsLocalDurationCombined(remainingHead, prevLead))
                break;
            planned.Add(prevLead);
            remainingHead = prevLead;
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
