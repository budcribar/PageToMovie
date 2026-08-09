using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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

    public bool ShowCopyFeedback { get; set; } = false;
    public string SelectedFileName { get; set; } = "";

    public async Task Close()
    {
        IsOpen = false;
        ShowCopyFeedback = false;
        SelectedFileName = "";
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task Import()
    {
        if (OnImportCallback.HasDelegate && !string.IsNullOrWhiteSpace(FountainText))
        {
            await OnImportCallback.InvokeAsync(FountainText);
        }
        await Close();
    }

    public async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        try
        {
            SelectedFileName = "";
            var file = e.File;
            if (file != null)
            {
                SelectedFileName = file.Name;
                using var stream = file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);
                using var reader = new StreamReader(stream);
                FountainText = await reader.ReadToEndAsync();
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading file: {ex.Message}");
        }
    }

    public async Task CopyText()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", FountainText);
            ShowCopyFeedback = true;
            StateHasChanged();
            await Task.Delay(2000);
            ShowCopyFeedback = false;
            StateHasChanged();
        }
        catch { /* best effort fallback */ }
    }

    public async Task DownloadFile()
    {
        try
        {
            string fileName = "screenplay.fountain";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(FountainText);
            string base64 = Convert.ToBase64String(bytes);

            string jsCode = $@"
                (function() {{
                    var link = document.createElement('a');
                    link.download = '{fileName}';
                    link.href = 'data:text/plain;charset=utf-8;base64,' + '{base64}';
                    document.body.appendChild(link);
                    link.click();
                    document.body.removeChild(link);
                }})();
            ";

            await Js.InvokeVoidAsync("eval", jsCode);
        }
        catch { /* best effort fallback */ }
    }
}
