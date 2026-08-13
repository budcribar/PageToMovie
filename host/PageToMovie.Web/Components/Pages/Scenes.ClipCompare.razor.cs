using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_ClipCompare
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public int ClipNumber { get; set; }
    [Parameter] public string? Message { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public bool Promoting { get; set; }
    [Parameter] public IReadOnlyList<ClipVersionItem>? Versions { get; set; }
    [Parameter] public IReadOnlyList<ClipVersionItem>? TrashVersions { get; set; }
    [Parameter] public IReadOnlyDictionary<string, string?>? VideoUrls { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> Promote { get; set; }
    [Parameter] public EventCallback<string> Delete { get; set; }
    [Parameter] public EventCallback<string> Restore { get; set; }
    [Parameter] public EventCallback EmptyTrash { get; set; }

    private bool GridView { get; set; } = true;
    private bool ShowTrashBin;
    private bool ShowEmptyTrashConfirm;
    private string? SelectedCompareVersionId;

    private ClipVersionItem? SelectedCompareVersion =>
        Versions?.FirstOrDefault(v => string.Equals(v.VersionId, SelectedCompareVersionId, StringComparison.OrdinalIgnoreCase));

    private string? VideoUrlFor(ClipVersionItem v) =>
        VideoUrls is not null && VideoUrls.TryGetValue(v.VersionId, out var u) ? u : null;
}
