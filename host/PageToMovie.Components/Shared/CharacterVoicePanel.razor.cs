using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class CharacterVoicePanel
{
    [Parameter] public string VoiceLabel { get; set; } = "";
    [Parameter] public EventCallback<string> VoiceLabelChanged { get; set; }
    [Parameter] public string VoiceProfile { get; set; } = "";
    [Parameter] public EventCallback<string> VoiceProfileChanged { get; set; }
    [Parameter] public string? ImagineVoiceId { get; set; }
    [Parameter] public EventCallback<string?> ImagineVoiceIdChanged { get; set; }
    [Parameter] public IReadOnlyList<PresetVoiceEntry> PresetVoices { get; set; } = Array.Empty<PresetVoiceEntry>();
    [Parameter] public bool ShowProfileFields { get; set; } = true;
    [Parameter] public string? PreviewUrl { get; set; }
    [Parameter] public string? Hint { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }

    Task OnLabelInput(ChangeEventArgs e) =>
        VoiceLabelChanged.InvokeAsync(e.Value?.ToString() ?? "");

    Task OnProfileInput(ChangeEventArgs e) =>
        VoiceProfileChanged.InvokeAsync(e.Value?.ToString() ?? "");

    Task OnImagineVoiceChanged(ChangeEventArgs e) =>
        ImagineVoiceIdChanged.InvokeAsync(e.Value?.ToString());

    internal static string VoiceOptionLabel(PresetVoiceEntry voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.Id : voice.DisplayName;
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(voice.Gender)) bits.Add(voice.Gender);
        if (!string.IsNullOrWhiteSpace(voice.Age)) bits.Add(voice.Age);
        return bits.Count == 0 ? name : $"{name} ({string.Join(", ", bits)})";
    }
}
