using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Locations : IDisposable
{

    private string _projectId = "";
    private List<LocationSummary> _locations = new();
    private string? _selectedKey;
    private LocationSummary? _selected;
    private string _editDescription = "";
    private string _editVisualLock = "";
    private string _imageEditInstruction = "";
    private string? _plateUrl;
    private string? _error;
    private string? _message;
    private string? _saveHint;
    private bool _busy;
    private bool _loading = true;
    private CancellationTokenSource? _saveCts;
    private JobSnapshot? _job;
    private CancellationTokenSource? _pollCts;

    protected override async Task OnInitializedAsync()
    {
        ActiveProject.Changed += OnProjectChanged;
        await LoadAsync();
    }

    private void OnProjectChanged() => _ = InvokeAsync(LoadAsync);

    private async Task LoadAsync()
    {
        _error = null;
        _message = null;
        _projectId = ActiveProject.ProjectId ?? "";
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            _locations = new();
            _selected = null;
            _loading = false;
            return;
        }

        _loading = true;
        try
        {
            var dto = await Engine.GetLocationsAsync(_projectId);
            _locations = dto?.Locations ?? new List<LocationSummary>();
            if (TrySelectFromQuery())
            {
                // focused from Film/Script deep link
            }
            else if (!string.IsNullOrWhiteSpace(_selectedKey))
            {
                _selected = _locations.FirstOrDefault(l =>
                    string.Equals(l.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
                if (_selected is not null)
                    ApplySelected(_selected);
            }
            else if (_locations.Count > 0)
            {
                await SelectAsync(_locations[0].Key);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _locations = new();
        }
        finally
        {
            _loading = false;
        }
    }

    private bool TrySelectFromQuery()
    {
        var q = StudioDeepLinks.QueryValue(Nav, "loc");
        if (string.IsNullOrWhiteSpace(q)) return false;
        var match = StudioDeepLinks.MatchLocation(_locations, q);
        if (match is null) return false;
        _selectedKey = match.Key;
        _selected = match;
        ApplySelected(match);
        _imageEditInstruction = "";
        return true;
    }

    private async Task SelectAsync(string key)
    {
        _selectedKey = key;
        _selected = _locations.FirstOrDefault(l =>
            string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
        if (_selected is not null)
            ApplySelected(_selected);
        _imageEditInstruction = "";
        await InvokeAsync(StateHasChanged);
    }

    private void ApplySelected(LocationSummary loc)
    {
        _editDescription = loc.Description ?? "";
        _editVisualLock = loc.VisualLock ?? "";
        _plateUrl = loc.HasPreferred
            ? KeyFormatting.CacheBust(Engine.LocationRefUrl(_projectId, loc.Key))
            : null;
        _saveHint = null;
    }

    private Task OnDescriptionChanged(string value)
    {
        _editDescription = value ?? "";
        ScheduleSave();
        return Task.CompletedTask;
    }

    private Task OnVisualLockChanged(string value)
    {
        _editVisualLock = value ?? "";
        ScheduleSave();
        return Task.CompletedTask;
    }

    private Task OnImageEditChanged(string value)
    {
        _imageEditInstruction = value ?? "";
        return Task.CompletedTask;
    }

    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _saveHint = "Pending…";
        _ = SaveDebouncedAsync(token);
    }

    private async Task SaveDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(800, token);
            if (token.IsCancellationRequested || _selected is null) return;
            _saveHint = "Saving…";
            await InvokeAsync(StateHasChanged);
            await Engine.UpdateLocationLookAsync(
                _projectId, _selected.Key, _editDescription, _editVisualLock, token);
            _selected.Description = _editDescription;
            _selected.VisualLock = _editVisualLock;
            _saveHint = "Saved";
            await InvokeAsync(StateHasChanged);
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            _saveHint = "Save failed";
            _error = ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task StartGenerateAsync()
    {
        if (_selected is null) return;
        if (string.IsNullOrWhiteSpace(_editDescription) && string.IsNullOrWhiteSpace(_editVisualLock))
        {
            _error = "Add a description or visual lock first.";
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        var hasEdit = !string.IsNullOrWhiteSpace(_imageEditInstruction) && _selected.HasPreferred;
        try
        {
            await Engine.StartLocationVariantsAsync(new StartLocationVariantsRequest
            {
                ProjectId = _projectId,
                LocKey = _selected.Key,
                Count = 3,
                DescriptionOverride = _editDescription,
                VisualLockOverride = _editVisualLock,
                ImageEditInstruction = hasEdit ? _imageEditInstruction : null,
                PersistDescription = !hasEdit,
            });
            if (hasEdit)
                _imageEditInstruction = "";
            _message = "Generating set plates…";
            StartJobPoll();
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

    private void StartJobPoll()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = PollJobAsync(token);
    }

    private async Task PollJobAsync(CancellationToken token)
    {
        try
        {
            for (var i = 0; i < 120 && !token.IsCancellationRequested; i++)
            {
                var jobs = await Engine.GetJobAsync(token);
                var j = jobs?.Job;
                if (j is not null &&
                    string.Equals(j.Kind, "location_variants", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(j.ProjectId, _projectId, StringComparison.OrdinalIgnoreCase))
                {
                    _job = j;
                    await InvokeAsync(StateHasChanged);
                    if (j.IsFinished)
                    {
                        if (j.IsSuccess)
                        {
                            _message = j.Message ?? "Set plates ready.";
                            await LoadAsync();
                            if (!string.IsNullOrWhiteSpace(_selectedKey))
                                await SelectAsync(_selectedKey);
                        }
                        else if (!string.IsNullOrWhiteSpace(j.Error))
                            _error = j.Error;
                        return;
                    }
                }
                await Task.Delay(1500, token);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            _error = ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LockVariantAsync(int index)
    {
        if (_selected is null) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await Engine.LockLocationVariantAsync(_projectId, _selected.Key, index);
            _message = $"Locked variant {index}.";
            await LoadAsync();
            await SelectAsync(_selected.Key);
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

    private async Task OnUploadAsync(InputFileChangeEventArgs e)
    {
        if (_selected is null) return;
        var file = e.File;
        if (file is null) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await using var stream = file.OpenReadStream(maxAllowedSize: 12 * 1024 * 1024, cancellationToken: _saveCts?.Token ?? CancellationToken.None);
            await Engine.UploadLocationRefAsync(_projectId, _selected.Key, stream, file.Name);
            _message = "Location plate locked.";
            await LoadAsync();
            await SelectAsync(_selected.Key);
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

    public void Dispose()
    {
        ActiveProject.Changed -= OnProjectChanged;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
    }
}
