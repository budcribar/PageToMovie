using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components;

public partial class CharacterLookPanel
{
    /// <summary>Static coach chips for the tweak-mic popover (no API cost).</summary>
    internal static readonly IReadOnlyList<string> FaceTweakSuggestions = new[]
    {
        "make his beard longer",
        "remove the beard",
        "shorter hair",
        "softer light on the face",
        "look a little older",
        "more front-facing",
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

    private string Prefix => string.IsNullOrWhiteSpace(TestId) ? "look" : TestId!;
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
