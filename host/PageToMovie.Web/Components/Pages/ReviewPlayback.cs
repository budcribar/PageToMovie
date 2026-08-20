using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Components.Pages;

public partial class Review
{
    /// <summary>Playback domain for the Review page. Owns related UI state and behavior.</summary>
    public sealed class ReviewPlayback
    {
        private readonly Review S;
        public ReviewPlayback(Review host) => S = host;

        internal string? _clientSceneUrl;

        internal string? _clientStitchStatus;

        internal bool _clientStitching;

        internal string? _clientWipUrl;

        internal string? _clipServerSrcCached;

        internal long _clipVideoKey;

        internal string? _dubStatus;

        internal bool _dubbing;

        internal int? _playSceneAfterRemux;

        internal bool _playWipAfterRemux;

        internal int? _playingClipNum;

        internal int? _playingClipScene;

        internal int? _playingScene;

        internal string _preferredVideoEditor = "ClipChamp";

        internal string? _sceneServerSrcCached;

        internal int? _sceneServerSrcScene;

        internal long _sceneVideoKey;

        internal bool _showClipPlayer;

        internal bool _showScenePlayer;

        internal bool _showWipPlayer;

        internal long _wipBytes;

        internal bool _wipCanBuild;

        internal bool _wipExists;

        internal string? _wipPath;

        internal string? _wipReason;

        internal string? _wipServerSrcCached;

        // CacheBust() stamps the current second, so calling it inline in markup re-evaluates on
            // every render (any SignalR/job-poll re-render elsewhere on the page) and gives the <video>
            // a new src each time, which makes the browser reload the resource and restart playback —
            // looks like looping. Memoized per key below instead of recomputed per call.
            internal string? _wipServerSrcForProject;

        internal bool _wipStale;

        internal string? _wipUpdatedAt;

        internal long _wipVideoKey;


        internal bool CanPlayMovie =>
            _wipExists || _wipCanBuild || S.MediaFolder.IsConnected || S.MediaFolder.IsSyncing || S.List._scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);


        /// <summary>
        /// True only once real video actually exists — a browser or server cut, a scene composite, or clips
        /// on disk. Unlike <see cref="CanPlayMovie"/> this does NOT count a merely-connected media folder, so
        /// clip-dependent actions (Play, Share, Open in editor, AI review) stay disabled until clips exist.
        /// </summary>
        internal bool HasGeneratedClips =>
            _wipExists
            || !string.IsNullOrEmpty(_clientWipUrl)
            || S.List._scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);


        internal string WipPlayTitle
        {
            get
            {
                if (!_wipCanBuild && !_wipExists && string.IsNullOrEmpty(_clientWipUrl)
                    && !S.MediaFolder.IsConnected && !S.MediaFolder.IsSyncing)
                    return "No scene videos were found";
                if (_wipStale || !_wipExists)
                    return "Play full movie (combine scenes in browser)";
                return "Play full movie (up to date)";
            }
        }


        internal async Task LoadPreferredVideoEditorAsync()
        {
            try
            {
                var dto = await S.Engine.GetConfigAsync(S._projectId);
                if (dto?.Config is { } cfg &&
                    cfg.TryGetValue("preferred_video_editor", out var edEl) &&
                    edEl.ValueKind == JsonValueKind.String &&
                    edEl.GetString() is { Length: > 0 } pve)
                {
                    _preferredVideoEditor = pve.Trim();
                }
            }
            catch { /* keep default */ }
        }


        /// <summary>Dub the whole movie in the user's cloned voice (narrator by default) and download it.
        /// Server synthesizes the cloned voice per line; the browser overlays + stitches + downloads.</summary>
        internal async Task DubInMyVoiceAsync()
        {
            if (string.IsNullOrWhiteSpace(S._projectId)) return;
            // The clips + synthesized audio live in the browser media folder — it must be connected.
            if (!S.MediaFolder.IsConnected)
            {
                var connected = await S.MediaFolder.ConnectFolderAsync();
                if (!connected && !S.MediaFolder.IsConnected)
                {
                    S._message = "Connect your media folder first so your movie can be built in your voice.";
                    return;
                }
            }
            _dubbing = true;
            S._busy = true;
            S._error = null;
            S._message = null;
            _dubStatus = "Starting…";
            try
            {
                var res = await S.VoiceSub.DubMovieInMyVoiceAsync(
                    S._projectId,
                    charKey: null, // narrator by default (server default)
                    onProgress: s => { _dubStatus = s; _ = S.InvokeAsync(S.StateHasChanged); });
                if (res.Ok && !string.IsNullOrWhiteSpace(res.DownloadUrl))
                {
                    await S.VoiceSub.DownloadAsync(res.DownloadUrl, "movie-in-my-voice.mp4");
                    S._message = $"Your movie is ready — {res.ClipsDubbed} clip(s) in your voice"
                               + (res.ClipsFailed > 0 ? $" ({res.ClipsFailed} skipped)" : "") + ". Download started.";
                }
                else
                {
                    S._error = res.Error ?? "Could not make your version.";
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                _dubbing = false;
                S._busy = false;
                _dubStatus = null;
            }
        }


        internal async Task OpenInExternalEditorAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.OpenInExternalEditorAsync(S._projectId, sceneNumber: null, clipNumber: null, _preferredVideoEditor);

                bool isClipchamp = string.Equals(_preferredVideoEditor, "ClipChamp", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(_preferredVideoEditor, "Clipchamp", StringComparison.OrdinalIgnoreCase);

                if (isClipchamp)
                {
                    try
                    {
                        await S.JS.InvokeVoidAsync("eval", "try { window.location.href = 'ms-clipchamp:'; } catch(_) {}");
                    }
                    catch { /* best-effort client protocol trigger */ }
                }

                if (res.Ok)
                {
                    S._message = $"🎬 Opened full cut in {res.Editor ?? "Clipchamp"}.";
                }
                else
                {
                    S._message = "Preparing movie for download…";
                    S.StateHasChanged();
                    var movieUrl = await S.Share.EnsureShareableMovieUrlAsync();
                    if (!string.IsNullOrEmpty(movieUrl))
                    {
                        var cleanPid = CommonRegex.Replace(S._projectId, @"[^\w\.-]", "_");
                        var fileName = $"{cleanPid}_full.mp4";
                        S._message = $"🎬 Downloaded movie to your PC — opening in {res.Editor ?? _preferredVideoEditor}." +
                            (S.Share._lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
                        await S.JS.InvokeVoidAsync("eval", $"const a=document.createElement('a');a.href='{movieUrl}';a.download='{fileName}';document.body.appendChild(a);a.click();document.body.removeChild(a);");
                    }
                    else
                    {
                        S._error = res.Error ?? "Could not prepare full movie. Ensure at least one scene clip exists.";
                    }
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }


        internal async Task RefreshWipMetaAsync()
        {
            try
            {
                var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
                _wipExists = meta?.Exists ?? false;
                _wipStale = (meta?.Stale ?? false) || !_wipExists;
                _wipCanBuild = meta?.CanBuild ?? false;
                _wipReason = meta?.Reason;
                _wipPath = meta?.Path;
                _wipUpdatedAt = meta?.UpdatedAt;
                _wipBytes = meta?.Bytes ?? 0;
                if (!_wipExists)
                    _showWipPlayer = false;
            }
            catch
            {
                _wipExists = false;
                _wipStale = true;
                _wipCanBuild = false;
            }
        }


        /// <summary>
        /// Play full cut: stream on-disk WIP when current; otherwise stitch composites/clips in the browser.
        /// </summary>
        internal async Task PlayWipAsync()
        {
            // S._busy flips true synchronously, before the first await — see PlaySceneAsync's comment
            // for why (a fast second click otherwise slips past this guard and races the first over
            // shared local blob caches).
            if (S._busy || _clientStitching) return;
            S._busy = true;
            try
            {
                S.List._activeTab = ReviewTab.Play;
                _showWipPlayer = true;
                await RefreshWipMetaAsync();

                if (HasFreshClientWip())
                {
                    S._message = "Playing WIP";
                    return;
                }

                await RefreshWipMetaAsync();
                if (HasFreshServerWip())
                {
                    ShowServerWip();
                    return;
                }

                var sceneNums = CollectPlayableSceneNumbers();
                if (sceneNums.Count == 0)
                {
                    S._error = MissingWipScenesError();
                    return;
                }

                await StitchWipInBrowserAsync(sceneNums);
            }
            finally
            {
                S._busy = false;
            }
        }

        private bool HasFreshClientWip() =>
            !string.IsNullOrEmpty(_clientWipUrl) && !_wipStale;

        private bool HasFreshServerWip() =>
            _wipExists && !_wipStale;

        private void ShowServerWip()
        {
            _clientWipUrl = null;
            _showWipPlayer = true;
            _showScenePlayer = false;
            _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = "Playing WIP (up to date)";
        }

        private List<int> CollectPlayableSceneNumbers() =>
            S.List._scenes
                .Where(s => s.CompositeExists || s.ClipsOnDisk > 0 || S.MediaFolder.IsConnected || S.MediaFolder.IsSyncing)
                .OrderBy(s => s.SceneNumber)
                .Select(s => s.SceneNumber)
                .ToList();

        private string MissingWipScenesError() =>
            S.MediaFolder.IsConnected
                ? "No scene videos were found in your local media folder or on the server."
                : "Connect your local media folder to rebuild this movie from your synced clips.";

        private async Task StitchWipInBrowserAsync(List<int> sceneNums)
        {
            _clientStitching = true;
            S._error = null;
            _clientStitchStatus = "Collecting scenes…";
            _showClipPlayer = false;
            _showScenePlayer = false;
            _clientSceneUrl = null;
            _showWipPlayer = true;
            _clientWipUrl = null;
            try
            {
                // Revoke the OLD preview before collecting new segments — see the comment in
                // EnsureShareableMovieUrlAsync for why revoking after collection can destroy a
                // blob the segments list still needs.
                await S.Stitch.RevokePreviewUrlAsync();
                var meta = await S.Engine.GetWipMovieMetaAsync(S._projectId);
                var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
                var segs = await S.Stitch.CollectAndMixSceneSegmentInfosAsync(S._projectId, sceneNums, S.List._scenes, stale);
                if (segs.Count == 0)
                {
                    S._error = "No scene videos were found";
                    _showWipPlayer = false;
                    return;
                }

                _clientStitchStatus = segs.Count == 1
                    ? "Loading…"
                    : $"Combining {segs.Count} file(s)…";
                var result = await S.Stitch.ConcatAsync(segs.Select(s => s.Url).ToList());
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    S._error = result.Error ?? "Browser stitch failed";
                    _showWipPlayer = false;
                    return;
                }

                _clientWipUrl = result.Url;
                _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                S._message = $"Preview ready — {segs.Count} scene(s)";
                await TryRegisterFilmBuildAsync(segs, result);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _showWipPlayer = false;
                _clientWipUrl = null;
            }
            finally
            {
                _clientStitching = false;
                _clientStitchStatus = null;
            }
        }

        private async Task TryRegisterFilmBuildAsync(IReadOnlyList<ClientWipSegment> segs, ClientStitchResult result)
        {
            // film_build.v1: full segment EDL + studio.sha256 (non-fatal)
            try
            {
                _clientStitchStatus = "Saving cut timeline…";
                var reg = await S.Stitch.RegisterFilmBuildAfterWipStitchAsync(S._projectId, segs, result);
                if (reg.Ok && !string.IsNullOrWhiteSpace(reg.FilmId))
                    S._message = $"Preview ready — {segs.Count} scene(s) · film {reg.FilmId}";
            }
            catch
            {
                /* provenance must not block playback */
            }
        }


        /// <summary>Connect a local media folder from the WIP player's "needs rebuild" prompt, then
        /// immediately retry the rebuild — a single click instead of connect-then-hunt-for-play-again.</summary>
        internal async Task ConnectFolderForWipAsync()
        {
            await S.MediaFolder.EnsureHubHookAsync();
            var connected = await S.MediaFolder.ConnectFolderAsync();
            if (connected)
                await PlayWipAsync();
        }


        internal async Task PlaySceneAsync(int scene)
        {
            // S._busy must flip true synchronously, before the first await — otherwise a fast second
            // click (e.g. double-click) can slip past this guard during the window between it and
            // wherever the flag used to get set further down, running a second concurrent stitch that
            // races the first over the same locally-cached clip blob URLs (one call's blob gets
            // revoked-and-replaced out from under the other's in-flight ffmpeg fetch — "Failed to
            // fetch"). Same fix applied to every other Play*/stitch entry point in this file and
            // Scenes.razor.
            if (S._busy || _clientStitching) return;
            S._busy = true;
            try
            {
                _showClipPlayer = false;
                await RefreshWipMetaAsync();
                var summary = S.List._scenes.FirstOrDefault(s => s.SceneNumber == scene);
                var stale = (await S.Engine.GetWipMovieMetaAsync(S._projectId))?.StaleScenes?.Contains(scene) ?? false;
                var compositeOk = summary?.CompositeExists == true;
                var needsStitch = !compositeOk || stale;

                if (!needsStitch)
                {
                    _clientSceneUrl = null;
                    _showWipPlayer = false;
                    _playingScene = scene;
                    _showScenePlayer = true;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = $"Playing S{scene:D2} composite";
                    return;
                }

                var clipsOnDisk = summary?.ClipsOnDisk ?? 0;
                if (clipsOnDisk <= 0 && !compositeOk)
                {
                    S._error = $"No clips for S{scene:D2} — generate clips first";
                    return;
                }

                _clientStitching = true;
                S._error = null;
                _clientStitchStatus = "Collecting clips…";
                _showWipPlayer = false;
                _clientWipUrl = null;
                _playingScene = scene;
                _showScenePlayer = true;
                _clientSceneUrl = null;
                try
                {
                    var urls = await S.Stitch.CollectClipUrlsAsync(S._projectId, scene);
                    if (urls.Count == 0 && compositeOk)
                    {
                        _clientSceneUrl = null;
                        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        S._message = $"Playing S{scene:D2} composite (may be stale)";
                        return;
                    }

                    var stitched = await S.Stitch.TryConcatSceneClipsAsync(
                        urls,
                        $"No on-disk clips for S{scene:D2}",
                        status => _clientStitchStatus = status,
                        FailScenePlayer);
                    if (stitched is null)
                        return;

                    _clientSceneUrl = stitched;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    S._message = urls.Count == 1
                        ? $"Playing S{scene:D2} (single clip)"
                        : $"Playing S{scene:D2} — {urls.Count} clips stitched in browser";
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
            finally
            {
                S._busy = false;
            }
        }


        private void FailScenePlayer(string error)
        {
            S._error = error;
            _showScenePlayer = false;
            _playingScene = null;
            _clientSceneUrl = null;
        }

        internal async Task HideScenePlayerAsync()
        {
            _showScenePlayer = false;
            _playingScene = null;
            if (!string.IsNullOrEmpty(_clientSceneUrl))
            {
                _clientSceneUrl = null;
                await S.Stitch.RevokePreviewUrlAsync();
            }
        }


        internal async Task HideWipPlayerAsync()
        {
            _showWipPlayer = false;
            if (!string.IsNullOrEmpty(_clientWipUrl))
            {
                _clientWipUrl = null;
                await S.Stitch.RevokePreviewUrlAsync();
            }
        }


        internal void PlayClip(int scene, int clip)
        {
            _playingClipScene = scene;
            _playingClipNum = clip;
            _showClipPlayer = true;
            _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S._message = $"Playing S{scene:D2}C{clip:D2}";
        }


        internal void HideClipPlayer()
        {
            _showClipPlayer = false;
            _playingClipScene = null;
            _playingClipNum = null;
        }


        internal static string CacheBust(string url) => KeyFormatting.CacheBust(url);


        internal string? WipServerSrc()
        {
            if (_wipServerSrcForProject != S._projectId)
            {
                _wipServerSrcForProject = S._projectId;
                _wipServerSrcCached = CacheBust(S.Engine.WipMovieUrl(S._projectId));
            }
            return _wipServerSrcCached;
        }


        internal string? SceneServerSrc(int sn)
        {
            if (_sceneServerSrcScene != sn)
            {
                _sceneServerSrcScene = sn;
                _sceneServerSrcCached = CacheBust(S.Engine.CompositeVideoUrl(S._projectId, sn));
            }
            return _sceneServerSrcCached;
        }


        internal string? ClipServerSrc(int scene, int clip)
        {
            if (S._clipServerSrcKey != (scene, clip))
            {
                S._clipServerSrcKey = (scene, clip);
                _clipServerSrcCached = CacheBust(S.Engine.ClipVideoUrl(S._projectId, scene, clip));
            }
            return _clipServerSrcCached;
        }

    }
}
