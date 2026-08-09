using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ScreenplayEditorApp.Components;

public partial class ScreenplayEditor_FountainModal
{
    [Inject]
    public IJSRuntime Js { get; set; } = default!;

    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public string Mode { get; set; } = "import"; // import | export

    [Parameter]
    public string FountainText { get; set; } = "";

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public EventCallback<string> OnImportCallback { get; set; }

    public async Task Close()
    {
        IsOpen = false;
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task Import()
    {
        if (OnImportCallback.HasDelegate)
        {
            await OnImportCallback.InvokeAsync(FountainText);
        }
        await Close();
    }

    public async Task CopyText()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", FountainText);
        }
        catch { /* best effort fallback */ }
    }
}
