using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Utils;
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
    internal int? _playingClip;


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

    /// <summary>Local MP4 confirmed via media-folder stat (not just a connected folder).</summary>
    internal readonly Dictionary<(int Scene, int Clip), bool> _localVideoReady = new();



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
                var expectedSize = await S.ClipRegen.ResolveExpectedClipSizeAsync(scene, clip);

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



    internal bool HasCachedLocalVideo(int scene, int clip) =>
        _localVideoReady.TryGetValue((scene, clip), out var ok) && ok;

    internal (bool CanPlay, string? Reason) DecideScenePlay(int sn)
    {
        if (sn <= 0)
            return (false, "Open a scene first");

        var detail = S.List._detail;
        if (detail is { SceneNumber: var dsn, Clips: { Count: > 0 } } && dsn == sn)
        {
            var missing = detail.Clips
                .Where(c => !ScenePlayGate.HasServerVideo(c.SizeBytes))
                .Select(c => c.ClipNumber)
                .ToList();
            return ScenePlayGate.DecideScenePlay(
                sn, detail.ClipCount, missing, cn => HasCachedLocalVideo(sn, cn), detail.CompositeExists);
        }

        var summary = S.List._scenes?.FirstOrDefault(s => s.SceneNumber == sn);
        if (summary is null)
            return (false, $"S{sn:D2} has no clips yet");
        return ScenePlayGate.DecideScenePlay(
            sn,
            summary.ClipCount,
            summary.ClipsMissingServerVideo,
            cn => HasCachedLocalVideo(sn, cn),
            summary.CompositeExists);
    }

    internal (bool CanPlay, string? Reason) DecideOpenScenePlay()
    {
        var sn = S.List._detail?.SceneNumber ?? S.List._selectedScene ?? 0;
        return DecideScenePlay(sn);
    }

    internal bool CanPlayOpenScene => DecideOpenScenePlay().CanPlay;

    internal string OpenScenePlayTitle
    {
        get
        {
            var decided = DecideOpenScenePlay();
            return decided.CanPlay
                ? "Play the checked clips (all when none are checked)"
                : decided.Reason ?? "Scene is not ready to play";
        }
    }

    internal async Task RefreshLocalPlayableAsync()
    {
        _localVideoReady.Clear();
        if (!S.MediaFolder.IsConnected || string.IsNullOrWhiteSpace(S._projectId))
            return;

        var needed = new HashSet<(int Scene, int Clip)>();
        if (S.List._scenes is { Count: > 0 })
        {
            foreach (var s in S.List._scenes)
            {
                foreach (var cn in s.ClipsMissingServerVideo)
                    needed.Add((s.SceneNumber, cn));
            }
        }
        if (S.List._detail?.Clips is { Count: > 0 } clips)
        {
            var sn = S.List._detail.SceneNumber;
            foreach (var c in clips.Where(c => !ScenePlayGate.HasServerVideo(c.SizeBytes)))
                needed.Add((sn, c.ClipNumber));
        }

        foreach (var (scene, clip) in needed)
        {
            var rel = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
            var (found, size) = await S.MediaFolder.StatLocalFileAsync(S._projectId, rel);
            _localVideoReady[(scene, clip)] = found && size >= ScenePlayGate.MinPlayableVideoBytes;
        }
    }

    internal async Task PlaySceneCompositeAsync(int sn)
    {
        // S._busy flips true synchronously, before the first await — see PlaySceneAsync's comment
        // in Review.razor for why (a fast second click otherwise slips past this guard and races
        // the first over shared local blob caches).
        if (S._busy || _clientStitching) return;
        var gate = DecideScenePlay(sn);
        if (!gate.CanPlay)
        {
            S._error = gate.Reason;
            return;
        }
        S._busy = true;
        try
        {
            var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
            var (compositeOk, clipsOnDisk) = ResolveSceneMediaAvailability(sn);
            var stale = meta?.StaleScenes?.Contains(sn) ?? false;
            var needsStitch = !compositeOk || stale;

            // Fresh composite on disk — stream it directly (no stitch).
            if (!needsStitch)
            {
                ShowFreshComposite(sn);
                return;
            }

            if (clipsOnDisk <= 0 && !compositeOk)
            {
                HandleNoClipsForScene(sn);
                return;
            }

            await StitchSceneClipsAsync(sn, compositeOk);
        }
        finally
        {
            S._busy = false;
        }
    }

    private (bool CompositeOk, int ClipsOnDisk) ResolveSceneMediaAvailability(int sn)
    {
        var summary = S.List._scenes?.FirstOrDefault(s => s.SceneNumber == sn);
        var compositeOk = summary?.CompositeExists == true
                          || (S.List._detail is { SceneNumber: var dsn, CompositeExists: true } && dsn == sn);
        var clipsOnDisk = summary?.ClipsOnDisk
                          ?? (S.List._detail is { SceneNumber: var d2 } && d2 == sn ? S.List._detail.ClipsOnDisk : 0);
        return (compositeOk, clipsOnDisk);
    }

    private void ShowFreshComposite(int sn)
    {
        _clientSceneUrl = null;
        _playingScene = sn;
        _playingClip = null;
        _showScenePlayer = true;
        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _inlineCompositeKey = _sceneVideoKey;
        S._message = $"Playing S{sn:D2} composite";
    }

    private void HandleNoClipsForScene(int sn)
    {
        if (S.MediaFolder.IsSyncing)
        {
            _showScenePlayer = true;
            _playingScene = sn;
            _playingClip = null;
            _clientSceneUrl = null;
            S._message = $"Downloading video clips for S{sn:D2} to local folder…";
        }
        else
        {
            S._error = $"No clips for S{sn:D2} — connect local media folder or generate clips first";
        }
    }

    private async Task StitchSceneClipsAsync(int sn, bool compositeOk)
    {
        // Missing or stale composite: stitch clips (or fall back to stale composite) in the browser.
        _clientStitching = true;
        S._error = null;
        S._message = null;
        _clientStitchStatus = "Collecting clips…";
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        _playingScene = sn;
        _playingClip = null;
        _showScenePlayer = true;
        _clientSceneUrl = null;
        try
        {
            SceneDetail? detail = S.List._detail is { SceneNumber: var d } && d == sn
                ? S.List._detail
                : null;
            var urls = await S.Stitch.CollectClipUrlsAsync(
                S._projectId, sn, detail, requireAllPlannedClips: true);
            if (S.Stitch.LastSkippedClipLabels.Count > 0)
            {
                FailScenePlayer(ScenePlayGate.FormatPlayFailedError("clips", S.Stitch.LastSkippedClipLabels));
                return;
            }
            if (urls.Count == 0 && compositeOk)
            {
                // Stale composite still playable
                _clientSceneUrl = null;
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                S._message = $"Playing S{sn:D2} composite (may be stale)";
                return;
            }

            var stitched = await ConcatSceneClipsAsync(urls, $"No on-disk clips for S{sn:D2}");
            if (stitched is null)
                return;

            // Layer locally-synced background music (if any) under the stitched video —
            // client-side replacement for the old server-side ffmpeg mix; no-op if none synced.
            _clientSceneUrl = await S.Stitch.MixSceneMusicAsync(S._projectId, stitched, sn);
            _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _inlineCompositeKey = _sceneVideoKey;
            S._message = urls.Count == 1
                ? $"Playing S{sn:D2} (single clip)"
                : $"Playing S{sn:D2} — {urls.Count} clips stitched in browser";
        }
        catch (Exception ex)
        {
            FailScenePlayer(ex.Message);
        }
        finally
        {
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }

    internal async Task PlaySingleClipAsync(int sn, int cn)
    {
        if (S._busy || _clientStitching) return;
        _playingScene = sn;
        _playingClip = cn;
        _showScenePlayer = true;
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        _clientSceneUrl = null;

        if (S.MediaFolder.IsConnected)
        {
            try
            {
                var relPath = $"assets/video/scene_{sn:D2}_clip_{cn:D2}.mp4";
                var expectedSize = await S.ClipRegen.ResolveExpectedClipSizeAsync(sn, cn);
                var localBlob = expectedSize is long exp
                    ? await S.MediaFolder.GetCurrentBlobUrlAsync(S._projectId, relPath, exp)
                    : await S.MediaFolder.GetLocalBlobUrlAsync(S._projectId, relPath);
                if (!string.IsNullOrWhiteSpace(localBlob))
                {
                    _clientSceneUrl = localBlob;
                }
            }
            catch { /* fallback to server URL */ }
        }

        if (string.IsNullOrEmpty(_clientSceneUrl))
        {
            // No local file: the server/provider copy of a video-extend clip is the combined video —
            // ResolveServerClipUrlAsync slices the previous clip's head off before it plays.
            var row = S.List._detail?.Clips.FirstOrDefault(c => c.ClipNumber == cn);
            _clientSceneUrl = row is { ProviderLeadInSeconds: > 0.1 }
                ? await S.Stitch.ResolveServerClipUrlAsync(S._projectId, sn, row)
                : Scenes.CacheBust(S.Engine.ClipVideoUrl(S._projectId, sn, cn));
        }

        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _inlineCompositeKey = _sceneVideoKey;
        S._message = $"Playing S{sn:D2} · C{cn:D2}";
        S.StateHasChanged();
    }

    internal async Task PlaySelectedClipsInSceneAsync(int sn)
    {
        if (S._busy || _clientStitching) return;
        var selectedClipNums = S.ClipSel._selectedClips.OrderBy(x => x).ToList();
        if (selectedClipNums.Count == 0)
        {
            await PlaySceneCompositeAsync(sn);
            return;
        }

        if (selectedClipNums.Count == 1)
        {
            await PlaySingleClipAsync(sn, selectedClipNums[0]);
            return;
        }

        var gate = DecideScenePlay(sn);
        if (!gate.CanPlay)
        {
            S._error = gate.Reason;
            return;
        }

        S._busy = true;
        _clientStitching = true;
        S._error = null;
        S._message = null;
        _clientStitchStatus = $"Collecting {selectedClipNums.Count} clips…";
        _showPreviewPlayer = false;
        _clientPreviewUrl = null;
        _playingScene = sn;
        _playingClip = null;
        _showScenePlayer = true;
        _clientSceneUrl = null;
        try
        {
            SceneDetail? detail = S.List._detail is { SceneNumber: var d } && d == sn
                ? S.List._detail
                : null;
            var urls = await S.Stitch.CollectClipUrlsAsync(S._projectId, sn, detail, clipNumbers: selectedClipNums);
            if (S.Stitch.LastSkippedClipLabels.Count > 0)
            {
                FailScenePlayer(ScenePlayGate.FormatPlayFailedError("clips", S.Stitch.LastSkippedClipLabels));
                return;
            }
            var stitched = await ConcatSceneClipsAsync(urls, $"No on-disk video for selected clips in S{sn:D2}");
            if (stitched is null)
                return;

            _clientSceneUrl = stitched;
            _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _inlineCompositeKey = _sceneVideoKey;
            S._message = $"Playing {urls.Count} selected clips from S{sn:D2}";
        }
        catch (Exception ex)
        {
            FailScenePlayer(ex.Message);
        }
        finally
        {
            S._busy = false;
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }

    private Task<string?> ConcatSceneClipsAsync(IReadOnlyList<string> urls, string emptyError) =>
        S.Stitch.TryConcatSceneClipsAsync(
            urls, emptyError, status => _clientStitchStatus = status, FailScenePlayer);

    private void FailScenePlayer(string error)
    {
        S._error = error;
        _showScenePlayer = false;
        _playingScene = null;
        _clientSceneUrl = null;
    }

    internal async Task HideScenePlayer()
    {
        _showScenePlayer = false;
        _playingScene = null;
        _playingClip = null;
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



    /// <summary>True when every selected scene has every planned clip actually playable.</summary>
    internal bool CanPlaySelected => DecidePlaySelected().CanPlay;

    internal string PlaySelectedDisabledReason =>
        DecidePlaySelected().Reason ?? "Select one or more scenes first";

    internal (bool CanPlay, string? Reason) DecidePlaySelected()
    {
        if (S.List._selected.Count == 0 || S.List._scenes is null)
            return (false, "Select one or more scenes first");

        var selected = S.List._scenes
            .Where(s => S.List._selected.Contains(s.SceneNumber))
            .Select(s => (
                s.SceneNumber,
                s.ClipCount,
                (IReadOnlyList<int>)(s.ClipsMissingServerVideo ?? new List<int>()),
                s.CompositeExists))
            .ToList();
        return ScenePlayGate.DecidePlaySelected(selected, HasCachedLocalVideo);
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
            if (S.Stitch.LastSkippedClipLabels.Count > 0)
            {
                S._error = ScenePlayGate.FormatPlayFailedError("scenes", S.Stitch.LastSkippedClipLabels);
                _showPreviewPlayer = false;
                return;
            }
            if (urls.Count == 0)
            {
                S._error = FormatEmptySelectedCollectError(S.Stitch.LastCollectError);
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

    private static string FormatEmptySelectedCollectError(string? lastCollectError)
    {
        if (lastCollectError is not { Length: > 0 })
            return "No composites or on-disk clips for the selected scenes";

        if (lastCollectError.Contains("S", StringComparison.Ordinal)
            && lastCollectError.Contains(" C", StringComparison.Ordinal))
            return $"Could not play the selected scenes — {lastCollectError}";

        return ScenePlayGate.FormatPlayFailedError("scenes", new[] { lastCollectError });
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
                map[v.VersionId] = await ResolveCompareVideoUrlAsync(v);
        }
        _compareVideoUrls = map;
        S.StateHasChanged();
    }

    private async Task<string?> ResolveCompareVideoUrlAsync(ClipVersionItem v)
    {
        var url = await TryGetLocalCompareBlobUrlAsync(CompareRelativePath(v));
        return string.IsNullOrEmpty(url) ? await ResolveCompareFallbackUrlAsync(v) : url;
    }

    private string CompareRelativePath(ClipVersionItem v) =>
        !string.IsNullOrEmpty(v.RelativePath)
            ? v.RelativePath
            : $"assets/video/scene_{S.ClipVer._compareSceneNumber:D2}_clip_{S.ClipVer._compareClipNumber:D2}.mp4";

    private async Task<string?> TryGetLocalCompareBlobUrlAsync(string relPath)
    {
        if (!S.MediaFolder.IsConnected)
            return null;
        try
        {
            var local = await S.MediaFolder.GetLocalBlobUrlAsync(S._projectId, relPath);
            if (!string.IsNullOrEmpty(local))
                return local;
        }
        catch { /* fallback to server URL */ }
        return null;
    }

    private async Task<string?> ResolveCompareFallbackUrlAsync(ClipVersionItem v)
    {
        if (v.IsCurrent)
            return await ResolveCurrentTakeCompareUrlAsync();
        if (!string.IsNullOrEmpty(v.ProviderPlaybackUrl))
            return await ResolveProviderTakeCompareUrlAsync(v);
        if (!string.IsNullOrEmpty(v.Mp4FileName))
            return S.Engine.BrowserMediaPath($"/api/projects/{Uri.EscapeDataString(S._projectId)}/assets/video/history/{v.Mp4FileName}");
        return null;
    }

    private async Task<string?> ResolveCurrentTakeCompareUrlAsync()
    {
        // No local copy: the server/provider copy of a video-extend clip is the combined
        // video — slice the previous clip's head off, same as playback does.
        var row = S.List._detail?.Clips.FirstOrDefault(c => c.ClipNumber == S.ClipVer._compareClipNumber);
        return row is { ProviderLeadInSeconds: > 0.1 }
            ? await S.Stitch.ResolveServerClipUrlAsync(S._projectId, S.ClipVer._compareSceneNumber, row)
            : S.Engine.ClipVideoUrl(S._projectId, S.ClipVer._compareSceneNumber, S.ClipVer._compareClipNumber);
    }

    private async Task<string?> ResolveProviderTakeCompareUrlAsync(ClipVersionItem v)
    {
        // Take recorded only by its sidecar: the server issued a proxy URL for the provider
        // copy; an extend take's copy is combined — slice the previous clip's head off.
        var url = v.ProviderPlaybackUrl;
        if (v.ProviderLeadInSeconds <= 0.1)
            return url;
        try
        {
            var probe = await S.Stitch.ProbeDurationAsync(url);
            if (probe is { } total && total > v.ProviderLeadInSeconds + 0.1)
            {
                var sliced = await S.Stitch.TrimTailAsync(url, total - v.ProviderLeadInSeconds);
                if (!string.IsNullOrWhiteSpace(sliced))
                    return sliced;
            }
        }
        catch { /* play combined rather than nothing */ }
        return url;
    }


    }
}
