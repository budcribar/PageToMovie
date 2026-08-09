using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components;

public partial class VisualMediumCard
{
    [Parameter] public string? ProjectId { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    /// <summary>Save-button label. Callers that re-apply on save (e.g. Look) pass a fuller label.</summary>
    [Parameter] public string SaveLabel { get; set; } = "Save";

    private readonly string _selectId = "vm-" + Guid.NewGuid().ToString("N")[..8];
    private string _edit = "auto";
    private string? _label;
    private string? _message;
    private string? _error;
    private bool _busy;
    private string? _loadedFor;
    private List<MediumOption> _options = DefaultOptions();

    private static List<MediumOption> DefaultOptions() => new()
    {
        new("auto", "Auto (infer from book)"),
        new("photoreal_live_action", "Photoreal / live action"),
        new("illustrated_picture_book", "Picture book / illustrated"),
        new("stylized_3d_animated", "Stylized 3D animation"),
        new("other", "Other / stylized"),
    };

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId) || ProjectId == _loadedFor)
            return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        _error = null;
        try
        {
            var dto = await Engine.GetVisualMediumAsync(ProjectId);
            _loadedFor = ProjectId;
            if (dto?.Options is { Count: > 0 })
                _options = dto.Options.Select(o => new MediumOption(o.Id ?? "", o.Label ?? o.Id ?? "")).ToList();
            _edit = string.IsNullOrWhiteSpace(dto?.VisualMedium) ? "auto" : dto!.VisualMedium!;
            _label = _options.FirstOrDefault(o => o.Id == _edit)?.Label ?? _edit;
        }
        catch (Exception ex)
        {
            _loadedFor = ProjectId;
            _error = ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        _busy = true;
        _message = null;
        _error = null;
        try
        {
            var dto = await Engine.SetVisualMediumAsync(ProjectId, _edit);
            if (dto is null || !dto.Ok)
                throw new InvalidOperationException(dto?.Error ?? "Save failed.");
            _edit = dto.VisualMedium ?? _edit;
            _label = _options.FirstOrDefault(o => o.Id == _edit)?.Label ?? _edit;
            _message = dto.Message ?? "Saved.";
            if (OnChanged.HasDelegate)
                await OnChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private sealed record MediumOption(string Id, string Label);
}
