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

// Forwarders: ScenesListState → Host.*
public partial class Scenes
{
    internal void ToggleSort(string column) => List.ToggleSort(column);

    internal IEnumerable<SceneSummary> SortedVisibleScenes => List.SortedVisibleScenes;

    internal (string Glyph, string Css, string Title) SceneProgressGlyph(SceneSummary s) => List.SceneProgressGlyph(s);

    internal void ClearFilters() => List.ClearFilters();

    internal IEnumerable<SceneSummary> FilteredScenes => List.FilteredScenes;

    internal void SelectByCharacter() => List.SelectByCharacter();

    internal void SelectByLocation() => List.SelectByLocation();

    internal void SelectMissingScenes() => List.SelectMissingScenes();

    internal void RequestDeleteScene(int sn) => List.RequestDeleteScene(sn);

    internal Task ConfirmDeleteSceneAsync() => List.ConfirmDeleteSceneAsync();

    internal Task AddSceneAsync(bool credits) => List.AddSceneAsync(credits);

    internal Task RebuildShotPlanAsync() => List.RebuildShotPlanAsync();

    internal Task LoadGenResolutionFromConfigAsync() => List.LoadGenResolutionFromConfigAsync();

    internal Task ReloadListAsync() => List.ReloadListAsync();

    internal Task RefreshResolutionLockAsync() => List.RefreshResolutionLockAsync();

    internal Task RefreshCostEstimateAsync() => List.RefreshCostEstimateAsync();

    internal double EstimateSelectedCostUsd() => List.EstimateSelectedCostUsd();

    internal double? EstimateSceneCostUsd(int sceneNumber) => List.EstimateSceneCostUsd(sceneNumber);

    internal Task RefreshCastGateAsync() => List.RefreshCastGateAsync();

    internal Task OpenSceneAsync(int sn) => List.OpenSceneAsync(sn);

    internal Task LoadDetailAsync(int sn) => List.LoadDetailAsync(sn);

    internal Task BackToListAsync() => List.BackToListAsync();

    internal void ToggleSelect(int sn, bool on) => List.ToggleSelect(sn, on);

    internal void SelectAll() => List.SelectAll();

    internal void ClearSelection() => List.ClearSelection();

    internal void ToggleSelectAllShown(bool on) => List.ToggleSelectAllShown(on);

    internal static (int W, int H) ResolutionDims(string? res) => ScenesListState.ResolutionDims(res);



    internal bool ResolutionLocked => List.ResolutionLocked;
    internal bool CastReady => List.CastReady;
    internal string CastBlockedTitle => List.CastBlockedTitle;
    internal bool SelectedLockedByOther => List.SelectedLockedByOther;
    internal string SelectionMode => List.SelectionMode;
    internal bool HasActiveFilters => List.HasActiveFilters;
    internal List<SceneSummary> VisibleScenes => List.VisibleScenes;
    internal bool AllShownScenesSelected => List.AllShownScenesSelected;
    internal ClipVersionItem? _selectedCompareVersion => List._selectedCompareVersion;

}
