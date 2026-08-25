using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Shared cache of locally confirmed current takes for Review and Film clip Play.
/// One implementation — pages must not copy the refresh loop.
/// </summary>
public sealed class LocalClipPlayableCache
{
    private readonly Dictionary<(int Scene, int Clip), bool> _ready = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Has(int scene, int clip) =>
        _ready.TryGetValue((scene, clip), out var ok) && ok;

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

        foreach (var (scene, clip) in needed.Where(k => !_ready.TryGetValue(k, out var ready) || !ready))
            _ready[(scene, clip)] = await media.HasCurrentTakeFileAsync(projectId, scene, clip);
    }
}
