using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;
using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminBrowserRenderingSection : PageSliceComponent
{
    private const string GetConfigJs = "PageToMovieCut.getFfmpegWorkerConfig";
    private const string SetSceneWorkersJs = "PageToMovieCut.setFfmpegWorkerCount";
    private const string SetTransitionWorkersJs = "PageToMovieCut.setFfmpegStitchWorkerCount";
    private const string SetClipWorkersJs = "PageToMovieCut.setFfmpegClipWorkerCount";
    private const string ClearCacheJs = "PageToMovieCut.clearSharedStitchCache";

    [Inject] public IJSRuntime Js { get; set; } = default!;
    [CascadingParameter] public Admin Host { get; set; } = default!;
    [CascadingParameter] public Admin.AdminUi Ui { get; set; } = default!;

    private bool _loading = true;
    private string? _error;
    private string? _status;
    private int _minWorkers = 1;
    private int _maxWorkers = 4;
    private int _sceneWorkers = 1;
    private int _transitionWorkers = 1;
    private int _clipWorkers = 1;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        await LoadAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadAsync()
    {
        try
        {
            var config = await Js.InvokeAsync<WorkerConfig>(GetConfigJs);
            _minWorkers = Math.Max(1, config.Min);
            _maxWorkers = Math.Max(_minWorkers, config.Max);
            _sceneWorkers = Clamp(config.Requested);
            _transitionWorkers = Clamp(config.StitchRequested);
            _clipWorkers = Clamp(config.ClipRequested);
            _error = null;
        }
        catch (JSException ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private RenderFragment WorkerOptions() => builder =>
    {
        var sequence = 0;
        for (var count = _minWorkers; count <= _maxWorkers; count++)
        {
            builder.OpenElement(sequence++, "option");
            builder.AddAttribute(sequence++, "value", count.ToString(CultureInfo.InvariantCulture));
            builder.AddContent(sequence++, count == 1 ? "1 (safe default)" : count.ToString(CultureInfo.InvariantCulture));
            builder.CloseElement();
        }
    };

    private Task SetSceneWorkersAsync(ChangeEventArgs args) =>
        SetWorkersAsync(args, SetSceneWorkersJs, value => _sceneWorkers = value);

    private Task SetTransitionWorkersAsync(ChangeEventArgs args) =>
        SetWorkersAsync(args, SetTransitionWorkersJs, value => _transitionWorkers = value);

    private Task SetClipWorkersAsync(ChangeEventArgs args) =>
        SetWorkersAsync(args, SetClipWorkersJs, value => _clipWorkers = value);

    private async Task SetWorkersAsync(ChangeEventArgs args, string jsMethod, Action<int> apply)
    {
        if (!int.TryParse(args.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
            return;

        try
        {
            var saved = await Js.InvokeAsync<int>(jsMethod, Clamp(requested));
            apply(Clamp(saved));
            _error = null;
            _status = "Saved. The next uncached render will use this setting.";
        }
        catch (JSException ex)
        {
            _error = ex.Message;
            _status = null;
        }
    }

    private async Task ClearCacheAsync()
    {
        try
        {
            await Js.InvokeVoidAsync(ClearCacheJs);
            _error = null;
            _status = "Browser render cache cleared.";
        }
        catch (JSException ex)
        {
            _error = ex.Message;
            _status = null;
        }
    }

    private int Clamp(int value) => Math.Clamp(value, _minWorkers, _maxWorkers);

    private sealed class WorkerConfig
    {
        [JsonPropertyName("requested")]
        public int Requested { get; init; } = 1;

        [JsonPropertyName("stitchRequested")]
        public int StitchRequested { get; init; } = 1;

        [JsonPropertyName("clipRequested")]
        public int ClipRequested { get; init; } = 1;

        [JsonPropertyName("min")]
        public int Min { get; init; } = 1;

        [JsonPropertyName("max")]
        public int Max { get; init; } = 4;
    }
}
