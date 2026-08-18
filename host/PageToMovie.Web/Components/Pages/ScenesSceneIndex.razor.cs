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
            if (Host.List._selected.Count == 0)
                return "Select one or more scenes first";
            return "Selected scenes have no clips or composites to play yet";
        }
    }

    private string VerifyDialogueTitle
    {
        get
        {
            if (Dialogue?.SelectedScenesHaveClipsToVerify == true)
                return "Check the spoken words in each finished clip against the screenplay";
            if (Host.List._selected.Count == 0)
                return "Select one or more scenes with finished clips first";
            return "Selected scenes have no finished clips to check yet";
        }
    }

    internal string _filterText = "";

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

    private static string ItemTitle(SceneSummary s)
    {
        var dur = FormatSceneDuration(s);
        var setting = string.IsNullOrWhiteSpace(s.Setting) ? "No setting" : s.Setting;
        var durSuffix = !string.IsNullOrEmpty(dur) ? $" · {dur}" : "";
        return $"Scene S{s.SceneNumber:D2}: {setting} ({s.ClipsOnDisk}/{s.ClipCount} clips{durSuffix})";
    }
}
