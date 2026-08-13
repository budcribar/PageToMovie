using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components;

/// <summary>
/// Shared parameters and tweak-edit handlers for character and location look panels.
/// Derived components keep their own markup, suggestion chips, and default test-id prefix.
/// </summary>
public abstract class LookPanelBase : ComponentBase
{
    [Parameter] public string Description { get; set; } = "";
    [Parameter] public EventCallback<string> DescriptionChanged { get; set; }
    [Parameter] public string VisualLock { get; set; } = "";
    [Parameter] public EventCallback<string> VisualLockChanged { get; set; }
    [Parameter] public string ImageEditInstruction { get; set; } = "";
    [Parameter] public EventCallback<string> ImageEditInstructionChanged { get; set; }
    [Parameter] public bool ShowImageEditInstruction { get; set; }
    [Parameter] public bool ShowDescription { get; set; } = true;
    [Parameter] public string? Hint { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }
    /// <summary>Fired after a chip or Dictate-edit apply — parent should start the one-shot tweak.</summary>
    [Parameter] public EventCallback<string> OnTweakRequested { get; set; }

    /// <summary>Fallback <see cref="TestId"/> when the parent does not pass one.</summary>
    protected abstract string DefaultPrefix { get; }

    protected string Prefix => string.IsNullOrWhiteSpace(TestId) ? DefaultPrefix : TestId;
    protected string ImgEditFieldId => Prefix + "-imgedit";
    protected string ImgEditVoiceTestId => Prefix + "-imgedit-voice";

    protected Task OnDescriptionInput(ChangeEventArgs e) =>
        DescriptionChanged.InvokeAsync(e.Value?.ToString() ?? "");

    protected Task OnVisualLockInput(ChangeEventArgs e) =>
        VisualLockChanged.InvokeAsync(e.Value?.ToString() ?? "");

    protected Task OnImageEditInput(ChangeEventArgs e) =>
        ImageEditInstructionChanged.InvokeAsync(e.Value?.ToString() ?? "");

    protected Task OnImageEditVoice(string value) => ImageEditInstructionChanged.InvokeAsync(value ?? "");

    protected async Task OnTweakCommittedAsync(string instruction)
    {
        instruction = (instruction ?? "").Trim();
        if (string.IsNullOrEmpty(instruction)) return;
        await ImageEditInstructionChanged.InvokeAsync(instruction);
        if (OnTweakRequested.HasDelegate)
            await OnTweakRequested.InvokeAsync(instruction);
    }
}
