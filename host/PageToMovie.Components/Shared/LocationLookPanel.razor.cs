using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components;

public partial class LocationLookPanel
{
    /// <summary>Static coach chips for the tweak-mic popover (no API cost).</summary>
    internal static readonly IReadOnlyList<string> PlateTweakSuggestions = new[]
    {
        "make the trees taller",
        "warmer late-day light",
        "wider shot of the courtyard",
        "fewer people in the background",
        "wet stone after rain",
        "clearer sky",
    };

    [Parameter] public string Description { get; set; } = "";
    [Parameter] public EventCallback<string> DescriptionChanged { get; set; }
    [Parameter] public string VisualLock { get; set; } = "";
    [Parameter] public EventCallback<string> VisualLockChanged { get; set; }
    [Parameter] public string ImageEditInstruction { get; set; } = "";
    [Parameter] public EventCallback<string> ImageEditInstructionChanged { get; set; }
    [Parameter] public bool ShowImageEditInstruction { get; set; }
    [Parameter] public string? Hint { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }

    private string Prefix => string.IsNullOrWhiteSpace(TestId) ? "loc" : TestId!;
    private string DescFieldId => Prefix + "-desc";
    private string VlockFieldId => Prefix + "-vlock";
    private string ImgEditFieldId => Prefix + "-imgedit";
    private string DescVoiceTestId => Prefix + "-desc-voice";
    private string VlockVoiceTestId => Prefix + "-vlock-voice";
    private string ImgEditVoiceTestId => Prefix + "-imgedit-voice";

    Task OnDescriptionInput(ChangeEventArgs e) =>
        DescriptionChanged.InvokeAsync(e.Value?.ToString() ?? "");

    Task OnVisualLockInput(ChangeEventArgs e) =>
        VisualLockChanged.InvokeAsync(e.Value?.ToString() ?? "");

    Task OnImageEditInput(ChangeEventArgs e) =>
        ImageEditInstructionChanged.InvokeAsync(e.Value?.ToString() ?? "");

    Task OnDescriptionVoice(string value) => DescriptionChanged.InvokeAsync(value ?? "");

    Task OnVisualLockVoice(string value) => VisualLockChanged.InvokeAsync(value ?? "");

    Task OnImageEditVoice(string value) => ImageEditInstructionChanged.InvokeAsync(value ?? "");
}
