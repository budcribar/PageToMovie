using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Review
{
    internal bool _busy;
    internal bool _gateChecked;
    internal string? _error;
    internal string? _message;
    internal string _projectId = "";
    internal List<SceneSummary> _scenes = new();
    internal string _sceneSortBy = "number"; // "number" | "duration"
    internal bool _sceneSortAsc = true;

    internal void ToggleSceneSort(string column)
    {
        if (_sceneSortBy == column)
            _sceneSortAsc = !_sceneSortAsc;
        else
        {
            _sceneSortBy = column;
            _sceneSortAsc = true;
        }
    }

    internal IEnumerable<SceneSummary> SortedReviewScenes
    {
        get
        {
            return _sceneSortBy switch
            {
                "duration" => _sceneSortAsc
                    ? _scenes.OrderBy(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0)
                    : _scenes.OrderByDescending(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0),
                _ => _sceneSortAsc
                    ? _scenes.OrderBy(s => s.SceneNumber)
                    : _scenes.OrderByDescending(s => s.SceneNumber),
            };
        }
    }

    internal static string FormatClock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "—";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    internal string? _activeTab = "review"; // null | "review" | "play" | "share"

    internal async Task ToggleTabAsync(string tab)
    {
        if (_activeTab == tab)
        {
            if (tab == "play")
            {
                await PlayWipAsync();
                return;
            }
            _activeTab = null; // Toggle off / collapse card
        }
        else
        {
            _activeTab = tab;
            if (tab == "play")
            {
                await PlayWipAsync();
            }
            else if (tab == "share")
            {
                PrepopulateDemoFields();
                await RefreshYouTubeStatusAsync();
            }
        }
    }

    internal void PrepopulateDemoFields()
    {
        if (string.IsNullOrWhiteSpace(_demoTitle))
        {
            _demoTitle = FormatDisplayTitle(_projectId);
        }
        if (string.IsNullOrWhiteSpace(_demoDescription))
        {
            _demoDescription = BuildSmartDescription(_projectId, _demoTitle);
        }
    }

    internal static string FormatDisplayTitle(string? rawProjectId)
    {
        if (string.IsNullOrWhiteSpace(rawProjectId))
            return "Untitled Short Film";

        var parts = rawProjectId.Trim().Split('/', '\\');
        var name = parts.Last().Trim();

        if (name.StartsWith("TellTaleHeart", StringComparison.OrdinalIgnoreCase))
            return "The Tell-Tale Heart";

        name = System.Text.RegularExpressions.Regex.Replace(name, @"V\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!name.Contains(' '))
            name = System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

        return name.Trim();
    }

    internal static string BuildSmartDescription(string? rawProjectId, string title)
    {
        var clean = (rawProjectId ?? "").Trim();
        if (clean.Contains("TellTaleHeart", StringComparison.OrdinalIgnoreCase))
        {
            return "A cinematic short film adaptation of Edgar Allan Poe’s classic Gothic horror story “The Tell-Tale Heart”. Produced with PageToMovie AI Film Studio.";
        }
        return $"A cinematic short film adaptation of “{title}”. Produced with PageToMovie AI Film Studio.";
    }

    internal Task SetTabReview() => ToggleTabAsync("review");
    internal Task SetTabShare() => ToggleTabAsync("share");

    internal bool CanPlayMovie =>
        _wipExists || _wipCanBuild || MediaFolder.IsConnected || MediaFolder.IsSyncing || _scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);

    /// <summary>
    /// True only once real video actually exists — a browser or server cut, a scene composite, or clips
    /// on disk. Unlike <see cref="CanPlayMovie"/> this does NOT count a merely-connected media folder, so
    /// clip-dependent actions (Play, Share, Open in editor, AI review) stay disabled until clips exist.
    /// </summary>
    internal bool HasGeneratedClips =>
        _wipExists
        || !string.IsNullOrEmpty(_clientWipUrl)
        || _scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);

    internal SceneDetail? _selectedDetail;
    internal bool _wipExists;
    internal bool _wipStale;
    internal bool _wipCanBuild;
    internal string? _wipReason;
    internal bool _showWipPlayer;
    internal bool _playWipAfterRemux;
    internal int? _playSceneAfterRemux;
    internal string? _wipPath;
    internal string? _wipUpdatedAt;
    internal long _wipBytes;
    internal readonly HashSet<string> _expandedSceneGroups = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsSceneGroupExpanded(string rangeStr) => _expandedSceneGroups.Contains(rangeStr);

    internal void ToggleSceneGroupExpand(string rangeStr)
    {
        if (!_expandedSceneGroups.Add(rangeStr))
            _expandedSceneGroups.Remove(rangeStr);
    }

    internal void ToggleAllSceneGroups(bool expand)
    {
        _expandedSceneGroups.Clear();
        if (expand && _movieReport?.GroupFeedback is { Count: > 0 } groups)
        {
            foreach (var g in groups)
                _expandedSceneGroups.Add(g.SceneRange);
        }
    }
    internal long _wipVideoKey;
    internal YouTubeStatusDto? _youTubeStatus;
    internal YouTubeUploadInfo? _youTubeUpload;
    internal string _youTubeTitle = "";
    internal string _youTubeDescription = "";
    internal string _youTubePrivacy = "unlisted";
    internal string _demoTitle = "";
    internal string _demoDescription = "";
    internal bool _demoAcceptedGuidelines;
    internal bool _demoMadeForKids;
    internal bool _demoIsAiSynthetic = true;
    internal bool _isPublishing;
    internal int _publishProgressPct;
    internal string _publishProgressStatus = "";
    internal DotNetObjectReference<Review>? _dotNetRef;

    [JSInvokable]
    public void ReportPublishProgress(int pct, string status)
    {
        _publishProgressPct = Math.Clamp(pct, 0, 100);
        _publishProgressStatus = status;
        StateHasChanged();
    }

    /// <summary>Can publish when browser stitch or fresh on-disk movie is available, or scenes can be stitched.</summary>
    internal bool CanShareMovie =>
        !string.IsNullOrEmpty(_clientWipUrl)
        || (_wipExists && !_wipStale)
        || MediaFolder.IsConnected
        || MediaFolder.IsSyncing
        || _scenes.Any(s => s.CompositeExists || s.ClipsOnDisk > 0);

    internal string WipPlayTitle =>
        !_wipCanBuild && !_wipExists && string.IsNullOrEmpty(_clientWipUrl) && !MediaFolder.IsConnected && !MediaFolder.IsSyncing
            ? "No scene videos were found"
            : _wipStale || !_wipExists
                ? "Play full movie (combine scenes in browser)"
                : "Play full movie (up to date)";
    internal string? _clientWipUrl;
    internal string? _clientSceneUrl;
    internal bool _clientStitching;
    internal string? _clientStitchStatus;
    internal bool _showScenePlayer;
    internal int? _playingScene;
    internal long _sceneVideoKey;
    internal bool _showClipPlayer;
    internal int? _playingClipScene;
    internal int? _playingClipNum;
    internal long _clipVideoKey;
    internal List<EditLogEntry> _entries = new();
    internal Dictionary<string, string> _reviews = new();
    internal int? _selectedScene;
    internal string _note = "";
    internal JobSnapshot? _job;
    internal bool _showActivity;
    internal const string passStatus = "pass";
    internal const string failStatus = "fail";

    /// <summary>SxxCyy → draft from last auto-review.</summary>
    internal readonly Dictionary<string, ClipAutoReviewDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);
    internal string? _editKey;
    internal List<EditRow>? _editRows;

    internal bool JobRunning =>
        string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_job?.Status, "queued", StringComparison.OrdinalIgnoreCase);

    internal sealed class EditRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Layer { get; set; } = "clip";
        public string Field { get; set; } = "";
        public string? CharKey { get; set; }
        public string Label { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Rationale { get; set; }
        public bool Include { get; set; } = true;
    }

    protected override async Task OnInitializedAsync()
    {
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
            if (!ActiveProject.HasProject)
                await ActiveProject.RefreshFromServerAsync(Engine);
            await ActiveProject.RefreshReadinessAsync(Engine);
            await Caps.RefreshAsync(Engine);
            _projectId = ActiveProject.ProjectId ?? "";
            _gateChecked = true;
            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanReview)
            {
                HandleYouTubeOAuthRedirect();
                return;
            }

            try { await Hub.StartAsync(); } catch { /* optional */ }
            await LoadAsync();

            // Contextual sync: Review plays this project's media, so pull it to the local folder now
            // (replaces the old sync-on-every-page-load behaviour).
            try
            {
                await MediaFolder.EnsureHubHookAsync();
                if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
                MediaFolder.TriggerAutoSyncIfConnected();
            }
            catch { /* media folder optional for browse */ }

            HandleYouTubeOAuthRedirect();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }

    internal void OnJobUpdated(JobSnapshot snap)
    {
        _job = snap;
        if (snap.Status is "done" or "partial" or "error" or "cancelled")
        {
            _ = InvokeAsync(async () =>
            {
                await SoftLoadAsync();
                if (snap.Status == "done" &&
                    string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) &&
                    snap.Scene is int rs && snap.Clip is int rc)
                {
                    try
                    {
                        var d = await Engine.GetClipAutoReviewDraftAsync(_projectId, rs, rc);
                        if (d is not null)
                            _drafts[$"S{rs:D2}C{rc:D2}"] = d;
                        _message = d is null
                            ? $"Review finished S{rs:D2}C{rc:D2}"
                            : $"Review ready S{rs:D2}C{rc:D2}: {d.Suggestion} — Apply suggestions or Pass/Fail";
                        _error = null;
                    }
                    catch (Exception ex)
                    {
                        _error = ex.Message;
                    }
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Refresh per-clip drafts for selected scene after batch
                        if (_selectedScene is int sel)
                        {
                            for (var c = 1; c <= ClipCountFor(sel); c++)
                            {
                                var d = await Engine.GetClipAutoReviewDraftAsync(_projectId, sel, c);
                                if (d is not null)
                                    _drafts[ClipKey(sel, c)] = d;
                            }
                        }
                        _message = snap.Message ?? "Batch auto-review finished";
                        _error = null;
                    }
                    catch (Exception ex)
                    {
                        _error = ex.Message;
                    }
                }
                else if (snap.Status == "error" &&
                         (string.Equals(snap.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(snap.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase)))
                {
                    _error = Session.IsAdmin
                        ? (snap.Error ?? snap.Message ?? "Auto-review failed")
                        : "Auto-review failed. Try again.";
                    _message = null;
                }
                else if (snap.Status == "done" &&
                    string.Equals(snap.Kind, "remux", StringComparison.OrdinalIgnoreCase))
                {
                    await SoftLoadAsync();
                    await RefreshWipMetaAsync();
                    if (_playWipAfterRemux)
                    {
                        _playWipAfterRemux = false;
                        if (_wipExists)
                        {
                            _showWipPlayer = true;
                            _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            _message = _wipStale
                                ? "WIP rebuilt but still marked stale — check clips"
                                : "WIP ready — player below";
                        }
                    }
                    else if (_playSceneAfterRemux is int playSn)
                    {
                        _playSceneAfterRemux = null;
                        _playingScene = playSn;
                        _showScenePlayer = true;
                        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _message = $"Scene S{playSn:D2} ready — playing";
                    }
                    else if (snap.Scene is int sn && sn > 0)
                    {
                        _playingScene = sn;
                        _showScenePlayer = true;
                        _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _message = $"Scene S{sn:D2} composite rebuilt — player below";
                    }
                }
                else if (snap.Status == "done" &&
                         string.Equals(snap.Kind, "scene", StringComparison.OrdinalIgnoreCase) &&
                         snap.Scene is int genSn &&
                         snap.Clip is int genCn)
                {
                    _message = $"Clip S{genSn:D2}C{genCn:D2} gen finished — Play scene when you want the updated composite";
                    if (_selectedScene == genSn)
                        await LoadSelectedDetailAsync(genSn);
                }
                else if (string.Equals(snap.Kind, "youtube_upload", StringComparison.OrdinalIgnoreCase))
                {
                    if (snap.Status == "done")
                    {
                        await RefreshYouTubeStatusAsync();
                        _message = snap.Message ?? "Uploaded to YouTube";
                        _error = null;
                    }
                    else if (snap.Status == "error")
                    {
                        _error = snap.Error ?? snap.Message ?? "YouTube upload failed";
                        _message = null;
                    }
                }
                StateHasChanged();
            });
        }
        else _ = InvokeAsync(StateHasChanged);
    }

    internal void OnJobLog(string line)
    {
        if (_job is not null)
            _job.Message = line;
        _ = InvokeAsync(StateHasChanged);
    }

    internal string _preferredVideoEditor = "ClipChamp";
    internal bool _dubbing;
    internal string? _dubStatus;
    internal bool _isReviewing;
    internal int _reviewProgressPct;
    internal string _reviewProgressStatus = "";

    internal async Task LoadPreferredVideoEditorAsync()
    {
        try
        {
            var dto = await Engine.GetConfigAsync(_projectId);
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
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        // The clips + synthesized audio live in the browser media folder — it must be connected.
        if (!MediaFolder.IsConnected)
        {
            var connected = await MediaFolder.ConnectFolderAsync();
            if (!connected && !MediaFolder.IsConnected)
            {
                _message = "Connect your media folder first so your movie can be built in your voice.";
                return;
            }
        }
        _dubbing = true;
        _busy = true;
        _error = null;
        _message = null;
        _dubStatus = "Starting…";
        try
        {
            var res = await VoiceSub.DubMovieInMyVoiceAsync(
                _projectId,
                charKey: null, // narrator by default (server default)
                onProgress: s => { _dubStatus = s; _ = InvokeAsync(StateHasChanged); });
            if (res.Ok && !string.IsNullOrWhiteSpace(res.DownloadUrl))
            {
                await VoiceSub.DownloadAsync(res.DownloadUrl, "movie-in-my-voice.mp4");
                _message = $"Your movie is ready — {res.ClipsDubbed} clip(s) in your voice"
                           + (res.ClipsFailed > 0 ? $" ({res.ClipsFailed} skipped)" : "") + ". Download started.";
            }
            else
            {
                _error = res.Error ?? "Could not make your version.";
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _dubbing = false;
            _busy = false;
            _dubStatus = null;
        }
    }

    internal async Task OpenInExternalEditorAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.OpenInExternalEditorAsync(_projectId, sceneNumber: null, clipNumber: null, _preferredVideoEditor);
            
            bool isClipchamp = string.Equals(_preferredVideoEditor, "ClipChamp", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(_preferredVideoEditor, "Clipchamp", StringComparison.OrdinalIgnoreCase);

            if (isClipchamp)
            {
                try
                {
                    await JS.InvokeVoidAsync("eval", "try { window.location.href = 'ms-clipchamp:'; } catch(_) {}");
                }
                catch { /* best-effort client protocol trigger */ }
            }

            if (res.Ok)
            {
                _message = $"🎬 Opened full cut in {res.Editor ?? "Clipchamp"}.";
            }
            else
            {
                _message = "Preparing movie for download…";
                StateHasChanged();
                var movieUrl = await EnsureShareableMovieUrlAsync();
                if (!string.IsNullOrEmpty(movieUrl))
                {
                    var cleanPid = System.Text.RegularExpressions.Regex.Replace(_projectId, @"[^\w\.-]", "_");
                    var fileName = $"{cleanPid}_full.mp4";
                    _message = $"🎬 Downloaded movie to your PC — opening in {res.Editor ?? _preferredVideoEditor}." +
                        (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
                    await JS.InvokeVoidAsync("eval", $"const a=document.createElement('a');a.href='{movieUrl}';a.download='{fileName}';document.body.appendChild(a);a.click();document.body.removeChild(a);");
                }
                else
                {
                    _error = res.Error ?? "Could not prepare full movie. Ensure at least one scene clip exists.";
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            await LoadPreferredVideoEditorAsync();
            var scenes = await Engine.GetScenesAsync(_projectId);
            _scenes = scenes?.Scenes ?? new();
            var log = await Engine.GetEditLogAsync(_projectId);
            _entries = log?.EditLog?.Entries ?? new();
            var revs = await Engine.GetClipReviewsAsync(_projectId);
            _reviews = revs?.Reviews ?? new();
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            await RefreshWipMetaAsync();
            await RefreshYouTubeStatusAsync();
            var movieRes = await Engine.GetMovieReviewReportAsync(_projectId);
            _movieReport = movieRes?.Report;
            if (_selectedScene is int sn)
                await LoadSelectedDetailAsync(sn);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task SoftLoadAsync()
    {
        try
        {
            var log = await Engine.GetEditLogAsync(_projectId);
            _entries = log?.EditLog?.Entries ?? new();
            var revs = await Engine.GetClipReviewsAsync(_projectId);
            _reviews = revs?.Reviews ?? new();
            var scenes = await Engine.GetScenesAsync(_projectId);
            _scenes = scenes?.Scenes ?? new();
            await RefreshWipMetaAsync();
            if (_selectedScene is int snSelected)
                await TryLoadDraftsForSceneAsync(snSelected);
            if (_selectedScene is int sn)
                await LoadSelectedDetailAsync(sn);
        }
        catch { /* ignore */ }
    }

    internal async Task TryLoadDraftsForSceneAsync(int scene)
    {
        var n = ClipCountFor(scene);
        for (var c = 1; c <= n; c++)
        {
            try
            {
                var d = await Engine.GetClipAutoReviewDraftAsync(_projectId, scene, c);
                if (d is not null)
                    _drafts[ClipKey(scene, c)] = d;
            }
            catch { /* optional */ }
        }
    }

    internal async Task LoadSelectedDetailAsync(int sn)
    {
        try
        {
            var dto = await Engine.GetSceneDetailAsync(_projectId, sn);
            _selectedDetail = dto?.Scene;
        }
        catch
        {
            _selectedDetail = null;
        }
    }

    internal async Task RefreshWipMetaAsync()
    {
        try
        {
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            _wipExists = meta?.Exists == true;
            _wipStale = meta?.Stale == true || !_wipExists;
            _wipCanBuild = meta?.CanBuild == true;
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

    internal async Task RefreshYouTubeStatusAsync()
    {
        try
        {
            _youTubeStatus = await Engine.GetYouTubeStatusAsync();
            _youTubeUpload = await Engine.GetYouTubeUploadInfoAsync(_projectId);
        }
        catch { /* optional feature — ignore */ }
    }

    /// <summary>
    /// Play full cut: stream on-disk WIP when current; otherwise stitch composites/clips in the browser.
    /// </summary>
    internal async Task PlayWipAsync()
    {
        // _busy flips true synchronously, before the first await — see PlaySceneAsync's comment
        // for why (a fast second click otherwise slips past this guard and races the first over
        // shared local blob caches).
        if (_busy || _clientStitching) return;
        _busy = true;
        try
        {
            _activeTab = "play";
            _showWipPlayer = true;
            await RefreshWipMetaAsync();

            if (!string.IsNullOrEmpty(_clientWipUrl) && !_wipStale)
            {
                _message = "Playing WIP";
                return;
            }

            await RefreshWipMetaAsync();
            if (_wipExists && !_wipStale)
            {
                _clientWipUrl = null;
                _showWipPlayer = true;
                _showScenePlayer = false;
                _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _message = "Playing WIP (up to date)";
                return;
            }

            var sceneNums = _scenes
                .Where(s => s.CompositeExists || s.ClipsOnDisk > 0 || MediaFolder.IsConnected || MediaFolder.IsSyncing)
                .OrderBy(s => s.SceneNumber)
                .Select(s => s.SceneNumber)
                .ToList();
            if (sceneNums.Count == 0)
            {
                _error = MediaFolder.IsConnected
                    ? "No scene videos were found in your local media folder or on the server."
                    : "Connect your local media folder to rebuild this movie from your synced clips.";
                return;
            }

            _clientStitching = true;
            _error = null;
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
                await Stitch.RevokePreviewUrlAsync();
                var meta = await Engine.GetWipMovieMetaAsync(_projectId);
                var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
                var segs = await Stitch.CollectAndMixSceneSegmentInfosAsync(_projectId, sceneNums, _scenes, stale);
                if (segs.Count == 0)
                {
                    _error = "No scene videos were found";
                    _showWipPlayer = false;
                    return;
                }

                _clientStitchStatus = segs.Count == 1
                    ? "Loading…"
                    : $"Combining {segs.Count} file(s)…";
                var result = await Stitch.ConcatAsync(segs.Select(s => s.Url).ToList());
                if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                {
                    _error = result.Error ?? "Browser stitch failed";
                    _showWipPlayer = false;
                    return;
                }

                _clientWipUrl = result.Url;
                _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _message = $"Preview ready — {segs.Count} scene(s)";

                // film_build.v1: full segment EDL + studio.sha256 (non-fatal)
                try
                {
                    _clientStitchStatus = "Saving cut timeline…";
                    var reg = await Stitch.RegisterFilmBuildAfterWipStitchAsync(_projectId, segs, result);
                    if (reg.Ok && !string.IsNullOrWhiteSpace(reg.FilmId))
                        _message = $"Preview ready — {segs.Count} scene(s) · film {reg.FilmId}";
                }
                catch
                {
                    /* provenance must not block playback */
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _showWipPlayer = false;
                _clientWipUrl = null;
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

    /// <summary>Connect a local media folder from the WIP player's "needs rebuild" prompt, then
    /// immediately retry the rebuild — a single click instead of connect-then-hunt-for-play-again.</summary>
    internal async Task ConnectFolderForWipAsync()
    {
        await MediaFolder.EnsureHubHookAsync();
        var connected = await MediaFolder.ConnectFolderAsync();
        if (connected)
            await PlayWipAsync();
    }

    internal async Task PlaySceneAsync(int scene)
    {
        // _busy must flip true synchronously, before the first await — otherwise a fast second
        // click (e.g. double-click) can slip past this guard during the window between it and
        // wherever the flag used to get set further down, running a second concurrent stitch that
        // races the first over the same locally-cached clip blob URLs (one call's blob gets
        // revoked-and-replaced out from under the other's in-flight ffmpeg fetch — "Failed to
        // fetch"). Same fix applied to every other Play*/stitch entry point in this file and
        // Scenes.razor.
        if (_busy || _clientStitching) return;
        _busy = true;
        try
        {
            _showClipPlayer = false;
            await RefreshWipMetaAsync();
            var summary = _scenes.FirstOrDefault(s => s.SceneNumber == scene);
            var stale = (await Engine.GetWipMovieMetaAsync(_projectId))?.StaleScenes?.Contains(scene) == true;
            var compositeOk = summary?.CompositeExists == true;
            var needsStitch = !compositeOk || stale;

            if (!needsStitch && compositeOk)
            {
                _clientSceneUrl = null;
                _showWipPlayer = false;
                _playingScene = scene;
                _showScenePlayer = true;
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _message = $"Playing S{scene:D2} composite";
                return;
            }

            var clipsOnDisk = summary?.ClipsOnDisk ?? 0;
            if (clipsOnDisk <= 0 && !compositeOk)
            {
                _error = $"No clips for S{scene:D2} — generate clips first";
                return;
            }

            _clientStitching = true;
            _error = null;
            _clientStitchStatus = "Collecting clips…";
            _showWipPlayer = false;
            _clientWipUrl = null;
            _playingScene = scene;
            _showScenePlayer = true;
            _clientSceneUrl = null;
            try
            {
                var urls = await Stitch.CollectClipUrlsAsync(_projectId, scene);
                if (urls.Count == 0 && compositeOk)
                {
                    _clientSceneUrl = null;
                    _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _message = $"Playing S{scene:D2} composite (may be stale)";
                    return;
                }

                if (urls.Count == 0)
                {
                    _error = $"No on-disk clips for S{scene:D2}";
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

                _clientSceneUrl = result.Url;
                _sceneVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _message = urls.Count == 1
                    ? $"Playing S{scene:D2} (single clip)"
                    : $"Playing S{scene:D2} — {urls.Count} clips stitched in browser";
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

    internal async Task HideScenePlayerAsync()
    {
        _showScenePlayer = false;
        _playingScene = null;
        if (!string.IsNullOrEmpty(_clientSceneUrl))
        {
            _clientSceneUrl = null;
            await Stitch.RevokePreviewUrlAsync();
        }
    }

    internal async Task HideWipPlayerAsync()
    {
        _showWipPlayer = false;
        if (!string.IsNullOrEmpty(_clientWipUrl))
        {
            _clientWipUrl = null;
            await Stitch.RevokePreviewUrlAsync();
        }
    }

    internal void PlayClip(int scene, int clip)
    {
        _playingClipScene = scene;
        _playingClipNum = clip;
        _showClipPlayer = true;
        _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _message = $"Playing S{scene:D2}C{clip:D2}";
    }

    internal void HideClipPlayer()
    {
        _showClipPlayer = false;
        _playingClipScene = null;
        _playingClipNum = null;
    }

    internal static string CacheBust(string url) => KeyFormatting.CacheBust(url);

    // CacheBust() stamps the current second, so calling it inline in markup re-evaluates on
    // every render (any SignalR/job-poll re-render elsewhere on the page) and gives the <video>
    // a new src each time, which makes the browser reload the resource and restart playback —
    // looks like looping. Memoized per key below instead of recomputed per call.
    internal string? _wipServerSrcForProject;
    internal string? _wipServerSrcCached;

    internal string? WipServerSrc()
    {
        if (_wipServerSrcForProject != _projectId)
        {
            _wipServerSrcForProject = _projectId;
            _wipServerSrcCached = CacheBust(Engine.WipMovieUrl(_projectId));
        }
        return _wipServerSrcCached;
    }

    internal int? _sceneServerSrcScene;
    internal string? _sceneServerSrcCached;

    internal string? SceneServerSrc(int sn)
    {
        if (_sceneServerSrcScene != sn)
        {
            _sceneServerSrcScene = sn;
            _sceneServerSrcCached = CacheBust(Engine.CompositeVideoUrl(_projectId, sn));
        }
        return _sceneServerSrcCached;
    }

    internal (int Scene, int Clip)? _clipServerSrcKey;
    internal string? _clipServerSrcCached;

    internal string? ClipServerSrc(int scene, int clip)
    {
        if (_clipServerSrcKey != (scene, clip))
        {
            _clipServerSrcKey = (scene, clip);
            _clipServerSrcCached = CacheBust(Engine.ClipVideoUrl(_projectId, scene, clip));
        }
        return _clipServerSrcCached;
    }

    internal static string FormatBytes(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#} MB" :
        n >= 1_000 ? $"{n / 1_000.0:0.#} KB" : $"{n} B";

    internal async Task SelectSceneAsync(int scene)
    {
        _selectedScene = scene;
        CloseApplyPanel();
        await LoadSelectedDetailAsync(scene);
        await TryLoadDraftsForSceneAsync(scene);
    }

    internal int ClipCountFor(int scene) =>
        _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.ClipCount ?? 0;

    internal int ClipCountOnDisk(int scene) =>
        _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.ClipsOnDisk ?? 0;

    internal bool SceneHasComposite(int scene) =>
        _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.CompositeExists == true;

    internal bool ClipOnDisk(int scene, int clip)
    {
        if (_selectedDetail is { } d && d.SceneNumber == scene)
        {
            var c = d.Clips.FirstOrDefault(x => x.ClipNumber == clip);
            if (c is not null) return c.OnDisk;
        }
        // Fall back: if scene has all clips on disk, assume yes
        var s = _scenes.FirstOrDefault(x => x.SceneNumber == scene);
        return s is not null && s.ClipsOnDisk >= s.ClipCount && s.ClipCount > 0;
    }

    internal async Task ReviewAsync(int scene, int clip, string status)
    {
        _busy = true;
        _error = null;
        try
        {
            await Engine.ReviewClipAsync(_projectId, scene, clip, status, _note);
            _message = $"Marked S{scene:D2}C{clip:D2} {status}";
            _note = "";
            await SoftLoadAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal static string ClipKey(int scene, int clip) => $"S{scene:D2}C{clip:D2}";

    internal bool IsAutoReviewRunning(int scene, int clip) =>
        _job is not null &&
        (_job.Status is "running" or "queued") &&
        (
            (string.Equals(_job.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) &&
             _job.Scene == scene &&
             _job.Clip == clip)
            ||
            (string.Equals(_job.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase) &&
             (_job.Scene is null || _job.Scene == scene))
        );

    internal ClipAutoReviewDraft? GetLocalDraft(int scene, int clip) =>
        _drafts.TryGetValue(ClipKey(scene, clip), out var d) ? d : null;

    internal bool HasIncludedEdits() =>
        _editRows is { Count: > 0 } && _editRows.Any(r => r.Include && !string.IsNullOrWhiteSpace(r.Value));

    internal async Task StartAutoReviewAsync(int scene, int clip)
    {
        _busy = true;
        _error = null;
        _message = null;
        CloseApplyPanel();
        try
        {
            await EnsureHubAsync();
            _message = $"Sampling frames S{scene:D2}C{clip:D2}…";
            StateHasChanged();
            var (frames, sampleErr) = await Stitch.SampleAutoReviewFramesAsync(_projectId, scene, clip);
            if (!string.IsNullOrWhiteSpace(sampleErr) || frames.Count == 0)
                throw new InvalidOperationException(sampleErr ?? "No frames sampled");

            _message = $"Uploading {frames.Count} frame(s) · reviewing S{scene:D2}C{clip:D2}…";
            StateHasChanged();
            var started = await Engine.StartClipAutoReviewAsync(_projectId, scene, clip, frames);
            _job = started;
            if (_job is null)
            {
                var jobs = await Engine.GetJobAsync();
                _job = jobs?.Job;
            }
            _message = $"Reviewing S{scene:D2}C{clip:D2}…";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal MovieAutoReviewReport? _movieReport;

    internal async Task<IReadOnlyList<string>> ResolveSceneUrlsForReviewAsync(int scene)
    {
        var summary = _scenes.FirstOrDefault(s => s.SceneNumber == scene);
        if (summary?.CompositeExists == true)
        {
            return new List<string> { Engine.CompositeVideoUrl(_projectId, scene) };
        }
        return await Stitch.CollectClipUrlsAsync(_projectId, scene);
    }

    internal async Task StartFullMovieReviewAsync()
    {
        if (_busy || JobRunning || _isReviewing) return;
        _busy = true;
        _isReviewing = true;
        _reviewProgressPct = 5;
        _reviewProgressStatus = "Initializing frame sampling across scenes…";
        _error = null;
        _message = null;
        StateHasChanged();
        try
        {
            var keyframes = new List<MovieAutoReviewKeyframe>();
            var scenesToReview = _scenes.OrderBy(x => x.SceneNumber).ToList();

            for (var i = 0; i < scenesToReview.Count; i++)
            {
                var s = scenesToReview[i];
                _reviewProgressPct = 5 + (int)(55.0 * (i + 1) / Math.Max(1, scenesToReview.Count));
                _reviewProgressStatus = $"Sampling visual frames for Scene {s.SceneNumber} ({i + 1}/{scenesToReview.Count})…";
                StateHasChanged();

                var urls = await ResolveSceneUrlsForReviewAsync(s.SceneNumber);
                if (urls is { Count: > 0 })
                {
                    foreach (var url in urls.Take(2))
                    {
                        try
                        {
                            var framesResult = await Stitch.ExtractFramesRawAsync(url, mode: "span", count: 2);
                            if (framesResult.Success && framesResult.Frames is { Count: > 0 })
                            {
                                foreach (var f in framesResult.Frames)
                                {
                                    if (string.IsNullOrWhiteSpace(f.Base64)) continue;
                                    keyframes.Add(new MovieAutoReviewKeyframe
                                    {
                                        SceneNumber = s.SceneNumber,
                                        Label = $"SCENE_{s.SceneNumber:D2}",
                                        Base64 = f.Base64,
                                        Mime = string.IsNullOrWhiteSpace(f.Mime) ? "image/jpeg" : f.Mime
                                    });
                                }
                            }
                        }
                        catch { /* fall through */ }
                    }
                }
            }

            if (keyframes.Count == 0)
            {
                _error = "No video clips available to sample for movie review. Generate scene clips first.";
                return;
            }

            _reviewProgressPct = 75;
            _reviewProgressStatus = $"Evaluating 6 categories (Continuity, Character, Lighting, Pacing, Dialogue, Music) across {keyframes.Count} sampled keyframes with Vision AI…";
            StateHasChanged();

            var envelope = await Engine.ReviewMovieAsync(_projectId, keyframes);
            if (envelope?.Report is not null)
            {
                _movieReport = envelope.Report;
                _reviewProgressPct = 100;
                _reviewProgressStatus = "Full movie AI review ready!";
                _message = $"Full movie AI review ready — Score: {_movieReport.OverallScore}/10 ({_movieReport.Verdict})";
            }
            else
            {
                _error = "Failed to generate full movie review report.";
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _isReviewing = false;
            _busy = false;
            StateHasChanged();
        }
    }

    internal async Task StartBatchAutoReviewAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        CloseApplyPanel();
        try
        {
            await EnsureHubAsync();
            // Client-orchestrated: sample frames per clip, then single authenticated review job.
            var targets = new List<(int Scene, int Clip)>();
            foreach (var s in _scenes.OrderBy(x => x.SceneNumber))
            {
                if (s.ClipsOnDisk <= 0 && s.ClipCount <= 0) continue;
                // Prefer detail when selected; otherwise use summary counts
                SceneDetail? detail = null;
                if (_selectedScene == s.SceneNumber)
                    detail = _selectedDetail;
                else
                {
                    try
                    {
                        detail = (await Engine.GetSceneDetailAsync(_projectId, s.SceneNumber))?.Scene;
                    }
                    catch { /* fall through */ }
                }

                if (detail?.Clips is { Count: > 0 })
                {
                    foreach (var c in detail.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                        targets.Add((s.SceneNumber, c.ClipNumber));
                }
                else if (s.ClipsOnDisk > 0)
                {
                    for (var c = 1; c <= Math.Max(s.ClipCount, s.ClipsOnDisk); c++)
                    {
                        if (ClipOnDisk(s.SceneNumber, c))
                            targets.Add((s.SceneNumber, c));
                    }
                }
            }

            // onlyMissing: skip clips that already have a draft
            var todo = new List<(int Scene, int Clip)>();
            foreach (var (scene, clip) in targets)
            {
                if (_drafts.ContainsKey(ClipKey(scene, clip)))
                    continue;
                try
                {
                    var existing = await Engine.GetClipAutoReviewDraftAsync(_projectId, scene, clip);
                    if (existing is not null)
                    {
                        _drafts[ClipKey(scene, clip)] = existing;
                        continue;
                    }
                }
                catch { /* treat as missing */ }
                todo.Add((scene, clip));
            }

            if (todo.Count == 0)
            {
                _message = "Batch auto-review: nothing to do (no missing drafts)";
                return;
            }

            var ok = 0;
            var failed = 0;
            for (var i = 0; i < todo.Count; i++)
            {
                var (scene, clip) = todo[i];
                _message = $"Auto-review {i + 1}/{todo.Count}: sampling S{scene:D2}C{clip:D2}…";
                StateHasChanged();
                try
                {
                    var (frames, sampleErr) = await Stitch.SampleAutoReviewFramesAsync(_projectId, scene, clip);
                    if (!string.IsNullOrWhiteSpace(sampleErr) || frames.Count == 0)
                        throw new InvalidOperationException(sampleErr ?? "No frames");

                    _message = $"Auto-review {i + 1}/{todo.Count}: reviewing S{scene:D2}C{clip:D2} ({frames.Count} frames)…";
                    StateHasChanged();
                    var started = await Engine.StartClipAutoReviewAsync(_projectId, scene, clip, frames);
                    _job = started;
                    var snap = await Engine.WaitForJobTerminalAsync(
                        jobId: started?.JobId,
                        timeout: TimeSpan.FromMinutes(6));
                    _job = snap ?? started;
                    if (snap is not null &&
                        string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        ok++;
                        try
                        {
                            var d = await Engine.GetClipAutoReviewDraftAsync(_projectId, scene, clip);
                            if (d is not null)
                                _drafts[ClipKey(scene, clip)] = d;
                        }
                        catch { /* optional */ }
                    }
                    else
                    {
                        failed++;
                        var err = snap?.Error ?? snap?.Message ?? "job failed";
                        _error = $"S{scene:D2}C{clip:D2}: {err}";
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _error = $"S{scene:D2}C{clip:D2}: {ex.Message}";
                }
            }

            try { await Engine.GetReviewIndexAsync(_projectId, rebuild: true); } catch { /* optional */ }
            await SoftLoadAsync();
            _message = $"Batch auto-review done: {ok} ok, {failed} failed of {todo.Count}";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal void OpenApplyPanel(int scene, int clip)
    {
        var draft = GetLocalDraft(scene, clip);
        if (draft is null) return;
        _editKey = ClipKey(scene, clip);
        _editRows = draft.Suggestions.Select(s => new EditRow
        {
            Layer = s.Layer,
            Field = s.Field,
            CharKey = s.CharKey,
            Label = string.IsNullOrWhiteSpace(s.Label) ? s.Field : s.Label,
            CurrentValue = s.CurrentValue ?? "",
            Value = s.SuggestedValue ?? "",
            Rationale = s.Rationale,
            Include = s.IncludeByDefault,
        }).ToList();
        // If no structured suggestions but fail, still allow empty panel + manual regen
        if (_editRows.Count == 0 &&
            string.Equals(draft.Suggestion, "fail", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(draft.Note))
        {
            _editRows.Add(new EditRow
            {
                Layer = "clip",
                Field = "visual_prompt",
                Label = "Clip note (add to visual prompt yourself or leave unchecked)",
                CurrentValue = "",
                Value = draft.Note,
                Include = false,
                Rationale = "AI did not propose a full field rewrite — edit or leave unchecked.",
            });
        }
    }

    internal void CloseApplyPanel()
    {
        _editKey = null;
        _editRows = null;
    }

    internal async Task ApplyAndRegenAsync(int scene, int clip)
    {
        if (_editRows is null) return;
        var items = _editRows
            .Where(r => r.Include && !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new ClipAutoReviewApplyItem
            {
                Layer = r.Layer,
                Field = r.Field,
                CharKey = r.CharKey,
                Value = r.Value.Trim(),
            })
            .ToList();

        _busy = true;
        _error = null;
        try
        {
            if (items.Count > 0)
            {
                await Engine.ApplyClipAutoReviewAsync(_projectId, scene, clip, items);
                _message = $"Saved {items.Count} change(s) — regenerating S{scene:D2}C{clip:D2}…";
            }
            else
            {
                _message = $"Regenerating S{scene:D2}C{clip:D2} (no field changes)…";
            }

            await EnsureHubAsync();
            await Engine.StartSceneGenAsync(_projectId, scene, onlyMissing: false, clip: clip);
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            CloseApplyPanel();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task ApproveAsync(int scene)
    {
        _busy = true;
        _error = null;
        try
        {
            await EnsureHubAsync();
            // Approve is review state only — Play stitches in the browser (no server remux).
            await Engine.ApproveSceneAsync(_projectId, scene, _note);
            _message = $"Approved S{scene:D2}";
            await SoftLoadAsync();
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task CancelAsync()
    {
        try
        {
            await Engine.CancelJobAsync();
            _message = "Cancel requested";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
    }

    internal void HandleYouTubeOAuthRedirect()
    {
        var uri = new Uri(Nav.Uri);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("youtube", out var status))
            return;
        if (status == "connected")
            _message = "YouTube channel connected";
        else if (status == "error")
            _error = query.TryGetValue("message", out var msg) ? msg.ToString() : "YouTube connect failed";
        // Drop the one-shot query params so a page refresh doesn't re-show the toast.
        Nav.NavigateTo(uri.GetLeftPart(UriPartial.Path), replace: true);
    }

    internal bool _showIncompleteWarning;
    internal bool _confirmedIncompletePublish;
    internal int _incompleteScenesCount;
    internal int _missingClipsCount;

    internal void CheckIncompleteMovieState()
    {
        _missingClipsCount = _scenes
            .Where(s => !s.CompositeExists)
            .Sum(s => Math.Max(0, s.ClipCount - s.ClipsOnDisk));
        _incompleteScenesCount = _scenes
            .Count(s => !s.CompositeExists && s.ClipsOnDisk < s.ClipCount);
    }

    internal async Task ConfirmSaveAsync()
    {
        CheckIncompleteMovieState();
        if ((_incompleteScenesCount > 0 || _missingClipsCount > 0) && !_confirmedIncompletePublish)
        {
            _showIncompleteWarning = true;
            StateHasChanged();
            return;
        }

        _showIncompleteWarning = false;
        await PublishDemoAsync();
    }

    internal async Task ConfirmIncompleteAndPublishAsync()
    {
        _confirmedIncompletePublish = true;
        _showIncompleteWarning = false;
        await PublishDemoAsync();
    }

    internal void CancelIncompleteWarning()
    {
        _showIncompleteWarning = false;
    }

    /// <summary>
    /// Build cut + publish to /api/demos → YouTube upload. Gallery lists the film once YouTube id is set.
    /// </summary>
    internal async Task PublishDemoAsync()
    {
        if (!_demoAcceptedGuidelines)
        {
            _error = "Accept the gallery guidelines before submitting.";
            return;
        }

        _busy = true;
        _isPublishing = true;
        _publishProgressPct = 5;
        _publishProgressStatus = "Preparing movie cut for upload...";
        _dotNetRef ??= DotNetObjectReference.Create(this);
        _error = null;
        _message = null;
        try
        {
            var mediaUrl = await EnsureShareableMovieUrlAsync();
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                _error = "Could not build a movie to share — generate clips first.";
                return;
            }

            var uploadPath = "/api/demos";
            var token = Session.Token;
            var title = string.IsNullOrWhiteSpace(_demoTitle) ? _projectId : _demoTitle.Trim();
            var description = string.IsNullOrWhiteSpace(_demoDescription) ? "" : _demoDescription.Trim();

            // Register stitched export hash so server can auto-public trusted demos
            if (mediaUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                var expPath = $"assets/exports/{_projectId}_demo.mp4";
                await MediaFolder.RegisterBlobAsExportAsync(_projectId, mediaUrl, expPath);
            }

            var res = await JS.InvokeAsync<System.Text.Json.JsonElement>(
                "PageToMovieExport.uploadDemoMovieAsync",
                mediaUrl,
                uploadPath,
                token,
                new
                {
                    title,
                    description,
                    projectId = _projectId,
                    fileName = $"{_projectId}_demo.mp4",
                    acceptedGuidelines = true,
                    madeForKids = _demoMadeForKids,
                    isAiSynthetic = _demoIsAiSynthetic,
                },
                _dotNetRef);

            if (!res.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            {
                if (_wipExists && !_wipStale && string.IsNullOrEmpty(_clientWipUrl))
                {
                    var pub = await Engine.PublishDemoFromWipAsync(
                        _projectId,
                        title,
                        string.IsNullOrWhiteSpace(description) ? null : description,
                        acceptedGuidelines: true,
                        madeForKids: _demoMadeForKids,
                        isAiSynthetic: _demoIsAiSynthetic);
                    if (pub?.Ok == true)
                    {
                        _activeTab = "review";
                        _message = (pub.Message ?? $"“{pub.Demo?.Title ?? title}” sent to YouTube — it appears in the gallery when the upload finishes.") +
                            (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
                        return;
                    }
                    _error = pub?.Error ?? "Demo submit failed";
                    return;
                }

                var err = res.TryGetProperty("error", out var e) ? e.GetString() : "upload failed";
                _error = err ?? "Demo upload failed";
                return;
            }

            var publishedTitle = title;
            if (res.TryGetProperty("demo", out var demoEl) && demoEl.ValueKind == System.Text.Json.JsonValueKind.Object
                && demoEl.TryGetProperty("title", out var tEl))
            {
                publishedTitle = tEl.GetString() ?? publishedTitle;
            }

            var msg = res.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
            _activeTab = "review";
            _message = (msg ?? $"“{publishedTitle}” sent to YouTube — it appears in the gallery when the upload finishes.") +
                (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
            _isPublishing = false;
        }
    }

    /// <summary>True after EnsureShareableMovieUrlAsync if the last built cut has no background
    /// music because the local media folder wasn't connected on this tab — MixSceneMusicAsync has
    /// no server fallback for music (unlike clips) and is deliberately best-effort/never-blocking,
    /// so without this flag a tab that never connected its folder silently exports/uploads a
    /// musicless movie with no indication anything was skipped. Callers append a note using it.</summary>
    internal bool _lastExportMissingMusic;

    /// <summary>Return a browser-fetchable URL for the full cut (blob or authenticated WIP).</summary>
    internal async Task<string?> EnsureShareableMovieUrlAsync()
    {
        await RefreshWipMetaAsync();

        // Best-effort, no dialog: the common case is the folder was already connected on another
        // tab and this one just never got the silent no-gesture reconnect — try once before
        // building, so music generated elsewhere still gets picked up here instead of being
        // silently dropped. TryReconnectAsync only succeeds if the browser already has persisted
        // permission (no folder picker popup); it's a no-op otherwise, unlike ConnectFolderAsync
        // which would intrusively prompt every export for users who never connected a folder.
        if (!MediaFolder.IsConnected)
            await MediaFolder.TryReconnectAsync();
        _lastExportMissingMusic = !MediaFolder.IsConnected;

        // Stitch fresh in browser to ensure all newly generated clips are included with zero duplicates
        var sceneNums = _scenes
            .Where(s => s.CompositeExists || s.ClipsOnDisk > 0)
            .OrderBy(s => s.SceneNumber)
            .Select(s => s.SceneNumber)
            .ToList();
        if (sceneNums.Count == 0)
            return null;

        _clientStitching = true;
        _clientStitchStatus = "Preparing cut for upload…";
        try
        {
            // Revoke the OLD preview before collecting new segments, not after — CollectAndMix-
            // SceneSegmentsAsync's internal per-scene concatVideosAsync calls reuse the JS side's
            // single shared blob-tracking slot, so a scene segment with no music to mix can end up
            // being exactly the URL RevokePreviewUrlAsync() would revoke; calling it here (before
            // any new segment exists) means it can only ever revoke a blob from a prior, separate
            // operation, never one this call just built. Revoking after collection blew up the
            // final combine's fetch of that segment with "Failed to fetch" the moment that
            // coincidence happened — reproducible on a single, non-double-clicked Share & Export.
            await Stitch.RevokePreviewUrlAsync();
            var meta = await Engine.GetWipMovieMetaAsync(_projectId);
            var stale = meta?.StaleScenes?.ToHashSet() ?? new HashSet<int>();
            var segs = await Stitch.CollectAndMixSceneSegmentInfosAsync(_projectId, sceneNums, _scenes, stale);
            if (segs.Count == 0) return null;
            var result = await Stitch.ConcatAsync(segs.Select(s => s.Url).ToList());
            if (!result.Success || string.IsNullOrWhiteSpace(result.Url))
                throw new InvalidOperationException(result.Error ?? "Browser stitch failed");
            _clientWipUrl = result.Url;
            _showWipPlayer = true;
            _wipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                await Stitch.RegisterFilmBuildAfterWipStitchAsync(_projectId, segs, result);
            }
            catch { /* non-fatal */ }
            return _clientWipUrl;
        }
        finally
        {
            _clientStitching = false;
            _clientStitchStatus = null;
        }
    }

    internal async Task ConnectYouTubeAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var url = await Engine.GetYouTubeConnectUrlAsync();
            Nav.NavigateTo(url, forceLoad: true);
        }
        catch (Exception ex) { _error = ex.Message; _busy = false; }
    }

    internal async Task DisconnectYouTubeAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            await Engine.DisconnectYouTubeAsync();
            _message = "YouTube channel disconnected";
            await RefreshYouTubeStatusAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task StartYouTubeUploadAsync()
    {
        _busy = true;
        _error = null;
        _message = "Preparing movie cut for YouTube upload…";
        StateHasChanged();
        try
        {
            var mediaUrl = await EnsureShareableMovieUrlAsync();
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                _error = "Could not build a movie to upload — generate scene clips first.";
                return;
            }

            await EnsureHubAsync();
            _dotNetRef ??= DotNetObjectReference.Create(this);

            if (mediaUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                var res = await JS.InvokeAsync<System.Text.Json.JsonElement>(
                    "PageToMovieExport.uploadDemoMovieAsync",
                    mediaUrl,
                    "/api/jobs/youtube-upload",
                    Session.Token,
                    new
                    {
                        projectId = _projectId,
                        title = _youTubeTitle,
                        description = _youTubeDescription,
                        privacyStatus = _youTubePrivacy,
                        fileName = $"{_projectId}_wip.mp4",
                    },
                    _dotNetRef);

                if (!res.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                {
                    _error = res.TryGetProperty("error", out var err) ? err.GetString() : "Failed to upload movie cut to server";
                    return;
                }
            }
            else
            {
                await Engine.StartYouTubeUploadAsync(new StartYouTubeUploadRequest
                {
                    ProjectId = _projectId,
                    Title = _youTubeTitle,
                    Description = _youTubeDescription,
                    PrivacyStatus = _youTubePrivacy,
                });
            }

            _activeTab = "review";
            _message = "Uploading to YouTube…" +
                (_lastExportMissingMusic ? " (No local media folder connected — background music not included.)" : "");
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    internal Task EnsureHubAsync() => Hub.EnsureStartedAsync();

    public async ValueTask DisposeAsync()
    {
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        _clientWipUrl = null;
        _clientSceneUrl = null;
        await Stitch.RevokePreviewUrlAsync();
        _dotNetRef?.Dispose();
    }
}

