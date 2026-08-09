using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: ListState — partial methods/properties for the Scenes page
public partial class Scenes
{
    internal bool ResolutionLocked => !string.IsNullOrWhiteSpace(_resolutionLock) || (_scenes is not null && _scenes.Sum(s => s.ClipsOnDisk) > 0);


    internal void ToggleSort(string column)
    {
        if (_sortBy == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortBy = column;
            _sortAscending = true;
        }
    }


    internal IEnumerable<SceneSummary> SortedVisibleScenes
    {
        get
        {
            var scenes = VisibleScenes;
            return _sortBy switch
            {
                "duration" => _sortAscending
                    ? scenes.OrderBy(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0)
                    : scenes.OrderByDescending(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0),
                _ => _sortAscending
                    ? scenes.OrderBy(s => s.SceneNumber)
                    : scenes.OrderByDescending(s => s.SceneNumber)
            };
        }
    }


    /// <summary>True when every cast member has approved voice + locked look (or voice-only + voice).</summary>
    internal bool CastReady => _castReady;


    internal string CastBlockedTitle =>
        _castMissing.Count > 0
            ? $"Approve voice + locked image first: {string.Join(", ", _castMissing.Take(4))}{(_castMissing.Count > 4 ? "…" : "")}"
            : "Approve voice + locked image for every character before generating video";


    internal bool SelectedLockedByOther =>
        _scenes is not null &&
        _selected.Any(sn => _scenes.Any(s => s.SceneNumber == sn && s.LockedByOther));


    internal string SelectionMode => _selectionMode;


    // Tri-state progress glyph for a scene's clip generation:
    //   ○ (muted)   nothing generated yet, or no clips planned
    //   ◐ (warning) some clips on disk, not all
    //   ● (success) every planned clip generated
    internal (string Glyph, string Css, string Title) SceneProgressGlyph(SceneSummary s)
    {
        if (s.ClipCount <= 0)
            return ("○", "text-muted", "No clips planned");
        if (s.ClipsComplete)
            return ("●", "text-success", $"All {s.ClipCount} clips generated");
        if (s.ClipsOnDisk > 0)
            return ("◐", "text-warning", $"{s.ClipsOnDisk} of {s.ClipCount} clips generated");
        return ("○", "text-muted", $"0 of {s.ClipCount} clips generated");
    }


    internal bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(_pickCharacter) ||
        !string.IsNullOrWhiteSpace(_pickLocation) ||
        !string.IsNullOrWhiteSpace(_pickSetting);


    internal void ClearFilters()
    {
        _pickCharacter = "";
        _pickLocation = "";
        _pickSetting = "";
    }


    internal IEnumerable<SceneSummary> FilteredScenes
    {
        get
        {
            if (_scenes is null) return Enumerable.Empty<SceneSummary>();
            IEnumerable<SceneSummary> list = _scenes;
            if (!string.IsNullOrWhiteSpace(_pickCharacter))
            {
                var match = _pickCharacter;
                list = list.Where(s => s.CharactersOnScreen.Any(c =>
                    string.Equals(c, match, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ShortChar(c), match, StringComparison.OrdinalIgnoreCase)));
            }
            if (!string.IsNullOrWhiteSpace(_pickLocation))
            {
                var match = _pickLocation;
                list = list.Where(s =>
                    string.Equals(s.PrimaryLocationId, match, StringComparison.OrdinalIgnoreCase) ||
                    s.LocationIds.Any(l => string.Equals(l, match, StringComparison.OrdinalIgnoreCase)));
            }
            if (!string.IsNullOrWhiteSpace(_pickSetting))
            {
                var text = _pickSetting;
                list = list.Where(s => (s.Setting ?? "").Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            return list;
        }
    }


    internal List<SceneSummary> VisibleScenes => FilteredScenes.ToList();


    internal void SelectByCharacter()
    {
        if (_scenes is null) return;
        if (string.IsNullOrWhiteSpace(_pickCharacter)) return;
        var match = _pickCharacter;
        _selected.Clear();
        foreach (var s in _scenes.Where(s =>
            s.CharactersOnScreen.Any(c =>
                string.Equals(c, match, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ShortChar(c), match, StringComparison.OrdinalIgnoreCase))))
        {
            _selected.Add(s.SceneNumber);
        }
    }


    internal void SelectByLocation()
    {
        if (_scenes is null || string.IsNullOrWhiteSpace(_pickLocation)) return;
        var match = _pickLocation;
        _selected.Clear();
        foreach (var s in _scenes.Where(s =>
            string.Equals(s.PrimaryLocationId, match, StringComparison.OrdinalIgnoreCase) ||
            s.LocationIds.Any(l => string.Equals(l, match, StringComparison.OrdinalIgnoreCase))))
        {
            _selected.Add(s.SceneNumber);
        }
    }


    /// <summary>Select scenes that still need clips (not fully on disk).</summary>
    internal void SelectMissingScenes()
    {
        if (_scenes is null) return;
        _selected.Clear();
        foreach (var s in VisibleScenes.Where(s => !s.ClipsComplete || s.ClipsOnDisk < s.ClipCount))
            _selected.Add(s.SceneNumber);
        _selectionMode = _selected.Count > 0 ? "missing" : "";
    }


    internal void RequestDeleteScene(int sn) => _deleteSceneTarget = sn;


    internal async Task ConfirmDeleteSceneAsync()
    {
        if (_deleteSceneTarget is not int sn) return;
        _deleteSceneTarget = null;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            // Persist: remove the scene from the shot plan (blueprint) so it doesn't reappear on reload.
            var res = await Engine.DeleteSceneAsync(_projectId, sn);
            if (!res.Ok)
            {
                _error = res.Error ?? "Could not delete the scene.";
                return;
            }
            _selected.Remove(sn);
            _message = res.Message ?? $"Deleted Scene {sn:D2}";
            await SoftReloadAsync();
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


    internal async Task AddSceneAsync(bool credits)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var res = await Engine.AddSceneAsync(_projectId, credits);
            if (!res.Ok)
            {
                _error = res.Error ?? "Could not add the scene.";
                return;
            }
            _message = res.Message ?? (credits ? "Added credits scene" : $"Added Scene {res.Scene:D2}");
            await SoftReloadAsync();
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


    /// <summary>
    /// Replan from the screenplay — scoped to the checked scenes when any are selected, so editing
    /// the Fountain (e.g. just the title) and regenerating doesn't re-prompt the AI for scenes whose
    /// script text didn't change (Stage2PlannerService merges a scoped replan into the existing
    /// blueprint instead of rebuilding it from scratch). Falls back to every scene — the original
    /// "restore missing scenes" behavior — when nothing is checked.
    /// </summary>
    internal async Task RebuildShotPlanAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var scoped = _selected.Count > 0;
            await Engine.StartStage2Async(new StartStage2Request
            {
                ProjectId = _projectId,
                Scenes = scoped ? string.Join(",", _selected.OrderBy(x => x)) : "all"
            });
            _message = scoped
                ? $"Regenerating {_selected.Count} selected scene(s) from the screenplay…"
                : "Rebuilding shot plan from screenplay…";
            await SoftReloadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }


    internal async Task LoadGenResolutionFromConfigAsync()
    {
        try
        {
            var dto = await Engine.GetConfigAsync(_projectId);
            if (dto?.Config is { } cfg)
            {
                if (cfg.TryGetValue("resolution", out var el) &&
                    el.ValueKind == JsonValueKind.String &&
                    el.GetString() is { Length: > 0 } res)
                {
                    _genResolution = res.Trim().ToLowerInvariant() switch
                    {
                        "480" or "480p" => "480p",
                        "720" or "720p" => "720p",
                        "1080" or "1080p" => "1080p",
                        _ => res.Trim(),
                    };
                }
                if (cfg.TryGetValue("preferred_video_editor", out var edEl) &&
                    edEl.ValueKind == JsonValueKind.String &&
                    edEl.GetString() is { Length: > 0 } pve)
                {
                    _preferredVideoEditor = pve.Trim();
                }
                if (cfg.TryGetValue("audio_model_name", out var amEl) &&
                    amEl.ValueKind == JsonValueKind.String &&
                    amEl.GetString() is { Length: > 0 } am &&
                    !string.Equals(am, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedAudioModel = am.Trim();
                }
            }
        }
        catch { /* keep default */ }
    }


    internal async Task ReloadListAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var dto = await Engine.GetScenesAsync(_projectId);
            _scenes = dto?.Scenes ?? new List<SceneSummary>();
            // Drop selections that no longer exist
            _selected.RemoveWhere(sn => _scenes.All(s => s.SceneNumber != sn));
            if (_selectedScene is int sn)
                await LoadDetailAsync(sn);
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            await RefreshMyJobsAsync();
            await RefreshCastGateAsync();
            await RefreshResolutionLockAsync();
            await RefreshCostEstimateAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _scenes = null;
        }
        finally { _busy = false; }
    }


    /// <summary>
    /// Once a project has on-disk clips at a consistent resolution, lock the resolution
    /// picker to it so a later Regen/batch can't silently mix resolutions in one movie.
    /// </summary>
    internal async Task RefreshResolutionLockAsync()
    {
        try
        {
            _resolutionLock = await Engine.GetResolutionLockAsync(_projectId);
            if (_resolutionLock is { Length: > 0 })
                _genResolution = _resolutionLock;
        }
        catch { /* fail open — leave picker editable */ }
    }


    /// <summary>Refreshes the per-scene cost report at the currently selected generation resolution.</summary>
    internal async Task RefreshCostEstimateAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCostAsync(_projectId, draftResolution: _genResolution, heroResolution: _genResolution);
            _costReport = dto?.Cost;
        }
        catch { _costReport = null; }
    }


    internal double EstimateSelectedCostUsd()
    {
        if (_costReport is null) return 0;
        var sum = 0.0;
        // The end-credits card renders client-side (canvas → ffmpeg.wasm) for free — see
        // StartBatchAsync, which already splits it out of the paid video-model batch. The cost
        // report itself doesn't know that, so exclude it here too or the confirm modal quotes a
        // price for a scene that will never actually be sent to a video model.
        foreach (var row in _costReport.Scenes.Where(r => _selected.Contains(r.Scene) && !IsCreditsSceneNum(r.Scene)))
            sum += row.RemainingDraftUsd;
        return sum;
    }


    internal double? EstimateSceneCostUsd(int sceneNumber)
    {
        var row = _costReport?.Scenes.FirstOrDefault(r => r.Scene == sceneNumber);
        return row?.RemainingDraftUsd;
    }


    /// <summary>
    /// Refresh project-wide cast readiness (voice + locked image for every character).
    /// Soft-fails: if adaptation cannot load, keep previous gate state.
    /// </summary>
    internal async Task RefreshCastGateAsync()
    {
        try
        {
            var adapt = await Engine.GetAdaptationAsync(_projectId);
            var cast = adapt?.Adaptation?.Cast;
            if (cast is null)
            {
                _castChecked = true;
                _castReady = false;
                _castReadyCount = null;
                _castTotal = null;
                _castMissing = new List<string>();
                return;
            }

            _castChecked = true;
            _castReady = cast.ReadyForShots;
            _castReadyCount = cast.Ready;
            _castTotal = cast.Total;
            _castMissing = cast.Missing?.Count > 0
                ? cast.Missing.ToList()
                : new List<string>();
        }
        catch
        {
            // Keep last known; mark checked so UI does not hang open forever
            _castChecked = true;
        }
    }


    internal async Task OpenSceneAsync(int sn)
    {
        _busy = true;
        _error = null;
        _message = null; // clear any leftover completion message from a previous scene/action
        try
        {
            await LoadDetailAsync(sn);
            _selectedScene = sn;
            _selectedClip = null;
            _clip = null;
            _selectedClips.Clear();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }


    internal async Task LoadDetailAsync(int sn)
    {
        var dto = await Engine.GetSceneDetailAsync(_projectId, sn);
        _detail = dto?.Scene
            ?? throw new InvalidOperationException($"Scene {sn} not found");

        _sceneCompositeVideoUrl = null;
        // Resolved once per scene load, not inline in markup — CacheBust() stamps the current
        // second, so calling it inline re-evaluates on every render (any SignalR/job-poll
        // re-render elsewhere on the page) and gives the <video> a new src each time, which
        // makes the browser reload the resource and restart playback — looks like looping.
        _sceneCompositeServerUrl = CacheBust(Engine.CompositeVideoUrl(_projectId, sn));
        if (MediaFolder.IsConnected && _detail.CompositeExists)
        {
            try
            {
                var localBlob = await MediaFolder.GetLocalBlobUrlAsync(_projectId, $"assets/video/scene_{sn:D2}.mp4");
                if (!string.IsNullOrWhiteSpace(localBlob))
                    _sceneCompositeVideoUrl = localBlob;
            }
            catch { /* fallback */ }
        }
    }


    internal async Task BackToListAsync()
    {
        _selectedScene = null;
        _detail = null;
        _selectedClip = null;
        _clip = null;
        _selectedClips.Clear();
        _message = null; // clear any leftover completion message from a previous scene/action
        await ReloadListAsync();
    }


    internal void ToggleSelect(int sn, bool on)
    {
        if (on) _selected.Add(sn);
        else _selected.Remove(sn);
        _selectionMode = "";
    }


    internal void SelectAll()
    {
        _selected = VisibleScenes.Select(s => s.SceneNumber).ToHashSet();
        _selectionMode = "all";
    }


    internal void ClearSelection()
    {
        _selected.Clear();
        _selectionMode = "";
    }


    internal bool AllShownScenesSelected =>
        VisibleScenes.Count > 0 && VisibleScenes.All(s => _selected.Contains(s.SceneNumber));


    internal void ToggleSelectAllShown(bool on)
    {
        if (on) SelectAll();
        else ClearSelection();
    }


    internal static (int W, int H) ResolutionDims(string? res) => (res ?? "").Trim().ToLowerInvariant() switch
    {
        "1080p" => (1920, 1080),
        "480p" => (854, 480),
        _ => (1280, 720),
    };


    internal ClipVersionItem? _selectedCompareVersion =>
        _clipVersions?.FirstOrDefault(v => string.Equals(v.VersionId, _selectedCompareVersionId, StringComparison.OrdinalIgnoreCase));

}
