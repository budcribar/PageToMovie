using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_FountainModal
{
    [Inject]
    public IJSRuntime Js { get; set; } = default!;

    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public string Mode { get; set; } = "import";

    [Parameter]
    public string FountainText { get; set; } = "";

    [Parameter]
    public byte[]? PdfBytes { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public EventCallback<string> OnImportCallback { get; set; }

    public bool ShowCopyFeedback { get; set; } = false;
    public string SelectedFileName { get; set; } = "";

    private string ModalTitle
    {
        get
        {
            if (Mode == "import") return "📥 Import Fountain Screenplay";
            if (Mode == "export-pdf") return "📤 Export Screenplay PDF";
            return "📤 Export Fountain Screenplay";
        }
    }

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
                using var stream = file.OpenReadStream(maxAllowedSize: 8_000_000);
                using var reader = new StreamReader(stream);
                FountainText = await reader.ReadToEndAsync();
                await Import(); // 1-Click Instant Import!
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
            string fileName;
            byte[] bytes;
            string mime;
            if (Mode == "export-pdf" && PdfBytes != null)
            {
                fileName = "screenplay.pdf";
                bytes = PdfBytes;
                mime = "application/pdf";
            }
            else
            {
                fileName = "screenplay.fountain";
                bytes = System.Text.Encoding.UTF8.GetBytes(FountainText);
                mime = "text/plain;charset=utf-8";
            }
            string base64 = Convert.ToBase64String(bytes);

            string jsCode = $@"
                (function() {{
                    var link = document.createElement('a');
                    link.download = '{fileName}';
                    link.href = 'data:{mime};base64,' + '{base64}';
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
