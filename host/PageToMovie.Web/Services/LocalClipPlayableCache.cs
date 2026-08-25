using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Shared cache of locally confirmed current takes for Review and Film clip Play.
/// One implementation — pages must not copy the refresh loop.
/// </summary>
public sealed class LocalClipPlayableCache
{
    /// <summary>
    /// How long a "not local yet" answer is trusted before another folder stat. Without it,
    /// a project whose clips are not in the folder re-stats every clip on every Changed event,
    /// and Changed fires from ~30 places. Short enough that a clip saved from a job still
    /// lights up its Play button within a beat.
    /// </summary>
    internal static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(3);

    private readonly Dictionary<(int Scene, int Clip), (bool Ready, DateTime CheckedUtc)> _ready = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Has(int scene, int clip) =>
        _ready.TryGetValue((scene, clip), out var e) && e.Ready;

    public async Task RefreshAsync(
        ClientMediaFolderService media,
        string? projectId,
        IReadOnlyList<SceneSummary>? scenes,
        SceneDetail? detail)
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync(media, projectId, scenes, detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static HashSet<(int Scene, int Clip)> CollectNeeded(
        IReadOnlyList<SceneSummary>? scenes,
        SceneDetail? detail)
    {
        var needed = new HashSet<(int Scene, int Clip)>();
        if (scenes is { Count: > 0 })
        {
            foreach (var s in scenes)
            {
                foreach (var cn in s.ClipsMissingServerVideo)
                    needed.Add((s.SceneNumber, cn));
            }
        }

        if (detail?.Clips is { Count: > 0 } clips)
        {
            var sn = detail.SceneNumber;
            foreach (var c in clips.Where(c => !ScenePlayGate.HasServerVideo(c.SizeBytes)))
                needed.Add((sn, c.ClipNumber));
        }

        return needed;
    }

    private async Task RefreshCoreAsync(
        ClientMediaFolderService media,
        string? projectId,
        IReadOnlyList<SceneSummary>? scenes,
        SceneDetail? detail)
    {
        // A folder sync raises Changed for every saved file. Wait for its final event, then
        // update only missing/negative entries rather than clearing and re-statting every clip.
        if (media.IsSyncing)
            return;
        if (!media.IsConnected || string.IsNullOrWhiteSpace(projectId))
        {
            _ready.Clear();
            return;
        }

        var needed = CollectNeeded(scenes, detail);
        foreach (var stale in _ready.Keys.Where(k => !needed.Contains(k)).ToList())
            _ready.Remove(stale);

        // Materialize before the loop: the predicate reads _ready, which the loop body writes.
        var now = DateTime.UtcNow;
        foreach (var key in needed.Where(k => NeedsStat(k, now)).ToList())
        {
            var ready = await media.HasCurrentTakeFileAsync(projectId, key.Scene, key.Clip);
            _ready[key] = (ready, DateTime.UtcNow);
        }
    }

    /// <summary>Unknown, or a negative answer that has aged past <see cref="NegativeTtl"/>.
    /// A confirmed local file is never re-statted.</summary>
    private bool NeedsStat((int Scene, int Clip) key, DateTime now)
    {
        if (!_ready.TryGetValue(key, out var e))
            return true;
        return !e.Ready && now - e.CheckedUtc >= NegativeTtl;
    }
}
