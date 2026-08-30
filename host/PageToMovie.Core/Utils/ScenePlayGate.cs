namespace PageToMovie.Core.Utils;

/// <summary>One-pass index for repeated clip-presence checks against a video directory listing.</summary>
public sealed class SceneMediaPresenceIndex
{
    private readonly HashSet<(int Scene, int Clip)> _present = new();
    private readonly HashSet<(int Scene, int Clip)> _serverMp4 = new();

    public SceneMediaPresenceIndex(IReadOnlyDictionary<string, long> files)
    {
        foreach (var (name, size) in files)
        {
            if (!TryParse(name, out var key))
                continue;
            var isMp4 = name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var isClipSidecar = name.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);
            var isVideoMarker = name.EndsWith(".mp4.client.json", StringComparison.OrdinalIgnoreCase);
            var isCurrentTakePointer = name.EndsWith(ClipTakeNaming.CurrentTakePointerSuffix, StringComparison.OrdinalIgnoreCase);
            if (isMp4 || isClipSidecar || isVideoMarker || isCurrentTakePointer)
                _present.Add(key);
            if (isMp4 && size >= ScenePlayGate.MinPlayableVideoBytes)
                _serverMp4.Add(key);
        }
    }

    public bool IsPresent(int scene, int clip) => _present.Contains((scene, clip));
    public bool HasServerMp4(int scene, int clip) => _serverMp4.Contains((scene, clip));

    private static bool TryParse(string name, out (int Scene, int Clip) key)
    {
        key = default;
        var stem = Path.GetFileName(name);
        if (!stem.StartsWith("scene_", StringComparison.OrdinalIgnoreCase))
            return false;
        var clipMarker = stem.IndexOf("_clip_", StringComparison.OrdinalIgnoreCase);
        if (clipMarker <= 6
            || !int.TryParse(stem.AsSpan(6, clipMarker - 6), out var scene))
            return false;
        var clipStart = clipMarker + 6;
        var clipEnd = stem.IndexOfAny(new[] { '_', '.' }, clipStart);
        if (clipEnd < 0)
            clipEnd = stem.Length;
        if (!int.TryParse(stem.AsSpan(clipStart, clipEnd - clipStart), out var clip))
            return false;
        key = (scene, clip);
        return scene > 0 && clip > 0;
    }
}

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

        // Takes only. A bare scene_SS_clip_CC.mp4 in the index is a leftover, not this clip's video.
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
        var presence = new SceneMediaPresenceIndex(videoIndex);
        var missing = new List<int>();
        foreach (var cn in plannedClipNumbers.Distinct().OrderBy(x => x))
        {
            if (cn <= 0)
                continue;
            if (!presence.HasServerMp4(scene, cn))
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
    /// <c>SizeBytes</c> 0 / unknown is not a disable — file size is not a
    /// media validator (media-timeline-contract).
    /// </summary>
    public static bool IsClipPlayable(bool hasServerVideo, bool hasLocalVideo = false) =>
        hasServerVideo || hasLocalVideo;

    public static bool IsClipPlayable(long sizeBytes, bool hasLocalVideo = false) =>
        hasLocalVideo
        || sizeBytes <= 0
        || HasServerVideo(sizeBytes);

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
    /// Tooltip while the local media folder is still downloading this project's files.
    /// Prefers the live sync status when the caller already has one.
    /// </summary>
    public static string MediaStillDownloadingReason(
        int syncCurrent = 0,
        int syncTotal = 0,
        string? lastStatus = null)
    {
        if (!string.IsNullOrWhiteSpace(lastStatus))
            return lastStatus.Trim();
        if (syncTotal > 0)
            return $"Media is still downloading ({syncCurrent}/{syncTotal})";
        return "Media is still downloading";
    }

    /// <summary>
    /// Scene Play / Play selected: every planned clip must be playable
    /// (server MP4 or confirmed local file). First hole wins the tooltip.
    /// While media-sync is still running, scene Play stays off so we do not
    /// treat a half-downloaded folder as ready.
    /// </summary>
    public static (bool CanPlay, string? DisabledReason) DecideScenePlay(
        int sceneNumber,
        int clipCount,
        IReadOnlyList<int> missingServerVideoClips,
        Func<int, bool>? hasLocalVideo = null,
        bool compositeExists = false,
        bool mediaSyncing = false,
        string? mediaSyncReason = null)
    {
        if (mediaSyncing)
            return (false, string.IsNullOrWhiteSpace(mediaSyncReason)
                ? MediaStillDownloadingReason()
                : mediaSyncReason);

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
        Func<int, int, bool>? hasLocalVideo = null,
        bool mediaSyncing = false,
        string? mediaSyncReason = null)
    {
        if (mediaSyncing)
            return (false, string.IsNullOrWhiteSpace(mediaSyncReason)
                ? MediaStillDownloadingReason()
                : mediaSyncReason);

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
