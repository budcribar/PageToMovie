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

    private string ItemTitle(SceneSummary s)
    {
        var dur = s.ActualDurationSeconds is double ad ? $"{Scenes.FormatClock(ad)} actual" : (s.PlannedDurationSeconds is double pd ? $"~{Scenes.FormatClock(pd)} plan" : "");
        var setting = string.IsNullOrWhiteSpace(s.Setting) ? "No setting" : s.Setting;
        return $"Scene S{s.SceneNumber:D2}: {setting} ({s.ClipsOnDisk}/{s.ClipCount} clips{(!string.IsNullOrEmpty(dur) ? $" · {dur}" : "")})";
    }
}
