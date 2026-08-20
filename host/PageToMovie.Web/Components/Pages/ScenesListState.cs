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
    /// <summary>ListState domain for the Scenes page. Owns related UI state and behavior.</summary>
    public sealed class ScenesListState
    {

    private readonly Scenes S;
    public ScenesListState(Scenes host) => S = host;


    internal string _pickSetting = "";


    internal string _pickCharacter = "";


    internal string _pickLocation = "";


    internal bool _showFilters;


    /// <summary>Resolution already used by this project's on-disk clips, if consistent — null when unset.</summary>
    internal string? _resolutionLock;


    internal string _sortBy = "number";


    internal bool _sortAscending = true;


    /// <summary>Cost estimate at the current resolution, refreshed on load and resolution change.</summary>
    internal CostReport? _costReport;


    /// <summary>Project-wide cast gate: every character voice + locked image before video spend.</summary>
    internal bool _castChecked;


    internal bool _castReady;


    internal int? _castReadyCount;


    internal int? _castTotal;


    internal List<string> _castMissing = new();


    internal List<SceneSummary>? _scenes;

    /// <summary>Shot plan rows or on-disk clips already exist — do not send the user to rebuild.</summary>
    internal bool HasSceneOrClipMedia =>
        !VoiceSubstitutionOverlayGate.IsMissingSceneList(_scenes)
        || (_scenes?.Sum(s => s.ClipsOnDisk) > 0);


    internal HashSet<int> _selected = new();


    internal string _selectionMode = "";


    internal int? _selectedScene;


    internal SceneDetail? _detail;


    internal int? _deleteSceneTarget;


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
            var scenes = GetVisibleScenes();
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



    internal string CastBlockedTitle
    {
        get
        {
            if (_castMissing.Count > 0)
            {
                var ellipsis = _castMissing.Count > 4 ? "…" : "";
                var suffix = ProductionModes.IsDraft(_costReport?.ProductionMode)
                    ? " (draft: plates optional)"
                    : " (+ locked image)";
                return $"Approve voice first: {string.Join(", ", _castMissing.Take(4))}{ellipsis}{suffix}";
            }
            if (ProductionModes.IsDraft(_costReport?.ProductionMode))
                return "Approve voice for speaking cast before generating (draft: plates optional)";
            return "Approve voice + locked image for every character before generating video";
        }
    }

    internal string? GenerateBatchTitle
    {
        get
        {
            if (!CastReady) return CastBlockedTitle;
            if (SelectedLockedByOther) return "Selection includes scenes locked by another user";
            return null;
        }
    }

    internal string GenerateBatchNoun
    {
        get
        {
            var n = _selected.Count;
            if (n <= 0) return "Batch";
            var suffix = n == 1 ? "" : "s";
            return $"{n} scene{suffix}";
        }
    }

    internal string SortArrow(string column)
    {
        if (_sortBy != column) return "⇅";
        return _sortAscending ? "▲" : "▼";
    }

    internal static string ClipsCountClass(SceneSummary s)
    {
        if (s.ClipsComplete) return "text-success";
        if (s.ClipsOnDisk > 0) return "text-warning";
        return "text-muted";
    }



    internal bool SelectedLockedByOther =>
        _scenes is not null &&
        _selected.Any(sn => _scenes.Any(s => s.SceneNumber == sn && s.LockedByOther));



    internal string SelectionMode => _selectionMode;



    // Tri-state progress glyph for a scene's clip generation:
    //   ○ (muted)   nothing generated yet, or no clips planned
    //   ◐ (warning) some clips on disk, not all
    //   ● (success) every planned clip generated
    internal static (string Glyph, string Css, string Title) SceneProgressGlyph(SceneSummary s)
    {
        if (s.ClipCount <= 0)
            return ("○", "text-muted", "No clips planned");
        if (s.ClipsComplete)
            return ("●", "text-success", $"All {s.ClipCount} clips generated");
        if (s.ClipsOnDisk > 0)
            return ("◐", "text-warning", $"{s.ClipsOnDisk} of {s.ClipCount} clips generated");
        return ("○", "text-muted", $"0 of {s.ClipCount} clips generated");
    }

    /// <summary>D6 — movie-level readiness for the Film hub strip.</summary>
    internal MovieReadinessSnapshot MovieReadiness
    {
        get
        {
            if (_scenes is null || _scenes.Count == 0)
                return default;
            var scenes = _scenes.Count;
            var planned = _scenes.Sum(s => s.ClipCount);
            var onDisk = _scenes.Sum(s => s.ClipsOnDisk);
            var missing = Math.Max(0, planned - onDisk);
            var completeScenes = _scenes.Count(s => s.ClipCount > 0 && s.ClipsComplete);
            var partialScenes = _scenes.Count(s => s.ClipsOnDisk > 0 && s.ClipCount > s.ClipsOnDisk);
            var staleClips = _scenes.Sum(s => s.StaleClipCount);
            var staleScenes = _scenes.Count(s => s.HasStaleClips || s.StaleClipCount > 0);
            return new MovieReadinessSnapshot(scenes, planned, onDisk, missing, completeScenes, partialScenes, staleClips, staleScenes);
        }
    }

    internal readonly record struct MovieReadinessSnapshot(
        int Scenes,
        int ClipsPlanned,
        int ClipsOnDisk,
        int ClipsMissing,
        int ScenesComplete,
        int ScenesPartial,
        int StaleClips,
        int StaleScenes);

    internal void SelectStaleScenes()
    {
        _selected.Clear();
        if (_scenes is null) return;
        foreach (var s in _scenes.Where(s => s.HasStaleClips || s.StaleClipCount > 0))
            _selected.Add(s.SceneNumber);
        _selectionMode = _selected.Count > 0 ? "stale" : "";
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
                    string.Equals(Scenes.ShortChar(c), match, StringComparison.OrdinalIgnoreCase)));
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



    internal List<SceneSummary> GetVisibleScenes() => FilteredScenes.ToList();



    internal void SelectByCharacter()
    {
        if (_scenes is null) return;
        if (string.IsNullOrWhiteSpace(_pickCharacter)) return;
        var match = _pickCharacter;
        _selected.Clear();
        foreach (var s in _scenes.Where(s =>
            s.CharactersOnScreen.Any(c =>
                string.Equals(c, match, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Scenes.ShortChar(c), match, StringComparison.OrdinalIgnoreCase))))
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
        foreach (var s in GetVisibleScenes().Where(s => !s.ClipsComplete || s.ClipsOnDisk < s.ClipCount))
            _selected.Add(s.SceneNumber);
        _selectionMode = _selected.Count > 0 ? "missing" : "";
    }



    internal void RequestDeleteScene(int sn) => _deleteSceneTarget = sn;



    internal async Task ConfirmDeleteSceneAsync()
    {
        if (_deleteSceneTarget is not int sn) return;
        _deleteSceneTarget = null;
        S._busy = true;
        S._error = null;
        S._message = null;
        try
        {
            // Persist: remove the scene from the shot plan (blueprint) so it doesn't reappear on reload.
            var res = await S.Engine.DeleteSceneAsync(S._projectId, sn);
            if (!res.Ok)
            {
                S._error = res.Error ?? "Could not delete the scene.";
                return;
            }
            _selected.Remove(sn);
            S._message = res.Message ?? $"Deleted Scene {sn:D2}";
            await S.Gen.SoftReloadAsync();
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



    internal async Task AddSceneAsync(bool credits)
    {
        S._busy = true;
        S._error = null;
        S._message = null;
        try
        {
            var res = await S.Engine.AddSceneAsync(S._projectId, credits);
            if (!res.Ok)
            {
                S._error = res.Error ?? "Could not add the scene.";
                return;
            }
            S._message = res.Message ?? (credits ? "Added credits scene" : $"Added Scene {res.Scene:D2}");
            await S.Gen.SoftReloadAsync();
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



    /// <summary>
    /// Replan from the screenplay — scoped to the checked scenes when any are selected, so editing
    /// the Fountain (e.g. just the title) and regenerating doesn't re-prompt the AI for scenes whose
    /// script text didn't change (Stage2PlannerService merges a scoped replan into the existing
    /// blueprint instead of rebuilding it from scratch). Falls back to every scene — the original
    /// "restore missing scenes" behavior — when nothing is checked.
    /// </summary>
    /// <summary>The "Regenerate Selected Scenes" button: re-plans exactly the checked scenes. Nothing
    /// checked is an error, not a silent full rebuild (that re-planned every scene when the user
    /// forgot to tick one). Select all + Regenerate is the explicit full rebuild.</summary>
    internal async Task RegenerateSelectedScenesAsync()
    {
        if (_selected.Count == 0)
        {
            S._message = null;
            S._error = "No scenes selected — check the scene(s) to re-plan, or Select all to rebuild the whole shot plan.";
            return;
        }
        // Confirm first (same shape as Generate): what is re-planned, what it costs in clips.
        _showReplanConfirm = true;
    }

    internal bool _showReplanConfirm;
    internal void CloseReplanConfirm() => _showReplanConfirm = false;
    internal async Task ConfirmReplanAsync()
    {
        _showReplanConfirm = false;
        await RebuildShotPlanAsync();
    }

    /// <summary>Build / rebuild the shot plan: the checked scenes when any are checked (all checked =
    /// full rebuild), else every scene (first build, auto-build on an empty plan).</summary>
    internal async Task RebuildShotPlanAsync()
    {
        S._busy = true;
        S._error = null;
        S._message = null;
        try
        {
            var scoped = _selected.Count > 0 && _selected.Count < (_scenes?.Count ?? 0);
            await S.Engine.StartStage2Async(new StartStage2Request
            {
                ProjectId = S._projectId,
                Scenes = scoped ? string.Join(",", _selected.OrderBy(x => x)) : "all"
            });
            S._message = scoped
                ? $"Regenerating {_selected.Count} selected scene(s) from the screenplay…"
                : "Rebuilding shot plan from screenplay…";
            await S.Gen.SoftReloadAsync();
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
        }
        finally { S._busy = false; }
    }



    internal async Task LoadGenResolutionFromConfigAsync()
    {
        try
        {
            var dto = await S.Engine.GetConfigAsync(S._projectId);
            if (dto?.Config is { } cfg)
                ApplyPipelineConfigLookups(cfg);
        }
        catch { /* keep default */ }
    }

    private void ApplyPipelineConfigLookups(Dictionary<string, JsonElement> cfg)
    {
        if (TryReadConfigString(cfg, "resolution", out var res))
        {
            S.Gen._genResolution = res.Trim().ToLowerInvariant() switch
            {
                "480" or "480p" => "480p",
                "720" or "720p" => "720p",
                "1080" or "1080p" => "1080p",
                _ => res.Trim(),
            };
        }
        if (TryReadConfigString(cfg, "preferred_video_editor", out var pve))
            S.ClipRegen._preferredVideoEditor = pve.Trim();
        if (TryReadConfigString(cfg, "audio_model_name", out var am) &&
            !string.Equals(am, "none", StringComparison.OrdinalIgnoreCase))
            S.Music._selectedAudioModel = am.Trim();
    }

    private static bool TryReadConfigString(Dictionary<string, JsonElement> cfg, string key, out string value)
    {
        value = "";
        if (!cfg.TryGetValue(key, out var el) ||
            el.ValueKind != JsonValueKind.String ||
            el.GetString() is not { Length: > 0 } s)
            return false;
        value = s;
        return true;
    }



    internal async Task ReloadListAsync()
    {
        S._busy = true;
        S._error = null;
        try
        {
            var dto = await S.Engine.GetScenesAsync(S._projectId);
            _scenes = dto?.Scenes ?? new List<SceneSummary>();
            // Drop selections that no longer exist
            _selected.RemoveWhere(sn => _scenes.All(s => s.SceneNumber != sn));
            if (_selectedScene is null && _scenes.Count > 0)
            {
                await OpenSceneAsync(_scenes[0].SceneNumber);
            }
            else if (_selectedScene is int sn)
            {
                await LoadDetailAsync(sn);
            }
            var jobs = await S.Engine.GetJobAsync();
            S.Gen._job = jobs?.Job;
            await S.Gen.RefreshMyJobsAsync();
            if (_scenes.Count > 0 && S._message?.StartsWith("Rebuilding shot plan", StringComparison.OrdinalIgnoreCase) == true)
                S._message = null;
            await RefreshCastGateAsync();
            await RefreshResolutionLockAsync();
            await RefreshCostEstimateAsync();
        }
        catch (Exception ex)
        {
            S._error = ex.Message;
            // Keep last good list so Film doesn't stick on "Loading scenes…" after a refresh blip.
            _scenes ??= new List<SceneSummary>();
        }
        finally { S._busy = false; }
    }



    /// <summary>
    /// Once a project has on-disk clips at a consistent resolution, lock the resolution
    /// picker to it so a later Regen/batch can't silently mix resolutions in one movie.
    /// </summary>
    internal async Task RefreshResolutionLockAsync()
    {
        try
        {
            _resolutionLock = await S.Engine.GetResolutionLockAsync(S._projectId);
            if (_resolutionLock is { Length: > 0 })
                S.Gen._genResolution = _resolutionLock;
        }
        catch { /* fail open — leave picker editable */ }
    }



    /// <summary>Refreshes the per-scene cost report at the currently selected generation resolution.</summary>
    internal async Task RefreshCostEstimateAsync()
    {
        if (string.IsNullOrEmpty(S._projectId)) return;
        try
        {
            var dto = await S.Engine.GetCostAsync(S._projectId, draftResolution: S.Gen._genResolution, heroResolution: S.Gen._genResolution);
            _costReport = dto?.Cost;
        }
        catch { _costReport = null; }
    }



    internal double EstimateSelectedCostUsd(bool forceAllTakes = false)
    {
        if (_costReport is null || S.ClipSel.EstimateSelectedClips(forceAllTakes) == 0) return 0;
        var sum = 0.0;
        // The end-credits card renders client-side (canvas → ffmpeg.wasm) for free — see
        // StartBatchAsync, which already splits it out of the paid video-model batch. The cost
        // report itself doesn't know that, so exclude it here too or the confirm modal quotes a
        // price for a scene that will never actually be sent to a video model.
        foreach (var row in _costReport.Scenes.Where(r => _selected.Contains(r.Scene) && !S.Gen.IsCreditsSceneNum(r.Scene)))
            sum += forceAllTakes ? row.AllDraftUsd : row.RemainingDraftUsd;
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
            var adapt = await S.Engine.GetAdaptationAsync(S._projectId);
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



    internal bool CanSelectPreviousScene
    {
        get
        {
            if (_selectedScene is not int sn || _scenes is null || _scenes.Count == 0) return false;
            var list = GetVisibleScenes().Select(s => s.SceneNumber).ToList();
            var idx = list.IndexOf(sn);
            return idx > 0;
        }
    }

    internal bool CanSelectNextScene
    {
        get
        {
            if (_selectedScene is not int sn || _scenes is null || _scenes.Count == 0) return false;
            var list = GetVisibleScenes().Select(s => s.SceneNumber).ToList();
            var idx = list.IndexOf(sn);
            return idx >= 0 && idx < list.Count - 1;
        }
    }

    internal async Task SelectPreviousSceneAsync()
    {
        if (_selectedScene is not int sn || _scenes is null) return;
        var list = GetVisibleScenes().Select(s => s.SceneNumber).ToList();
        var idx = list.IndexOf(sn);
        if (idx > 0)
        {
            await OpenSceneAsync(list[idx - 1]);
        }
    }

    internal async Task SelectNextSceneAsync()
    {
        if (_selectedScene is not int sn || _scenes is null) return;
        var list = GetVisibleScenes().Select(s => s.SceneNumber).ToList();
        var idx = list.IndexOf(sn);
        if (idx >= 0 && idx < list.Count - 1)
        {
            await OpenSceneAsync(list[idx + 1]);
        }
    }

    internal async Task OpenSceneAsync(int sn)
    {
        S._busy = true;
        S._error = null;
        S._message = null; // clear any leftover completion message from a previous scene/action
        try
        {
            await LoadDetailAsync(sn);
            _selectedScene = sn;
            S.ClipForm._selectedClip = null;
            S.ClipForm._clip = null;
            S.ClipSel._selectedClips.Clear();

            try
            {
                await S.JS.InvokeVoidAsync("eval", $"document.querySelector('[data-scene-number=\"{sn}\"]')?.scrollIntoView({{ block: 'nearest', behavior: 'smooth' }})");
            }
            catch { /* optional scroll fallback */ }
        }
        catch (Exception ex) { S._error = ex.Message; }
        finally { S._busy = false; }
    }



    internal async Task LoadDetailAsync(int sn)
    {
        var dto = await S.Engine.GetSceneDetailAsync(S._projectId, sn);
        _detail = dto?.Scene
            ?? throw new InvalidOperationException($"Scene {sn} not found");

        S.Playback._sceneCompositeVideoUrl = null;
        // Resolved once per scene load, not inline in markup — CacheBust() stamps the current
        // second, so calling it inline re-evaluates on every render (any SignalR/job-poll
        // re-render elsewhere on the page) and gives the <video> a new src each time, which
        // makes the browser reload the resource and restart playback — looks like looping.
        S.Playback._sceneCompositeServerUrl = Scenes.CacheBust(S.Engine.CompositeVideoUrl(S._projectId, sn));
        if (S.MediaFolder.IsConnected && _detail.CompositeExists)
        {
            try
            {
                var localBlob = await S.MediaFolder.GetLocalBlobUrlAsync(S._projectId, $"assets/video/scene_{sn:D2}.mp4");
                if (!string.IsNullOrWhiteSpace(localBlob))
                    S.Playback._sceneCompositeVideoUrl = localBlob;
            }
            catch { /* fallback */ }
        }

        await LoadSceneFountainAsync(sn);
    }

    internal bool _showFountainDrawer = false;
    internal bool _showRawFountain = false;
    internal string? _fullScreenplayFountainText;
    internal string? _sceneFountainText;
    internal List<PageToMovie.Fountain.FountainParser.Element> _sceneFountainElements = new();

    internal async Task LoadSceneFountainAsync(int sn)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_fullScreenplayFountainText))
            {
                var doc = await S.Engine.GetScreenplayAsync(S._projectId);
                _fullScreenplayFountainText = doc?.Text ?? "";
            }

            if (!string.IsNullOrWhiteSpace(_fullScreenplayFountainText))
            {
                ExtractSceneFountain(sn);
            }
        }
        catch
        {
            _sceneFountainText = null;
            _sceneFountainElements.Clear();
        }
    }

    private void ExtractSceneFountain(int sn)
    {
        _sceneFountainElements.Clear();
        _sceneFountainText = "";
        if (string.IsNullOrWhiteSpace(_fullScreenplayFountainText)) return;

        var parsed = PageToMovie.Fountain.FountainParser.Parse(_fullScreenplayFountainText);
        int currentSceneNumber = 0;
        var sceneElements = new List<PageToMovie.Fountain.FountainParser.Element>();

        foreach (var el in parsed.Elements)
        {
            if (el.Type == PageToMovie.Fountain.FountainParser.ElementType.SceneHeading)
            {
                currentSceneNumber++;
            }

            if (currentSceneNumber == sn)
            {
                sceneElements.Add(el);
            }
            else if (currentSceneNumber > sn)
            {
                break;
            }
        }

        _sceneFountainElements = sceneElements;
        if (sceneElements.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var el in sceneElements)
            {
                switch (el.Type)
                {
                    case PageToMovie.Fountain.FountainParser.ElementType.Character:
                    case PageToMovie.Fountain.FountainParser.ElementType.Parenthetical:
                        sb.AppendLine(el.Text);
                        break;
                    default:
                        sb.AppendLine(el.Text);
                        sb.AppendLine();
                        break;
                }
            }
            _sceneFountainText = sb.ToString().Trim();
        }
    }



    internal async Task BackToListAsync()
    {
        _selectedScene = null;
        _detail = null;
        S.ClipForm._selectedClip = null;
        S.ClipForm._clip = null;
        S.ClipSel._selectedClips.Clear();
        S._message = null; // clear any leftover completion message from a previous scene/action
        await ReloadListAsync();
    }



    internal void ToggleSelect(int sn, bool on)
    {
        if (on) _selected.Add(sn);
        else _selected.Remove(sn);
        _selectionMode = "";
    }

    /// <summary>Checkbox in the scene index: select AND show that scene in the detail (checking S03 while
    /// S02 is open left S02 on screen — surprising). Unchecking leaves the detail alone.</summary>
    internal async Task ToggleSelectAndOpenAsync(int sn, bool on)
    {
        ToggleSelect(sn, on);
        if (on && _selectedScene != sn)
            await OpenSceneAsync(sn);
    }



    internal void SelectAll()
    {
        _selected = GetVisibleScenes().Select(s => s.SceneNumber).ToHashSet();
        _selectionMode = "all";
    }



    internal void ClearSelection()
    {
        _selected.Clear();
        _selectionMode = "";
    }



    internal bool AreAllVisibleSelected()
    {
        var visible = GetVisibleScenes();
        return visible.Count > 0 && visible.All(s => _selected.Contains(s.SceneNumber));
    }



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
        S.ClipVer._clipVersions?.FirstOrDefault(v => string.Equals(v.VersionId, S.ClipVer._selectedCompareVersionId, StringComparison.OrdinalIgnoreCase));


    }
}
