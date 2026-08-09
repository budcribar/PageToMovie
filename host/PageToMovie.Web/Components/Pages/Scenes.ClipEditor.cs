using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: ClipEditor — partial methods/properties for the Scenes page
public partial class Scenes
{

    internal void ToggleClipDurationSort()
    {
        if (_clipSortByDuration)
            _clipSortAscending = !_clipSortAscending;
        else
        {
            _clipSortByDuration = true;
            _clipSortAscending = true;
        }
    }


    /// <summary>Clips in open scene, optionally sorted by actual/plan duration.</summary>
    internal IEnumerable<ClipSummary> SortedDetailClips
    {
        get
        {
            if (_detail?.Clips is null)
                return Array.Empty<ClipSummary>();
            if (!_clipSortByDuration)
                return _detail.Clips.OrderBy(c => c.ClipNumber);
            static double Dur(ClipSummary c) =>
                c.ActualDurationSeconds ?? (c.DurationSeconds > 0 ? c.DurationSeconds : 0);
            return _clipSortAscending
                ? _detail.Clips.OrderBy(Dur).ThenBy(c => c.ClipNumber)
                : _detail.Clips.OrderByDescending(Dur).ThenBy(c => c.ClipNumber);
        }
    }


    /// <summary>
    /// True when this exact clip is the one currently being (re)generated — the server updates
    /// the job's Scene/Clip to whichever item it's actively working on, for both single-clip
    /// regen (kind "scene") and multi-select batch regen (kind "batch"). Used to avoid showing
    /// a stale "on disk" pill or letting Play open the file mid-overwrite.
    /// </summary>
    internal bool IsClipGenBusy(int clipNumber)
    {
        if (_detail is null) return false;
        var sn = _detail.SceneNumber;
        if (_pendingRegenScene == sn) return true;

        bool Affects(JobSnapshot j) =>
            (string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)) &&
            IsScenesWorkflowJob(j.Kind) &&
            j.Scene == sn && j.Clip == clipNumber;

        if (_job is not null && Affects(_job))
            return true;
        return _myJobs.Any(Affects);
    }


    /// <summary>
    /// Clip N (N&gt;1) needs clip N-1 on disk — Imagine continues from the previous video.
    /// </summary>
    internal bool PreviousClipMissing(int clipNumber)
    {
        if (clipNumber <= 1 || _detail is null) return false;
        var prev = _detail.Clips.FirstOrDefault(c => c.ClipNumber == clipNumber - 1);
        return prev is null || !prev.OnDisk;
    }


    /// <summary>Select clips in the open scene that are not on disk yet.</summary>
    internal void SelectMissingClips()
    {
        if (_detail is null) return;
        _selectedClips.Clear();
        foreach (var c in _detail.Clips.Where(c => !c.OnDisk))
            _selectedClips.Add(c.ClipNumber);
    }


    internal async Task OpenInExternalEditorAsync(int? sceneNumber = null, int? clipNumber = null)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.OpenInExternalEditorAsync(_projectId, sceneNumber, clipNumber, _preferredVideoEditor);
            if (res.Ok)
            {
                _message = $"🎬 Opened video in {res.Editor ?? _preferredVideoEditor}.";
            }
            else if (!string.IsNullOrWhiteSpace(res.VideoUrl))
            {
                var cleanPid = System.Text.RegularExpressions.Regex.Replace(_projectId, @"[^\w\.-]", "_");
                var fileName = sceneNumber is int sn
                    ? (clipNumber is int cn ? $"{cleanPid}_S{sn:D2}C{cn:D2}.mp4" : $"{cleanPid}_S{sn:D2}_composite.mp4")
                    : $"{cleanPid}_full.mp4";
                _message = $"🎬 Downloaded video to your PC — open {fileName} in {res.Editor ?? _preferredVideoEditor}.";
                try
                {
                    await JS.InvokeVoidAsync("eval", $"const a=document.createElement('a');a.href='{res.VideoUrl}';a.download='{fileName}';document.body.appendChild(a);a.click();document.body.removeChild(a);");
                }
                catch { /* ignore */ }
            }
            else
            {
                _error = res.Error ?? "Could not open external video editor.";
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


    internal void ToggleClipSelect(int cn, bool on)
    {
        if (on) _selectedClips.Add(cn);
        else _selectedClips.Remove(cn);
    }


    internal void ClearClipSelection() => _selectedClips.Clear();


    internal bool AllClipsSelected =>
        _detail is { Clips.Count: > 0 } && _detail.Clips.All(c => _selectedClips.Contains(c.ClipNumber));


    internal void ToggleSelectAllClips(bool on)
    {
        if (_detail is null) return;
        if (on)
        {
            foreach (var c in _detail.Clips)
                _selectedClips.Add(c.ClipNumber);
        }
        else
        {
            _selectedClips.Clear();
        }
    }


    internal double? EstimateSelectedClipsCostUsd()
    {
        if (_costReport is null || _detail is null) return null;
        var row = _costReport.Scenes.FirstOrDefault(r => r.Scene == _detail.SceneNumber);
        if (row is null || row.ClipsTotal <= 0) return null;
        // Approximate: whole-scene draft cost spread evenly per clip (force-regen ignores on-disk state).
        return row.AllDraftUsd / row.ClipsTotal * _selectedClips.Count;
    }


    internal async Task EnsurePredecessorsUploadedAsync(List<(int Scene, int Clip)> targets)
    {
        if (!MediaFolder.IsConnected || string.IsNullOrEmpty(_projectId) || targets.Count == 0) return;

        // Cache scene detail per scene number — targets can span scenes (multi-scene batch),
        // and _detail may be loaded for a different scene than the one we're checking (or null,
        // when this runs from the scene-list page rather than a scene-detail view).
        var detailCache = new Dictionary<int, SceneDetail?>();
        async Task<SceneDetail?> GetSceneAsync(int sn)
        {
            if (detailCache.TryGetValue(sn, out var cached)) return cached;
            var d = _detail?.SceneNumber == sn ? _detail : (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
            detailCache[sn] = d;
            return d;
        }

        // Video-extend continuity (see FilmJobService.GenerateOneClipAsync + ClientMediaFolderService.
        // PrepareExtendSourceAsync): resolved once per batch, not per target, since it's the same
        // active project setting for every clip here.
        var extendModel = await ResolveActiveVideoExtendModelAsync();

        foreach (var (sn, cn) in targets)
        {
            if (cn <= 1) continue;
            var prevClipNum = cn - 1;
            if (targets.Any(t => t.Scene == sn && t.Clip == prevClipNum)) continue;

            // OnDisk alone isn't enough here: it's also true when only the .client.json marker
            // exists (clip synced to the client, then pruned off server disk) — SizeBytes is 0 in
            // that case since there are no real bytes for the video-extend gate to find and copy.
            var sceneDetail = await GetSceneAsync(sn);
            var prevSummary = sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == prevClipNum);
            var serverHasRealBytes = prevSummary?.OnDisk == true && prevSummary.SizeBytes >= 1024;
            if (!serverHasRealBytes)
            {
                var localBytes = await MediaFolder.GetClipBytesAsync(_projectId, sn, prevClipNum);
                if (localBytes is { Length: >= 1024 })
                {
                    _message = $"Uploading local predecessor S{sn:D2}C{prevClipNum:D2} to server…";
                    StateHasChanged();

                    await Engine.UploadClipAsync(_projectId, sn, prevClipNum, localBytes);
                }
            }

            if (extendModel is { } maxInputSeconds)
            {
                var wantsExtend = string.Equals(
                    sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)?.Continuation,
                    "extend_previous", StringComparison.OrdinalIgnoreCase);
                if (wantsExtend)
                {
                    // Best-effort: a false return just means no extend-source file appears on the
                    // server, so it falls back to today's fresh-gen behavior — never blocks generation.
                    await MediaFolder.PrepareExtendSourceAsync(_projectId, sn, cn, maxInputSeconds);
                }
            }
        }
    }


    /// <summary>Active project video model's max input length for a real video-extend call, or
    /// null when the model doesn't support real continuity (today: only Grok's video model does)
    /// or lookup fails.</summary>
    internal async Task<double?> ResolveActiveVideoExtendModelAsync()
    {
        try
        {
            var cfg = await Engine.GetConfigAsync(_projectId);
            var modelId = cfg?.Config is { } c && c.TryGetValue("model_name", out var el) &&
                          el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            var models = await Engine.GetSupportedModelsAsync(capability: "video");
            var entry = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (entry is not { SupportsVideoContinue: true }) return null;

            return entry.AbsMaxClipDurationSeconds ?? entry.MaxClipDurationSeconds ?? 15;
        }
        catch
        {
            return null;
        }
    }


    /// <summary>Clip numbers in scene <paramref name="sn"/> not yet on server disk (or synced-only) —
    /// used to pre-check predecessors before an "only missing" generation batch.</summary>
    internal async Task<List<(int Scene, int Clip)>> MissingClipTargetsAsync(int sn)
    {
        var detail = _detail?.SceneNumber == sn ? _detail : (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
        return detail?.Clips?.Where(c => !c.OnDisk).Select(c => (Scene: sn, Clip: c.ClipNumber)).ToList()
            ?? new List<(int Scene, int Clip)>();
    }


    internal async Task RegenSelectedClipsAsync()
    {
        if (_detail is null || _selectedClips.Count == 0) return;
        var sn = _detail.SceneNumber;
        _busy = true;
        _error = null;
        _message = null;
        _pendingRegenScene = sn;
        try
        {
            var targets = _selectedClips.OrderBy(c => c).Select(c => (Scene: sn, Clip: c)).ToList();
            await EnsureHubAsync();
            await EnsurePredecessorsUploadedAsync(targets);
            _job = await Engine.StartClipBatchGenAsync(_projectId, targets, resolution: _genResolution);
            _message = $"Regenerating {targets.Count} clip(s) in S{sn:D2} @ {_genResolution}…";
            _selectedClips.Clear();
            StateHasChanged();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; _pendingRegenScene = null; }
    }


    internal void SelectClip(int? cn)
    {
        _message = null; // clear any leftover completion message from a previous scene/action
        _selectedClip = cn;
        _clip = cn is int n
            ? _detail?.Clips.FirstOrDefault(c => c.ClipNumber == n)
            : null;
        _clipVersions = null;
        _clipVideoUrl = null;
        if (cn is int cnv)
        {
            // Force new <video> mount so we never keep a previous composite/clip stream
            _clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Resolved once, not inline in markup — CacheBust() stamps the current second, so
            // calling it inline re-evaluates on every render (any SignalR/job-poll re-render
            // elsewhere on the page) and gives the <video> a new src each time, which makes the
            // browser reload the resource and restart playback — looks like looping.
            _clipServerVideoUrl = _detail is not null
                ? CacheBust(Engine.ClipVideoUrl(_projectId, _detail.SceneNumber, cnv))
                : null;
            // Gate the <video> behind a loading spinner while we check for a newer local copy —
            // otherwise it renders immediately with the (possibly stale) server fallback src and
            // autoplays that before swapping to the fresh one once the check resolves.
            _clipVideoLoading = MediaFolder.IsConnected;
            // Stop full-scene autoplay panel if open
            if (_showScenePlayer && _playingScene == _detail?.SceneNumber)
            {
                _showScenePlayer = false;
                _playingScene = null;
            }
            if (_detail is not null)
                _ = LoadClipVideoAndTakesCountAsync(_detail.SceneNumber, cnv);
        }
    }


    /// <summary>
    /// A local file at a clip's canonical relative path is not necessarily the *current* version
    /// — a later regen/promote may have happened without this browser open to catch the
    /// auto-save. Every call site that trusts a local clip copy (playback, dialogue
    /// re-verification upload) should gate on this against the server's currently-registered
    /// size rather than assuming presence means current. Returns null on any lookup failure —
    /// callers then fall back to their own "trust local unconditionally" or "use server" default.
    /// </summary>
    internal async Task<long?> ResolveExpectedClipSizeAsync(int scene, int clip)
    {
        try
        {
            var status = await Engine.GetClipMediaStatusAsync(_projectId, scene, clip);
            if (status is { Ok: true })
                return status.OnServer ? status.ServerSizeBytes
                    : status.OnClient ? status.ClientSizeBytes
                    : null;
        }
        catch { /* best effort — falls back to unconditional local trust */ }
        return null;
    }


    /// <summary>Force re-generate a single clip (onlyMissing: false).</summary>
    internal async Task RegenClipAsync(int sn, int cn)
    {
        // Credits are rendered deterministically client-side — never sent to the video model.
        if (IsCreditsSceneNum(sn)) { await GenerateCreditsEntryAsync(sn); return; }
        if (!CastReady)
        {
            _error = CastBlockedTitle;
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        _pendingRegenScene = sn;
        try
        {
            await EnsureHubAsync();
            await EnsurePredecessorsUploadedAsync(new List<(int Scene, int Clip)> { (sn, cn) });
            await Engine.StartSceneGenAsync(_projectId, sn, onlyMissing: false, clip: cn, resolution: _genResolution);
            _message = $"Regenerating S{sn:D2}C{cn:D2} @ {_genResolution}…";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; _pendingRegenScene = null; }
    }


    /// <summary>xAI's edit input cap — see MaxVideoEditInputSeconds's doc comment (client hint;
    /// RunVideoEditAsync is the authoritative server-side check).</summary>
    internal static bool ClipExceedsEditDurationCap(ClipSummary clip) =>
        (clip.ActualDurationSeconds ?? clip.DurationSeconds) > MaxVideoEditInputSeconds + 0.01;


    internal void OpenVideoEditPrompt()
    {
        _videoEditPromptText = "";
        _showVideoEditPrompt = true;
    }


    internal void CloseVideoEditPrompt() => _showVideoEditPrompt = false;


    internal async Task SubmitVideoEditAsync()
    {
        if (_detail is null || _clip is null || string.IsNullOrWhiteSpace(_videoEditPromptText))
            return;

        var sn = _detail.SceneNumber;
        var cn = _clip.ClipNumber;
        _showVideoEditPrompt = false;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await EnsureHubAsync();
            await Engine.StartVideoEditAsync(_projectId, sn, cn, _videoEditPromptText.Trim());
            _message = $"Editing S{sn:D2}C{cn:D2}…";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal void OpenClipEditor(ClipSummary clip)
    {
        if (_detail is null) return;
        _clipEditorIsNew = false;
        _clipEditor = new ClipEditRequest
        {
            ProjectId = _projectId,
            Scene = _detail.SceneNumber,
            Clip = clip.ClipNumber,
            VisualPrompt = clip.VisualPrompt,
            NegativePrompt = clip.NegativePrompt,
            Dialogue = clip.Dialogue,
            Speaker = clip.Speaker,
            Delivery = clip.Delivery,
            PronunciationHint = clip.PronunciationHint,
            PrimarySubject = clip.PrimarySubject,
            CharactersOnScreen = new List<string>(clip.CharactersOnScreen),
            ColorPalette = clip.ColorPalette,
            FilmStock = clip.FilmStock,
            DurationSeconds = clip.DurationSeconds,
        };
        _clipEditorCast = new HashSet<string>(clip.CharactersOnScreen, StringComparer.OrdinalIgnoreCase);
    }


    internal void OpenAddClipDialog()
    {
        if (_detail is null) return;
        var nextClip = _detail.Clips.Count == 0 ? 1 : _detail.Clips.Max(c => c.ClipNumber) + 1;
        _clipEditorIsNew = true;
        _clipEditor = new ClipEditRequest
        {
            ProjectId = _projectId,
            Scene = _detail.SceneNumber,
            Clip = nextClip,
            DurationSeconds = 5,
        };
        _clipEditorCast = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }


    internal void CloseClipEditor() => _clipEditor = null;


    internal void ToggleClipEditorCast(string charKey, bool on)
    {
        if (on) _clipEditorCast.Add(charKey);
        else _clipEditorCast.Remove(charKey);
    }

    internal Task OnClipEditorCastToggled((string Key, bool On) args)
    {
        ToggleClipEditorCast(args.Key, args.On);
        return Task.CompletedTask;
    }




    internal async Task SaveClipEditorAsync()
    {
        if (_clipEditor is null || _detail is null) return;

        // Mirror server rules for fast feedback (server still authoritative).
        if (string.IsNullOrWhiteSpace(_clipEditor.VisualPrompt))
        {
            _error = "Visual prompt is required.";
            return;
        }
        if (_clipEditor.DurationSeconds < 0 || _clipEditor.DurationSeconds > 12)
        {
            _error = "Duration must be 0 (unset) or 3–12 seconds.";
            return;
        }
        if (_clipEditor.DurationSeconds is > 0 and < 3)
        {
            _error = "Duration must be at least 3s (or 0 to leave unset).";
            return;
        }
        var dlg = (_clipEditor.Dialogue ?? "").Trim();
        var spk = (_clipEditor.Speaker ?? "").Trim();
        var del = (_clipEditor.Delivery ?? "").Trim();
        var delNone = del.Length == 0 || string.Equals(del, "none", StringComparison.OrdinalIgnoreCase);
        if (dlg.Length > 0 && spk.Length == 0)
        {
            _error = "Dialogue needs a speaker. Pick who says the line, or clear the dialogue.";
            return;
        }
        if (dlg.Length > 0 && delNone)
        {
            _error = "Dialogue needs a delivery: Spoken (on camera), Voiceover (internal), or Off camera.";
            return;
        }
        if (spk.Length > 0 && dlg.Length == 0)
        {
            _error = "Speaker is set but dialogue is empty. Add the line, or set speaker to none.";
            return;
        }
        if (_clipEditorIsNew && (_clipEditor.Clip < 1 || _clipEditor.Clip > 200))
        {
            _error = "Clip number must be between 1 and 200.";
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            _clipEditor.CharactersOnScreen = _clipEditorCast.ToList();
            if (_clipEditorIsNew)
            {
                await Engine.AddClipAsync(_projectId, _detail.SceneNumber, _clipEditor);
                _message = $"Added S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — generate its video when ready";
            }
            else
            {
                await Engine.UpdateClipAsync(_projectId, _detail.SceneNumber, _clipEditor.Clip, _clipEditor);
                _message = $"Saved S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — Regen the clip to re-render video/audio with the new fields";
            }
            try { await Engine.CommitProjectChangesAsync(_projectId, $"Saved clip S{_detail.SceneNumber:D2}C{_clipEditor.Clip:D2}"); } catch { }
            await RefreshUncommittedStatusAsync();
            _clipEditor = null;
            await LoadDetailAsync(_detail.SceneNumber);
            var scenesDto = await Engine.GetScenesAsync(_projectId);
            if (scenesDto?.Scenes is not null)
            {
                _scenes = scenesDto.Scenes;
            }
            if (_selectedClip is int sel)
                _clip = _detail.Clips.FirstOrDefault(c => c.ClipNumber == sel);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal void RequestDeleteClip(int scene, int clip) => _deleteClipTarget = (scene, clip);


    internal void CancelDeleteClip() => _deleteClipTarget = null;


    internal async Task ConfirmDeleteClipAsync()
    {
        if (_deleteClipTarget is not { } target) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.DeleteClipAsync(_projectId, target.Scene, target.Clip);
            _deleteClipTarget = null;
            if (_selectedClip == target.Clip)
            {
                _selectedClip = null;
                _clip = null;
            }
            _message = $"Deleted S{target.Scene:D2}C{target.Clip:D2} — Play scene / Play WIP to refresh the assembled cut";
            await ReloadListAsync();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal int EstimateSelectedClips()
    {
        if (_scenes is null) return 0;
        // Generate always fills missing only — estimate remaining work on selected scenes.
        return _scenes
            .Where(x => _selected.Contains(x.SceneNumber))
            .Sum(s => Math.Max(0, s.ClipCount - s.ClipsOnDisk));
    }


    internal async Task OpenClipCompareAsync(int sceneNumber, int clipNumber)
    {
        _compareSceneNumber = sceneNumber;
        _compareClipNumber = clipNumber;
        _showClipCompare = true;
        _loadingClipVersions = true;
        _clipCompareMessage = null;
        _selectedCompareVersionId = null;
        StateHasChanged();

        try
        {
            var res = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
            _clipVersions = res?.Versions;
            _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
            await RefreshCompareVideoUrlsAsync();

            var trashRes = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
            _trashVersions = trashRes?.Versions;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load clip versions: {ex.Message}";
        }
        finally
        {
            _loadingClipVersions = false;
            StateHasChanged();
        }
    }


    internal void CloseClipCompare()
    {
        _showClipCompare = false;
        _clipVersions = null;
        _trashVersions = null;
        _clipCompareMessage = null;
    }


    internal async Task PromoteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.PromoteClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = $"Successfully promoted version {versionId} to active clip.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
                await RefreshCompareVideoUrlsAsync();
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
                await RefreshUncommittedStatusAsync();
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to promote clip version.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Promote failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }


    internal async Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.SoftDeleteClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = "Take deleted. You can restore it from the Trash Bin below.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to delete take.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }


    internal async Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.RestoreClipVersionAsync(_projectId, sceneNumber, clipNumber, versionId);
            if (res.Ok)
            {
                _clipCompareMessage = "Take restored from Trash Bin.";
                var resV = await Engine.GetClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _clipVersions = resV?.Versions;
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to restore take.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }


    internal async Task EmptyClipTrashAsync(int sceneNumber, int clipNumber)
    {
        _promotingVersion = true;
        _clipCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.EmptyClipTrashAsync(_projectId, sceneNumber, clipNumber);
            if (res.Ok)
            {
                _clipCompareMessage = "Purged deleted take(s).";
                var resT = await Engine.GetTrashClipVersionsAsync(_projectId, sceneNumber, clipNumber);
                _trashVersions = resT?.Versions;
            }
            else
            {
                _clipCompareMessage = res.Error ?? "Failed to empty trash.";
            }
        }
        catch (Exception ex)
        {
            _clipCompareMessage = $"Empty trash failed: {ex.Message}";
        }
        finally
        {
            _promotingVersion = false;
            StateHasChanged();
        }
    }

}
