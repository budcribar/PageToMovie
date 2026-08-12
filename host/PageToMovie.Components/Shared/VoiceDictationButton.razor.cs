using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PageToMovie.Web.Components;

/// <summary>
/// Mic control that fills a text field via the browser Web Speech API (no server STT required).
/// Parent binds <see cref="Text"/> / <see cref="TextChanged"/> like a two-way string.
/// When <see cref="Suggestions"/> is set, opens a coach popover (try-saying chips + waveform).
/// <see cref="OnCommitted"/> fires when the user applies a chip or closes with spoken/typed text
/// so the parent can start the one-shot plate tweak.
/// </summary>
public partial class VoiceDictationButton : ComponentBase, IAsyncDisposable
{
    private const int WaveBarCount = 12;

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

    /// <summary>Visible label next to the mic (default: Dictate).</summary>
    [Parameter] public string Label { get; set; } = "Dictate";

    /// <summary>Hover / title text when not listening.</summary>
    [Parameter] public string Title { get; set; } = "Speak to fill this field";

    /// <summary>
    /// Optional “Try saying…” chips. When non-empty, mic opens a coach popover with
    /// waveform + tips (used for plate/face tweak). Plain dictation when null/empty.
    /// </summary>
    [Parameter] public IReadOnlyList<string>? Suggestions { get; set; }

    /// <summary>Fired with the final instruction when the user applies a chip or closes the coach.</summary>
    [Parameter] public EventCallback<string> OnCommitted { get; set; }

    private bool CoachMode => Suggestions is { Count: > 0 };

    private bool Supported { get; set; } = true;
    private bool _listening;
    private bool _popoverOpen;
    private string? _error;
    private double _level;
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

        // Open coach first so hints are visible even if the mic fails.
        if (CoachMode)
            _popoverOpen = true;

        _self?.Dispose();
        _self = DotNetObjectReference.Create(this);
        _baseBeforeListen = Replace ? "" : (Text ?? "").TrimEnd();
        _level = 0;
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
        _level = 0;
    }

    private Task ClosePopoverAsync() => FinishAsync(commit: true);

    private Task ApplySuggestionAsync(string tip) => FinishAsync(commit: true, overrideText: tip);

    private async Task FinishAsync(bool commit, string? overrideText = null)
    {
        if (_listening)
            await StopAsync();

        var text = (overrideText ?? Text ?? "").Trim();
        _popoverOpen = false;
        if (!commit || string.IsNullOrEmpty(text))
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        Text = text;
        await TextChanged.InvokeAsync(text);
        if (OnCommitted.HasDelegate)
            await OnCommitted.InvokeAsync(text);
        await InvokeAsync(StateHasChanged);
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
        _level = 0;
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
        _level = 0;
        _error = error switch
        {
            "not-allowed" => "Microphone permission denied.",
            "no-speech" => "No speech heard — try again.",
            "aborted" => null,
            _ => error,
        };
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnDictationLevel(string fieldId, double level)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return Task.CompletedTask;
        if (!CoachMode || !_listening) return Task.CompletedTask;
        var clamped = Math.Clamp(level, 0, 1);
        if (Math.Abs(clamped - _level) < 0.04) return Task.CompletedTask;
        _level = clamped;
        return InvokeAsync(StateHasChanged);
    }

    private double WaveHeight(int index)
    {
        if (!_listening)
            return 18 + (index % 3) * 4;

        var phase = (index / (double)(WaveBarCount - 1)) * Math.PI;
        var envelope = 0.35 + 0.65 * Math.Sin(phase);
        var live = 18 + _level * 82 * envelope;
        if (_level < 0.05)
            live = 22 + (index % 2 == 0 ? 10 : 4);
        return Math.Clamp(live, 12, 100);
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
