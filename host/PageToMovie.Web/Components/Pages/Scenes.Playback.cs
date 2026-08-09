using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: Playback — partial methods/properties for the Scenes page
public partial class Scenes
{

    internal async Task LoadClipVideoAndTakesCountAsync(int scene, int clip)
    {
        if (MediaFolder.IsConnected)
        {
            try
            {
                var relPath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
                var expectedSize = await ResolveExpectedClipSizeAsync(scene, clip);

                var localBlob = expectedSize is long exp
                    ? await MediaFolder.GetCurrentBlobUrlAsync(_projectId, relPath, exp)
                    : await MediaFolder.GetLocalBlobUrlAsync(_projectId, relPath);
                if (!string.IsNullOrWhiteSpace(localBlob))
                {
                    _clipVideoUrl = localBlob;
                }
            }
            catch { /* fallback to server URL */ }
            finally
            {
                _clipVideoLoading = false;
                StateHasChanged();
            }
        }

        // Proactive, lightweight fetch so the "Takes (N)" button shows a real count without
        // requiring a click first. OpenClipCompareAsync re-fetches the authoritative list (plus
        // trash) when the modal actually opens — this is just for the label.
        try
        {
            var res = await Engine.GetClipVersionsAsync(_projectId, scene, clip);
            _clipVersions = res?.Versions;
            StateHasChanged();
        }
        catch { /* label falls back to "1" */ }
    }


    internal async Task PlaySceneCompositeAsync(int sn)
    {
        // _busy flips true synchronously, before the first await — see PlaySceneAsync's comment
        // in Review.razor for why (a fast second click otherwise slips past this guard and races
        // the first over shared local blob caches).
        if (_busy || _clientStitching) return;
        _busy = true;
        try
        {
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            var summary = _scenes?.FirstOrDefault(s => s.SceneNumber == sn);
            var compositeOk = summary?.CompositeExists == true
                              || (_detail is { SceneNumber: var dsn, CompositeExists: true } && dsn == sn);
            var clipsOnDisk = summary?.ClipsOnDisk
                              ?? (_detail is { SceneNumber: var d2 } && d2 == sn ? _detail.ClipsOnDisk : 0);
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
                _message = $"Playing S{sn:D2} composite";
                return;
            }

            if (clipsOnDisk <= 0 && !compositeOk)
            {
                if (MediaFolder.IsSyncing)
                {
                    _showScenePlayer = true;
                    _playingScene = sn;
                    _clientSceneUrl = null;
                    _message = $"Downloading video clips for S{sn:D2} to local folder…";
                }
                else
                {
                    _error = $"No clips for S{sn:D2} — connect local media folder or generate clips first";
                }
                return;
            }

            // Missing or stale composite: stitch clips (or fall back to stale composite) in the browser.
            _clientStitching = true;
            _error = null;
            _message = null;
            _clientStitchStatus = "Collecting clips…";
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
            _playingScene = sn;
            _showScenePlayer = true;
            _clientSceneUrl = null;
            try
            {
                SceneDetail? detail = _detail is { SceneNumber: var d } && d == sn
                    ? _detail
                    : null;
                var urls = await Stitch.CollectClipUrlsAsync(_projectId, sn, detail);
                if (urls.Count == 0 && compositeOk)
                {
                    // Stale composite still playable
                    _clientSceneUrl = null;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _message = $"Playing S{sn:D2} composite (may be stale)";
                    return;
                }

                if (urls.Count == 0)
                {
                    _error = $"No on-disk clips for S{sn:D2}";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                _clientStitchStatus = urls.Count == 1 ? "Loading…" : $"Combining {urls.Count} clips…";
                await Stitch.RevokePreviewUrlAsync();
                var result = await Stitch.ConcatAsync(urls);
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    _error = result.Error ?? "Browser stitch failed";
                    _showScenePlayer = false;
                    _playingScene = null;
                    return;
                }

                // Layer locally-synced background music (if any) under the stitched video —
                // client-side replacement for the old server-side ffmpeg mix; no-op if none synced.
                _clientSceneUrl = await Stitch.MixSceneMusicAsync(_projectId, result.Url, sn);
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inlineCompositeKey = _sceneVideoKey;
                _message = urls.Count == 1
                    ? $"Playing S{sn:D2} (single clip)"
                    : $"Playing S{sn:D2} — {urls.Count} clips stitched in browser";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
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
            _busy = false;
        }
    }


    internal async Task HideScenePlayer()
    {
        _showScenePlayer = false;
        _playingScene = null;
        if (!string.IsNullOrEmpty(_clientSceneUrl))
        {
            _clientSceneUrl = null;
            await Stitch.RevokePreviewUrlAsync();
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
            _scenePlayerServerSrcCached = CacheBust(Engine.CompositeVideoUrl(_projectId, sn));
        }
        return _scenePlayerServerSrcCached;
    }


    internal async Task HidePreviewPlayerAsync()
    {
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        await Stitch.RevokePreviewUrlAsync();
    }


    /// <summary>True when selection has at least one scene to play (or local media folder connected).</summary>
    internal bool CanPlaySelected
    {
        get
        {
            if (_selected.Count == 0 || _scenes is null)
                return false;
            if (MediaFolder.IsConnected || MediaFolder.IsSyncing)
                return true;
            return _scenes.Any(s =>
                _selected.Contains(s.SceneNumber)
                && (s.CompositeExists || s.ClipsOnDisk > 0));
        }
    }


    /// <summary>Stitch the current selection in the browser (composites preferred, else clips).</summary>
    internal async Task PlaySelectedAsync()
    {
        if (!CanPlaySelected || _busy || _clientStitching)
            return;

        _busy = true;
        _clientStitching = true;
        _error = null;
        _message = null;
        _clientStitchStatus = "Preparing…";
        _previewScenes = _selected.OrderBy(x => x).ToList();
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
            await Stitch.RevokePreviewUrlAsync();
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
            _clientStitchStatus = "Collecting media…";
            var urls = await Stitch.CollectAndMixSceneSegmentsAsync(
                _projectId, _previewScenes, _scenes, stale);
            if (urls.Count == 0)
            {
                _error = "No composites or on-disk clips for the selected scenes";
                _showPreviewPlayer = false;
                return;
            }

            _clientStitchStatus = urls.Count == 1
                ? "Loading…"
                : $"Combining {urls.Count} clip/scene file(s)…";
            var result = await Stitch.ConcatAsync(urls);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
            {
                _error = result.Error ?? "Browser stitch failed";
                _showPreviewPlayer = false;
                return;
            }

            _clientPreviewUrl = result.Url;
            _previewVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _message = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _showPreviewPlayer = false;
            _clientPreviewUrl = null;
        }
        finally
        {
            _busy = false;
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }


    internal string? CompareVideoUrl(ClipVersionItem v) => _compareVideoUrls.GetValueOrDefault(v.VersionId);


    /// <summary>
    /// Resolves a playable URL for every take in _clipVersions, once, instead of computing it
    /// inline per-render (both the grid and split-view markup need this, and a take flagged
    /// ClientOnly has no server bytes to stream — it has to go through the local media folder
    /// instead of the server URL the "normal" server-backed case uses).
    /// </summary>
    internal async Task RefreshCompareVideoUrlsAsync()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_clipVersions is { Count: > 0 })
        {
            foreach (var v in _clipVersions)
            {
                map[v.VersionId] = v.ClientOnly && !string.IsNullOrEmpty(v.RelativePath)
                    ? await MediaFolder.GetLocalBlobUrlAsync(_projectId, v.RelativePath)
                    : v.IsCurrent
                        ? Engine.ClipVideoUrl(_projectId, _compareSceneNumber, _compareClipNumber)
                        : Engine.BrowserMediaPath($"/api/projects/{Uri.EscapeDataString(_projectId)}/assets/video/history/{v.Mp4FileName}");
            }
        }
        _compareVideoUrls = map;
        StateHasChanged();
    }

}
