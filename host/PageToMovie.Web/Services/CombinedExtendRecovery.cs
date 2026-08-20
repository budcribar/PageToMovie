using System.Globalization;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Combined video-extend recovery: the provider (or a leftover local) copy of clip N is
/// previous-clip + new tail. The current clip must receive only the tail; the head is the
/// previous clip (same scene, clip-1) when that file is missing locally.
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
    /// Previous clip in the same scene (clip-1), or false for clip 1 / a non-clip path.
    /// Path format matches <c>MediaRegistryService.ClipRelativePath</c> (Engine) —
    /// Web cannot reference Engine, so the format string lives here next to the only parser.
    /// </summary>
    internal static bool TryGetPreviousClipRelativePath(string? relativePath, out string previousRelativePath)
    {
        previousRelativePath = "";
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var m = ClipRelPathRx.Match(relativePath.Replace('\\', '/'));
        if (!m.Success)
            return false;
        var scene = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var clip = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (clip <= 1)
            return false;
        previousRelativePath = $"assets/video/scene_{scene:D2}_clip_{clip - 1:D2}.mp4";
        return true;
    }

    /// <summary>Prefer a local combined file; fall back to the provider URL; null if neither.</summary>
    internal static string? PreferCombinedSource(string? localCombinedUrl, string? providerUrl)
    {
        if (!string.IsNullOrWhiteSpace(localCombinedUrl))
            return localCombinedUrl;
        return string.IsNullOrWhiteSpace(providerUrl) ? null : providerUrl;
    }
}
