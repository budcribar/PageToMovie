using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PageToMovie.Web.Components;

/// <summary>
/// Mic control that fills a text field via the browser Web Speech API (no server STT required).
/// Parent binds <see cref="Text"/> / <see cref="TextChanged"/> like a two-way string.
/// </summary>
public partial class VoiceDictationButton : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = null!;

    /// <summary>Current field value (appended or replaced with spoken text).</summary>
    [Parameter] public string Text { get; set; } = "";

    [Parameter] public EventCallback<string> TextChanged { get; set; }

    /// <summary>When true, spoken text replaces the field; otherwise appends with a space.</summary>
    [Parameter] public bool Replace { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string? TestId { get; set; }

    [Parameter] public string? Lang { get; set; }

    /// <summary>Optional stable id when several mics share a page.</summary>
    [Parameter] public string FieldId { get; set; } = "dictation";

    private bool Supported { get; set; } = true;
    private bool _listening;
    private string? _error;
    private DotNetObjectReference<VoiceDictationButton>? _self;
    private string _baseBeforeListen = "";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            Supported = await Js.InvokeAsync<bool>("ptmDictation.isSupported");
        }
        catch
        {
            // Script not loaded yet or old browser
            try
            {
                Supported = await Js.InvokeAsync<bool>("isDictationSupported");
            }
            catch
            {
                Supported = false;
            }
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleAsync()
    {
        _error = null;
        if (_listening)
        {
            await StopAsync();
            return;
        }

        _self?.Dispose();
        _self = DotNetObjectReference.Create(this);
        _baseBeforeListen = Replace ? "" : (Text ?? "").TrimEnd();
        try
        {
            var result = await Js.InvokeAsync<DictationStartResult>(
                "ptmDictation.start", _self, FieldId, Lang);
            if (result is null || !result.Ok)
            {
                _error = result?.Error ?? "Could not start microphone.";
                return;
            }
            _listening = true;
        }
        catch (JSException ex)
        {
            _error = ex.Message;
        }
    }

    private async Task StopAsync()
    {
        try { await Js.InvokeVoidAsync("ptmDictation.stop"); }
        catch { /* ignore */ }
        _listening = false;
    }

    [JSInvokable]
    public async Task OnDictationPartial(string fieldId, string text)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return;
        var merged = Merge(_baseBeforeListen, text);
        Text = merged;
        await TextChanged.InvokeAsync(merged);
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnDictationEnd(string fieldId, string finalText)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return;
        _listening = false;
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            var merged = Merge(_baseBeforeListen, finalText);
            Text = merged;
            await TextChanged.InvokeAsync(merged);
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnDictationError(string fieldId, string error)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return;
        _listening = false;
        _error = error switch
        {
            "not-allowed" => "Microphone permission denied.",
            "no-speech" => "No speech heard — try again.",
            "aborted" => null,
            _ => error,
        };
        await InvokeAsync(StateHasChanged);
    }

    private static string Merge(string baseText, string spoken)
    {
        spoken = (spoken ?? "").Trim();
        if (string.IsNullOrEmpty(spoken)) return baseText ?? "";
        if (string.IsNullOrWhiteSpace(baseText)) return spoken;
        return baseText.TrimEnd() + " " + spoken;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listening)
        {
            try { await Js.InvokeVoidAsync("ptmDictation.stop"); } catch { /* ignore */ }
        }
        _self?.Dispose();
    }

    private sealed class DictationStartResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }
}
