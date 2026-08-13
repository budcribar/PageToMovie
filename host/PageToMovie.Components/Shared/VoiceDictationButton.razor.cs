using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PageToMovie.Web.Components;

/// <summary>
/// Mic control that fills a text field via the browser Web Speech API (no server STT required).
/// Parent binds <see cref="Text"/> / <see cref="TextChanged"/> like a two-way string.
/// When <see cref="Suggestions"/> is set, opens a centered coach (try-saying chips + waveform + editable draft).
/// Closing with <c>Use this</c> writes the draft into the field — it does not start generation.
/// </summary>
public partial class VoiceDictationButton : ComponentBase, IAsyncDisposable
{
    private const int WaveBarCount = 12;

    [Inject] private required IJSRuntime Js { get; set; }

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

    private bool CoachMode => Suggestions is { Count: > 0 };

    private bool Supported { get; set; } = true;
    private bool _listening;
    private bool _popoverOpen;
    private string? _error;
    private double _level;
    private string _draft = "";
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

        if (CoachMode)
        {
            _popoverOpen = true;
            _draft = Text ?? "";
        }

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

    private async Task CancelAsync()
    {
        if (_listening)
            await StopAsync();
        _popoverOpen = false;
    }

    private void ApplySuggestionToDraft(string tip)
    {
        _draft = (tip ?? "").Trim();
        _baseBeforeListen = _draft;
    }

    private void OnDraftInput(ChangeEventArgs e)
    {
        _draft = e.Value?.ToString() ?? "";
        _baseBeforeListen = _draft;
    }

    private async Task UseDraftAsync()
    {
        if (_listening)
            await StopAsync();
        var text = (_draft ?? "").Trim();
        _popoverOpen = false;
        if (string.IsNullOrEmpty(text))
            return;
        Text = text;
        await TextChanged.InvokeAsync(text);
    }

    [JSInvokable]
    public async Task OnDictationPartial(string fieldId, string text)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return;
        var merged = Merge(_baseBeforeListen, text);
        _draft = merged;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnDictationEnd(string fieldId, string finalText)
    {
        if (!string.Equals(fieldId, FieldId, StringComparison.Ordinal)) return;
        _listening = false;
        _level = 0;
        if (!string.IsNullOrWhiteSpace(finalText))
            _draft = Merge(_baseBeforeListen, finalText);
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
        public bool Ok { get; set; } = false;
        public string? Error { get; set; } = null;
    }
}
