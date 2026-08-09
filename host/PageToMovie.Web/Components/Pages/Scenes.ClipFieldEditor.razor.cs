using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_ClipFieldEditor
{
    [Parameter] public ClipEditRequest? Editor { get; set; }
    [Parameter] public bool IsNew { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public IReadOnlyList<string> CharacterOptions { get; set; } = Array.Empty<string>();
    [Parameter] public HashSet<string> CastKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback<(string Key, bool On)> CastToggled { get; set; }

    private bool ShowAdvanced;

    private Task ToggleCast(string key, bool on) => CastToggled.InvokeAsync((key, on));

    private static string FormatChar(string key) => KeyFormatting.ShortChar(key);
}
