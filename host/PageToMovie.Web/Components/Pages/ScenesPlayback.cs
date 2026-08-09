using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    /// <summary>Playback domain for the Scenes page. Owns related UI state and behavior.</summary>
    public sealed class ScenesPlayback
    {

    private readonly Scenes S;
    public ScenesPlayback(Scenes host) => S = host;


    internal int? _playSceneAfterRemux;


    internal bool _showScenePlayer;


    internal int? _playingScene;


    internal long _sceneVideoKey;


    internal long _inlineCompositeKey;


    internal long _clipVideoKey;


    /// <summary>"Play selected" — multi-scene (possibly non-contiguous) client-stitched preview.</summary>
    internal bool _showPreviewPlayer;


    internal long _previewVideoKey;


    internal List<int> _previewScenes = new();


    internal string? _clientPreviewUrl;


    internal string? _clientSceneUrl;


    internal bool _clientStitching;


    internal string? _clientStitchStatus;



    internal string? _clipVideoUrl;


    internal string? _clipServerVideoUrl;


    internal bool _clipVideoLoading;


    internal string? _sceneCompositeVideoUrl;


    internal string? _sceneCompositeServerUrl;



    internal int? _scenePlayerServerSrcScene;


    internal string? _scenePlayerServerSrcCached;



    internal Dictionary<string, string?> _compareVideoUrls = new(StringComparer.OrdinalIgnoreCase);



    internal async Task LoadClipVideoAndTakesCountAsync(int scene, int clip)
    {
        if (S.MediaFolder.IsConnected)
        {
            try
            {
                var relPath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
                var expectedSize = await S.ResolveExpectedClipSizeAsync(scene, clip);

                var localBlob = expectedSize is long exp
                    ? await S.MediaFolder.GetCurrentBlobUrlAsync(S._projectId, relPath, exp)
                    : await S.MediaFolder.GetLocalBlobUrlAsync(S._projectId, relPath);
                if (!string.IsNullOrWhiteSpace(localBlob))
                {
                    _clipVideoUrl = localBlob;
                }
            }
            catch { /* fallback to server URL */ }
            finally
            {
                _clipVideoLoading = false;
                S.StateHasChanged();
            }
        }

        // Proactive, lightweight fetch so the "Takes (N)" button shows a real count without
        // requiring a click first. OpenClipCompareAsync re-fetches the authoritative list (plus
        // trash) when the modal actually opens — this is just for the label.
        try
        {
            var res = await S.Engine.GetClipVersionsAsync(S._projectId, scene, clip);
            S.ClipVer._clipVersions = res?.Versions;
            S.StateHasChanged();
        }
        catch { /* label falls back to "1" */ }
    }



    internal async Task PlaySceneCompositeAsync(int sn)
    {
        // S._busy flips true synchronously, before the first await — see PlaySceneAsync's comment
        // in Review.razor for why (a fast second click otherwise slips past this guard and races
        // the first over shared local blob caches).
        if (S._busy || _clientStitching) return;
        S._busy = true;
        try
        {
            var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
            var summary = S.List._scenes?.FirstOrDefault(s => s.SceneNumber == sn);
            var compositeOk = summary?.CompositeExists == true
                              || (S.List._detail is { SceneNumber: var dsn, CompositeExists: true } && dsn == sn);
            var clipsOnDisk = summary?.ClipsOnDisk
                              ?? (S.List._detail is { SceneNumber: var d2 } && d2 == sn ? S.List._detail.ClipsOnDisk : 0);
            var stale = meta?.StaleScenes?.Contains(sn) == true;
            var needsStitch = !compositeOk || stale;

            // Fresh composite on disk — stream it directly (no stitch).
            if (!needsStitch && compositeOk)
            {
                _clientSceneUrl = null;
                _playingScene = sn;
                _showScenePlayer = true;
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inlineCompositeKey = _sceneVideoKey;
                S._message = $"Playing S{sn:D2} composite";
                return;
            }

            if (clipsOnDisk <= 0 && !compositeOk)
            {
                if (S.MediaFolder.IsSyncing)
                {
                    _showScenePlayer = true;
                    _playingScene = sn;
                    _clientSceneUrl = null;
                    S._message = $"Downloading video clips for S{sn:D2} to local folder…";
                }
                else
                {
                    S._error = $"No clips for S{sn:D2} — connect local media folder or generate clips first";
                }
                return;
            }

            // Missing or stale composite: stitch clips (or fall back to stale composite) in the browser.
            _clientStitching = true;
            S._error = null;
            S._message = null;
            _clientStitchStatus = "Collecting clips…";
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
            _playingScene = sn;
            _showScenePlayer = true;
            _clientSceneUrl = null;
            try
            {
                SceneDetail? detail = S.List._detail is { SceneNumber: var d } && d == sn
                    ? S.List._detail
                    : null;
                var urls = await S.Stitch.CollectClipUrlsAsync(S._projectId, sn, detail);
                if (urls.Count == 0 && compositeOk)
                {
                    // Stale composite still playable
                    _clientSceneUrl = null;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = $"Playing S{sn:D2} composite (may be stale)";
                    return;
                }

                if (urls.Count == 0)
                {
                    S._error = $"No on-disk clips for S{sn:D2}";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                _clientStitchStatus = urls.Count == 1 ? "Loading…" : $"Combining {urls.Count} clips…";
                await S.Stitch.RevokePreviewUrlAsync();
                var result = await S.Stitch.ConcatAsync(urls);
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    S._error = result.Error ?? "Browser stitch failed";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                // Layer locally-synced background music (if any) under the stitched video —
                // client-side replacement for the old server-side ffmpeg mix; no-op if none synced.
                _clientSceneUrl = await S.Stitch.MixSceneMusicAsync(S._projectId, result.Url, sn);
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inlineCompositeKey = _sceneVideoKey;
                S._message = urls.Count == 1
                    ? $"Playing S{sn:D2} (single clip)"
                    : $"Playing S{sn:D2} — {urls.Count} clips stitched in browser";
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _showScenePlayer = false;
                _playingScene = null;
                _clientSceneUrl = null;
            }
            finally
            {
                _clientStitching = false;
                _clientStitchStatus = null;
            }
        }
        finally
        {
            S._busy = false;
        }
    }



    internal async Task HideScenePlayer()
    {
        _showScenePlayer = false;
        _playingScene = null;
        if (!string.IsNullOrEmpty(_clientSceneUrl))
        {
            _clientSceneUrl = null;
            await S.Stitch.RevokePreviewUrlAsync();
        }
    }



    /// <summary>
    /// Cache-busted server URL is memoized per scene number rather than recomputed on every call
    /// — CacheBust() stamps the current second, so recomputing on every render (any SignalR/
    /// job-poll re-render elsewhere on the page) gives the &lt;video&gt; a new src each time,
    /// which makes the browser reload the resource and restart playback — looks like looping.
    /// </summary>
    internal string? ScenePlayerSrc(int sn)
    {
        if (!string.IsNullOrEmpty(_clientSceneUrl) && _playingScene == sn)
            return _clientSceneUrl;

        if (_scenePlayerServerSrcScene != sn)
        {
            _scenePlayerServerSrcScene = sn;
            _scenePlayerServerSrcCached = Scenes.CacheBust(S.Engine.CompositeVideoUrl(S._projectId, sn));
        }
        return _scenePlayerServerSrcCached;
    }



    internal async Task HidePreviewPlayerAsync()
    {
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        await S.Stitch.RevokePreviewUrlAsync();
    }



    /// <summary>True when selection has at least one scene to play (or local media folder connected).</summary>
    internal bool CanPlaySelected
    {
        get
        {
            if (S.List._selected.Count == 0 || S.List._scenes is null)
                return false;
            if (S.MediaFolder.IsConnected || S.MediaFolder.IsSyncing)
                return true;
            return S.List._scenes.Any(s =>
                S.List._selected.Contains(s.SceneNumber)
                && (s.CompositeExists || s.ClipsOnDisk > 0));
        }
    }



    /// <summary>Stitch the current selection in the browser (composites preferred, else clips).</summary>
    internal async Task PlaySelectedAsync()
    {
        if (!CanPlaySelected || S._busy || _clientStitching)
            return;

        S._busy = true;
        _clientStitching = true;
        S._error = null;
        S._message = null;
        _clientStitchStatus = "Preparing…";
        _previewScenes = S.List._selected.OrderBy(x => x).ToList();
        _showScenePlayer = false;
        _playingScene = null;
        _clientSceneUrl = null;
        _showPreviewPlayer = true;
        _clientPreviewUrl = null;
        try
        {
            // Revoke the OLD preview before collecting new segments — see the comment in
            // Review.razor's EnsureShareableMovieUrlAsync for why revoking after collection can
            // destroy a blob the segments list still needs.
            await S.Stitch.RevokePreviewUrlAsync();
            var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
            var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
            _clientStitchStatus = "Collecting media…";
            var urls = await S.Stitch.CollectAndMixSceneSegmentsAsync(
                S._projectId, _previewScenes, S.List._scenes, stale);
            if (urls.Count == 0)
            {
                S._error = "No composites or on-disk clips for the selected scenes";
                _showPreviewPlayer = false;
                return;
            }

            _clientStitchStatus = urls.Count == 1
                ? "Loading…"
                : $"Combining {urls.Count} clip/scene file(s)…";
            var result = await S.Stitch.ConcatAsync(urls);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
            {
                S._error = result.Error ?? "Browser stitch failed";
                _showPreviewPlayer = false;
                return;
            }

            _clientPreviewUrl = result.Url;
            _previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = null;
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
        }
        finally
        {
            S._busy = false;
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }



    internal string? CompareVideoUrl(ClipVersionItem v) => _compareVideoUrls.GetValueOrDefault(v.VersionId);



    /// <summary>
    /// Resolves a playable URL for every take in S.ClipVer._clipVersions, once, instead of computing it
    /// inline per-render (both the grid and split-view markup need this, and a take flagged
    /// ClientOnly has no server bytes to stream — it has to go through the local media folder
    /// instead of the server URL the "normal" server-backed case uses).
    /// </summary>
    internal async Task RefreshCompareVideoUrlsAsync()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (S.ClipVer._clipVersions is { Count: > 0 })
        {
            foreach (var v in S.ClipVer._clipVersions)
            {
                map[v.VersionId] = v.ClientOnly && !string.IsNullOrEmpty(v.RelativePath)
                    ? await S.MediaFolder.GetLocalBlobUrlAsync(S._projectId, v.RelativePath)
                    : v.IsCurrent
                        ? S.Engine.ClipVideoUrl(S._projectId, S.ClipVer._compareSceneNumber, S.ClipVer._compareClipNumber)
                        : S.Engine.BrowserMediaPath($"/api/projects/{Uri.EscapeDataString(S._projectId)}/assets/video/history/{v.Mp4FileName}");
            }
        }
        _compareVideoUrls = map;
        S.StateHasChanged();
    }


    }
}
