namespace PageToMovie.Core.Utils;

/// <summary>
/// Scene-level Play is allowed only when every planned clip is actually playable
/// (a real MP4, not just an OnDisk <c>.client.json</c> / sidecar marker).
/// Per-clip play in the editor is separate and is not gated here.
/// </summary>
public static class ScenePlayGate
{
    /// <summary>Same 1KB floor the video index uses for a real MP4 (markers are smaller).</summary>
    public const long MinPlayableVideoBytes = 1024;

    public static string FormatClipLabel(int scene, int clip) => $"S{scene:D2} C{clip:D2}";

    public static bool HasServerVideo(long sizeBytes) => sizeBytes >= MinPlayableVideoBytes;

    /// <summary>
    /// True when the directory index has a real MP4 for this clip. A
    /// <c>.client.json</c> or <c>.clip.json</c> marker alone is not enough.
    /// </summary>
    public static bool HasServerMp4(IReadOnlyDictionary<string, long> videoIndex, int scene, int clip)
    {
        if (videoIndex is null || videoIndex.Count == 0)
            return false;

        var fileName = $"scene_{scene:D2}_clip_{clip:D2}.mp4";
        if (videoIndex.TryGetValue(fileName, out var sz) && HasServerVideo(sz))
            return true;

        var prefix = $"scene_{scene:D2}_clip_{clip:D2}_take_";
        foreach (var kv in videoIndex)
        {
            if (kv.Value < MinPlayableVideoBytes)
                continue;
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (kv.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static List<int> MissingServerVideoClips(
        IReadOnlyDictionary<string, long> videoIndex,
        int scene,
        IEnumerable<int> plannedClipNumbers)
    {
        var missing = new List<int>();
        foreach (var cn in plannedClipNumbers.Distinct().OrderBy(x => x))
        {
            if (cn <= 0)
                continue;
            if (!HasServerMp4(videoIndex, scene, cn))
                missing.Add(cn);
        }
        return missing;
    }

    public static string MissingClipDisabledReason(int scene, int clip) =>
        $"{FormatClipLabel(scene, clip)} is still missing — play that clip in the editor, or wait until generate finishes";

    /// <summary>Leftover error after Play was clicked anyway — names every missing clip.</summary>
    public static string FormatPlayFailedError(string noun, IReadOnlyList<string> missingLabels)
    {
        var listed = missingLabels is { Count: > 0 }
            ? string.Join(", ", missingLabels)
            : "a clip";
        return $"Could not play the selected {noun} — {listed}: clip video missing (404). Generate those clips first or connect the media folder.";
    }

    /// <summary>
    /// Per-clip Play: only THIS clip's media. A sibling hole must not disable
    /// a clip that has a real MP4 or a confirmed local file. Sidecar /
    /// <c>.client.json</c> markers are not playable on their own.
    /// </summary>
    public static bool IsClipPlayable(bool hasServerVideo, bool hasLocalVideo = false) =>
        hasServerVideo || hasLocalVideo;

    public static bool IsClipPlayable(long sizeBytes, bool hasLocalVideo = false) =>
        IsClipPlayable(HasServerVideo(sizeBytes), hasLocalVideo);

    public static (bool CanPlay, string? DisabledReason) DecideOneClipPlay(
        int sceneNumber,
        int clipNumber,
        bool hasServerVideo,
        bool hasLocalVideo = false)
    {
        if (IsClipPlayable(hasServerVideo, hasLocalVideo))
            return (true, null);
        return (false, MissingClipDisabledReason(sceneNumber, clipNumber));
    }

    /// <summary>
    /// When scene detail is not loaded: this clip is playable unless the scene
    /// list marks it as missing server video (and no local file was confirmed).
    /// Do not require the rest of the scene to be complete.
    /// </summary>
    public static bool IsClipPlayableFromSceneMissingList(
        int clipNumber,
        IReadOnlyList<int>? missingServerVideoClips,
        bool hasLocalVideo = false)
    {
        if (hasLocalVideo)
            return true;
        var missing = missingServerVideoClips ?? Array.Empty<int>();
        return !missing.Contains(clipNumber);
    }

    /// <summary>
    /// Scene Play / Play selected: every planned clip must be playable
    /// (server MP4 or confirmed local file). First hole wins the tooltip.
    /// </summary>
    public static (bool CanPlay, string? DisabledReason) DecideScenePlay(
        int sceneNumber,
        int clipCount,
        IReadOnlyList<int> missingServerVideoClips,
        Func<int, bool>? hasLocalVideo = null,
        bool compositeExists = false)
    {
        if (clipCount <= 0)
        {
            return compositeExists
                ? (true, null)
                : (false, $"S{sceneNumber:D2} has no clips yet");
        }

        var missing = missingServerVideoClips ?? Array.Empty<int>();
        foreach (var cn in missing)
        {
            if (hasLocalVideo is not null && hasLocalVideo(cn))
                continue;
            return (false, MissingClipDisabledReason(sceneNumber, cn));
        }

        return (true, null);
    }

    public static (bool CanPlay, string? DisabledReason) DecidePlaySelected(
        IReadOnlyList<(int Scene, int ClipCount, IReadOnlyList<int> MissingServerVideo, bool CompositeExists)> selectedScenes,
        Func<int, int, bool>? hasLocalVideo = null)
    {
        if (selectedScenes is null || selectedScenes.Count == 0)
            return (false, "Select one or more scenes first");

        foreach (var s in selectedScenes)
        {
            var decided = DecideScenePlay(
                s.Scene,
                s.ClipCount,
                s.MissingServerVideo,
                hasLocalVideo is null ? null : cn => hasLocalVideo(s.Scene, cn),
                s.CompositeExists);
            if (!decided.CanPlay)
                return decided;
        }

        return (true, null);
    }
}
