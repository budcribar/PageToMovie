using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ScenesSceneIndex : PageSliceComponent
{
    [CascadingParameter] public required Scenes Host { get; set; }
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }
    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }
    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }
    [CascadingParameter] public Scenes.ScenesHistory? History { get; set; }
    [CascadingParameter] public Scenes.ScenesDialogueVerify? Dialogue { get; set; }
    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    private string PlaySelectedTitle
    {
        get
        {
            if (Playback?.CanPlaySelected == true)
                return "Stitch selected scenes in the browser (composites or clips)";
            return Playback?.PlaySelectedDisabledReason
                ?? (Host.List._selected.Count == 0
                    ? "Select one or more scenes first"
                    : "Selected scenes have no clips or composites to play yet");
        }
    }

    private string VerifyDialogueTitle
    {
        get
        {
            if (Dialogue?.SelectedScenesHaveClipsToVerify == true)
                return "Check each finished clip against the screenplay";
            if (Host.List._selected.Count == 0)
                return "Select one or more scenes with finished clips first";
            return "Selected scenes have no finished clips to check yet";
        }
    }

    internal string _filterText = "";

    // ---- drag-and-drop scene reorder (renumber-on-drop: files + blueprint + screenplay) -------

    private int? _dragScene;

    /// <summary>Reorder needs the FULL list visible in plan order — off while filtered or busy.</summary>
    private bool CanDragScenes =>
        string.IsNullOrWhiteSpace(_filterText)
        && Host.List is { HasActiveFilters: false }
        && !Host._busy
        && Host.Gen is { JobRunning: false };

    private void HandleSceneDragStart(int sn) => _dragScene = sn;

    private async Task HandleSceneDropAsync(int targetSn)
    {
        var drag = _dragScene;
        _dragScene = null;
        if (drag is not int dragSn || dragSn == targetSn || !CanDragScenes) return;
        var list = ListState ?? Host.List;
        if (list._scenes is not { Count: > 1 } all) return;

        var order = all.OrderBy(s => s.SceneNumber).Select(s => s.SceneNumber).ToList();
        var from = order.IndexOf(dragSn);
        var to = order.IndexOf(targetSn);
        if (from < 0 || to < 0) return;
        order.RemoveAt(from);
        order.Insert(to, dragSn);

        Host._busy = true;
        Host._error = null;
        StateHasChanged();
        try
        {
            var (ok, error) = await Engine.ReorderScenesAsync(Host._projectId, order);
            if (!ok)
            {
                Host._error = error ?? "Reorder failed.";
                return;
            }
            list._selected.Clear();
            ClipSel?._selectedClips.Clear();
            await MediaFolder.ApplyServerRenamesAsync(Host._projectId);
            await list.ReloadListAsync();
            if (list._selectedScene is int openSn)
            {
                // Scene numbers changed under the open detail — follow the dragged scene's new home.
                var newSn = order.IndexOf(dragSn) + 1;
                await list.OpenSceneAsync(openSn == dragSn ? newSn : Math.Min(order.Count, openSn));
            }
        }
        catch (Exception ex)
        {
            Host._error = ex.Message;
        }
        finally
        {
            Host._busy = false;
            StateHasChanged();
        }
    }

    internal IEnumerable<SceneSummary> FilteredScenes
    {
        get
        {
            var list = ListState?.GetVisibleScenes() ?? Enumerable.Empty<SceneSummary>();
            if (string.IsNullOrWhiteSpace(_filterText))
                return list;

            var term = _filterText.Trim();
            return list.Where(s =>
                s.SceneNumber.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ||
                $"S{s.SceneNumber:D2}".Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (s.Setting != null && s.Setting.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                s.CharactersOnScreen.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (s.PrimaryLocationId != null && s.PrimaryLocationId.Contains(term, StringComparison.OrdinalIgnoreCase))
            );
        }
    }

    private static string SceneBadgeClass(SceneSummary s)
    {
        if (s.ClipsComplete) return "bg-success text-white";
        if (s.ClipsOnDisk > 0) return "bg-warning text-dark";
        return "bg-secondary text-light";
    }

    private static string FormatSceneDuration(SceneSummary s)
    {
        if (s.ActualDurationSeconds is double ad)
            return $"{Scenes.FormatClock(ad)} actual";
        if (s.PlannedDurationSeconds is double pd)
            return $"~{Scenes.FormatClock(pd)} plan";
        return "";
    }

    /// <summary>
    /// Show a join only when the next visible row is the next scene in film order
    /// (hide when a filter skips a neighbor).
    /// </summary>
    internal static bool ShouldShowJoin(Scenes.ScenesListState list, IReadOnlyList<SceneSummary> shown, int index)
    {
        if (index < 0 || index + 1 >= shown.Count)
            return false;
        var current = shown[index].SceneNumber;
        var nextVisible = shown[index + 1].SceneNumber;
        var nextInFilm = list._scenes?
            .Where(s => s.SceneNumber > current)
            .OrderBy(s => s.SceneNumber)
            .Select(s => s.SceneNumber)
            .FirstOrDefault() ?? 0;
        return nextInFilm > 0 && nextInFilm == nextVisible;
    }

    private static string ItemTitle(SceneSummary s)
    {
        var dur = FormatSceneDuration(s);
        var setting = string.IsNullOrWhiteSpace(s.Setting) ? "No setting" : s.Setting;
        var durSuffix = !string.IsNullOrEmpty(dur) ? $" · {dur}" : "";
        return $"Scene S{s.SceneNumber:D2}: {setting} ({s.ClipsOnDisk}/{s.ClipCount} clips{durSuffix})";
    }
}
