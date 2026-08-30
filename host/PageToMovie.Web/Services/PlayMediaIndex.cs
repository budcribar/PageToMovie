using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// In-memory Film Review Play index: scene JSON, per-scene clip lists, resolved
/// clip URLs, and already-mixed scene segments. Fingerprinted by current-take
/// identity so a promote or regen drops that scene (or clip), not the whole movie.
/// </summary>
/// <remarks>
/// This is the walk Play used to redo on every click (GetSceneDetail + clip
/// index + URL resolve). A warm index lets Concat start without that walk.
/// </remarks>
public sealed class PlayMediaIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Project, int Scene), SceneEntry> _scenes = new();
    private readonly Dictionary<(string Project, int Scene, int Clip), ClipUrlEntry> _clipUrls = new();
    private readonly Dictionary<(string Project, int Scene), SegmentEntry> _segments = new();
    private SceneGroupEntry? _group;

    public int SceneCount
    {
        get { lock (_gate) return _scenes.Count; }
    }

    public void SyncSceneList(string projectId, IReadOnlyList<SceneSummary>? scenes)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return;

        lock (_gate)
        {
            foreach (var key in _scenes.Keys.Where(k => !string.Equals(k.Project, projectId, StringComparison.Ordinal)).ToList())
                RemoveSceneLocked(key.Project, key.Scene);

            var live = (scenes ?? Array.Empty<SceneSummary>()).Select(s => s.SceneNumber).ToHashSet();
            foreach (var key in _scenes.Keys.Where(k =>
                         string.Equals(k.Project, projectId, StringComparison.Ordinal) && !live.Contains(k.Scene)).ToList())
                RemoveSceneLocked(key.Project, key.Scene);

            if (scenes is { Count: > 0 })
            {
                foreach (var summary in scenes)
                {
                    var fp = FingerprintSummary(summary);
                    if (_scenes.TryGetValue((projectId, summary.SceneNumber), out var entry)
                        && !string.Equals(entry.SummaryFingerprint, fp, StringComparison.Ordinal))
                    {
                        RemoveSceneLocked(projectId, summary.SceneNumber);
                    }
                }
            }

            if (_group is { } g
                && (!string.Equals(g.Project, projectId, StringComparison.Ordinal)
                    || !string.Equals(g.Fingerprint, FingerprintSceneList(scenes), StringComparison.Ordinal)))
            {
                _group = null;
            }
        }
    }

    public void RememberSceneDetail(string projectId, SceneDetail detail, SceneSummary? summary = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || detail.SceneNumber <= 0)
            return;

        var summaryFp = summary is not null
            ? FingerprintSummary(summary)
            : FingerprintSummaryFromDetail(detail);
        var detailFp = FingerprintDetail(detail);
        var clipNumbers = detail.Clips?
            .OrderBy(c => c.ClipNumber)
            .Select(c => c.ClipNumber)
            .ToList();

        lock (_gate)
        {
            if (_scenes.TryGetValue((projectId, detail.SceneNumber), out var prior))
            {
                if (!string.Equals(prior.DetailFingerprint, detailFp, StringComparison.Ordinal))
                {
                    RemoveClipUrlsLocked(projectId, detail.SceneNumber);
                    _segments.Remove((projectId, detail.SceneNumber));
                }
                else if (summary is null && !string.IsNullOrEmpty(prior.SummaryFingerprint))
                {
                    // Caller had no scene-list row (clip Play). Keep the warm
                    // summary fingerprint so the next full-movie Play still hits.
                    summaryFp = prior.SummaryFingerprint;
                }
            }

            _scenes[(projectId, detail.SceneNumber)] = new SceneEntry
            {
                SummaryFingerprint = summaryFp,
                DetailFingerprint = detailFp,
                Detail = detail,
                ClipNumbers = clipNumbers,
            };
        }
    }

    public bool TryGetSceneDetail(
        string projectId,
        int scene,
        string? summaryFingerprint,
        out SceneDetail? detail)
    {
        lock (_gate)
        {
            if (!_scenes.TryGetValue((projectId, scene), out var entry))
            {
                detail = null;
                return false;
            }

            if (!string.IsNullOrEmpty(summaryFingerprint)
                && !string.Equals(entry.SummaryFingerprint, summaryFingerprint, StringComparison.Ordinal))
            {
                detail = null;
                return false;
            }

            detail = entry.Detail;
            return true;
        }
    }

    public bool TryGetSceneClipIndex(
        string projectId,
        int scene,
        string? summaryFingerprint,
        out IReadOnlyList<int> clipNumbers)
    {
        lock (_gate)
        {
            if (!_scenes.TryGetValue((projectId, scene), out var entry)
                || entry.ClipNumbers is not { Count: > 0 })
            {
                clipNumbers = Array.Empty<int>();
                return false;
            }

            if (!string.IsNullOrEmpty(summaryFingerprint)
                && !string.Equals(entry.SummaryFingerprint, summaryFingerprint, StringComparison.Ordinal))
            {
                clipNumbers = Array.Empty<int>();
                return false;
            }

            clipNumbers = entry.ClipNumbers;
            return true;
        }
    }

    public void RememberSceneGroup(
        string projectId,
        IReadOnlyList<SceneSummary>? scenes,
        IReadOnlyList<int> playableSceneNumbers)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return;

        lock (_gate)
        {
            _group = new SceneGroupEntry(
                projectId,
                FingerprintSceneList(scenes),
                playableSceneNumbers.ToList());
        }
    }

    public bool TryGetSceneGroup(
        string projectId,
        IReadOnlyList<SceneSummary>? scenes,
        out IReadOnlyList<int> playableSceneNumbers)
    {
        lock (_gate)
        {
            if (_group is { } g
                && string.Equals(g.Project, projectId, StringComparison.Ordinal)
                && string.Equals(g.Fingerprint, FingerprintSceneList(scenes), StringComparison.Ordinal))
            {
                playableSceneNumbers = g.Scenes;
                return true;
            }

            playableSceneNumbers = Array.Empty<int>();
            return false;
        }
    }

    public void RememberClipUrl(
        string projectId,
        int scene,
        int clip,
        string takeFingerprint,
        string url)
    {
        if (string.IsNullOrWhiteSpace(projectId) || scene <= 0 || clip <= 0
            || string.IsNullOrWhiteSpace(takeFingerprint) || string.IsNullOrWhiteSpace(url))
            return;

        lock (_gate)
            _clipUrls[(projectId, scene, clip)] = new ClipUrlEntry(takeFingerprint, url);
    }

    public bool TryGetClipUrl(
        string projectId,
        int scene,
        int clip,
        string takeFingerprint,
        out string? url)
    {
        lock (_gate)
        {
            if (_clipUrls.TryGetValue((projectId, scene, clip), out var entry)
                && string.Equals(entry.TakeFingerprint, takeFingerprint, StringComparison.Ordinal))
            {
                url = entry.Url;
                return true;
            }

            url = null;
            return false;
        }
    }

    public void RememberSceneSegment(
        string projectId,
        int scene,
        string fingerprint,
        ClientWipSegment segment)
    {
        if (string.IsNullOrWhiteSpace(projectId) || scene <= 0
            || string.IsNullOrWhiteSpace(fingerprint)
            || string.IsNullOrWhiteSpace(segment.Url))
            return;

        lock (_gate)
            _segments[(projectId, scene)] = new SegmentEntry(fingerprint, segment);
    }

    public bool TryGetSceneSegment(
        string projectId,
        int scene,
        string fingerprint,
        out ClientWipSegment? segment)
    {
        lock (_gate)
        {
            if (_segments.TryGetValue((projectId, scene), out var entry)
                && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                segment = entry.Segment;
                return true;
            }

            segment = null;
            return false;
        }
    }

    public void InvalidateScene(string projectId, int scene)
    {
        if (string.IsNullOrWhiteSpace(projectId) || scene <= 0)
            return;
        lock (_gate)
            RemoveSceneLocked(projectId, scene);
    }

    public void InvalidateClip(string projectId, int scene, int clip)
    {
        if (string.IsNullOrWhiteSpace(projectId) || scene <= 0 || clip <= 0)
            return;

        lock (_gate)
        {
            _clipUrls.Remove((projectId, scene, clip));
            _segments.Remove((projectId, scene));
        }
    }

    public static string FingerprintSummary(SceneSummary? summary)
    {
        if (summary is null)
            return "";
        var missing = summary.ClipsMissingServerVideo is { Count: > 0 } list
            ? string.Join(',', list.OrderBy(n => n))
            : "";
        return string.Join('|',
            summary.SceneNumber,
            summary.ClipCount,
            summary.ClipsOnDisk,
            summary.CompositeExists ? 1 : 0,
            missing,
            summary.StaleClipCount,
            summary.HasStaleClips ? 1 : 0,
            summary.HasBackgroundMusic ? 1 : 0,
            summary.Status ?? "");
    }

    public static string FingerprintSummaryFromDetail(SceneDetail detail)
    {
        var missing = detail.Clips?
            .Where(c => !ScenePlayGate.HasServerVideo(c.SizeBytes))
            .Select(c => c.ClipNumber)
            .OrderBy(n => n)
            .ToList() ?? new List<int>();
        return string.Join('|',
            detail.SceneNumber,
            detail.ClipCount > 0 ? detail.ClipCount : detail.Clips?.Count ?? 0,
            detail.ClipsOnDisk,
            detail.CompositeExists ? 1 : 0,
            string.Join(',', missing),
            0,
            0,
            detail.HasBackgroundMusic ? 1 : 0,
            "");
    }

    public static string FingerprintDetail(SceneDetail? detail)
    {
        if (detail?.Clips is not { Count: > 0 } clips)
        {
            return string.Join('|',
                detail?.SceneNumber ?? 0,
                detail?.ClipCount ?? 0,
                detail?.CompositeExists == true ? 1 : 0,
                detail?.HasBackgroundMusic == true ? 1 : 0);
        }

        var parts = clips
            .OrderBy(c => c.ClipNumber)
            .Select(c => FingerprintClip(c, currentTakeRel: null));
        return string.Join(';', parts);
    }

    public static string FingerprintClip(ClipSummary clip, string? currentTakeRel)
    {
        var takeName = !string.IsNullOrWhiteSpace(currentTakeRel)
            ? currentTakeRel
            : clip.FileName ?? "";
        var take = ClipTakeNaming.ParseTakeNumber(takeName);
        return string.Join('|',
            clip.ClipNumber,
            takeName,
            take,
            clip.SizeBytes,
            clip.OnDisk ? 1 : 0,
            clip.ProviderLeadInSeconds ?? 0);
    }

    public static string FingerprintSceneList(IReadOnlyList<SceneSummary>? scenes)
    {
        if (scenes is not { Count: > 0 })
            return "";
        return string.Join(';', scenes
            .OrderBy(s => s.SceneNumber)
            .Select(FingerprintSummary));
    }

    public static string FingerprintSegment(
        SceneSummary? summary,
        SceneDetail? detail,
        int urlCount)
    {
        var body = detail is not null ? FingerprintDetail(detail) : FingerprintSummary(summary);
        var music = summary?.HasBackgroundMusic == true || detail?.HasBackgroundMusic == true;
        return $"{body}|n{urlCount}|m{(music ? 1 : 0)}";
    }

    private void RemoveSceneLocked(string projectId, int scene)
    {
        _scenes.Remove((projectId, scene));
        RemoveClipUrlsLocked(projectId, scene);
        _segments.Remove((projectId, scene));
        if (_group is { } g && string.Equals(g.Project, projectId, StringComparison.Ordinal))
            _group = null;
    }

    private void RemoveClipUrlsLocked(string projectId, int scene)
    {
        foreach (var key in _clipUrls.Keys.Where(k =>
                     string.Equals(k.Project, projectId, StringComparison.Ordinal) && k.Scene == scene).ToList())
            _clipUrls.Remove(key);
    }

    private sealed class SceneEntry
    {
        public required string SummaryFingerprint { get; init; }
        public required string DetailFingerprint { get; init; }
        public required SceneDetail Detail { get; init; }
        public IReadOnlyList<int>? ClipNumbers { get; init; }
    }

    private sealed record ClipUrlEntry(string TakeFingerprint, string Url);

    private sealed record SegmentEntry(string Fingerprint, ClientWipSegment Segment);

    private sealed record SceneGroupEntry(string Project, string Fingerprint, IReadOnlyList<int> Scenes);
}

/// <summary>Hit/miss counters for one Play collect. Reset at the start of each collect.</summary>
public sealed class PlayMediaCollectStats
{
    private int _detailHits;
    private int _detailMisses;
    private int _groupHits;
    private int _groupMisses;
    private int _clipUrlHits;
    private int _clipUrlMisses;
    private int _segmentHits;
    private int _segmentMisses;

    public int SceneDetailHits => _detailHits;
    public int SceneDetailMisses => _detailMisses;
    public int SceneGroupHits => _groupHits;
    public int SceneGroupMisses => _groupMisses;
    public int ClipUrlHits => _clipUrlHits;
    public int ClipUrlMisses => _clipUrlMisses;
    public int SegmentHits => _segmentHits;
    public int SegmentMisses => _segmentMisses;

    public void Reset()
    {
        Interlocked.Exchange(ref _detailHits, 0);
        Interlocked.Exchange(ref _detailMisses, 0);
        Interlocked.Exchange(ref _groupHits, 0);
        Interlocked.Exchange(ref _groupMisses, 0);
        Interlocked.Exchange(ref _clipUrlHits, 0);
        Interlocked.Exchange(ref _clipUrlMisses, 0);
        Interlocked.Exchange(ref _segmentHits, 0);
        Interlocked.Exchange(ref _segmentMisses, 0);
    }

    public void AddDetailHit() => Interlocked.Increment(ref _detailHits);
    public void AddDetailMiss() => Interlocked.Increment(ref _detailMisses);
    public void AddGroupHit() => Interlocked.Increment(ref _groupHits);
    public void AddGroupMiss() => Interlocked.Increment(ref _groupMisses);
    public void AddClipUrlHit() => Interlocked.Increment(ref _clipUrlHits);
    public void AddClipUrlMiss() => Interlocked.Increment(ref _clipUrlMisses);
    public void AddSegmentHit() => Interlocked.Increment(ref _segmentHits);
    public void AddSegmentMiss() => Interlocked.Increment(ref _segmentMisses);
}
