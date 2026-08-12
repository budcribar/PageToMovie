using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Locations : IDisposable
{

    private string _projectId = "";
    private List<LocationSummary> _locations = new();
    private bool _showUnusedInPlan;
    private string? _selectedKey;

    private IEnumerable<LocationSummary> LocationsForUi =>
        _locations.Where(l => _showUnusedInPlan || l.UsedInPlan);

    private int UnusedInPlanCount =>
        _locations.Count(l => !l.UsedInPlan);

    private int UsedInPlanCount =>
        _locations.Count(l => l.UsedInPlan);

    private int LockedPlateCount =>
        LocationsForUi.Count(l => l.Locked || l.HasPreferred);

    private int NeedPlateCount =>
        LocationsForUi.Count(l => !l.Locked && !l.HasPreferred);

    private string? ListThumbUrl(LocationSummary loc)
    {
        if (loc.HasPreferred || loc.Locked)
        {
            if (loc.PreferredUrl is { Length: > 0 } u)
                return KeyFormatting.CacheBust(Engine.AbsolutizeMediaUrl(u) ?? u);
            return KeyFormatting.CacheBust(Engine.LocationRefUrl(_projectId, loc.Key));
        }
        var v = loc.Variants.Where(x => x.Exists).OrderBy(x => x.Index).FirstOrDefault();
        if (v is not null)
        {
            if (v.Url is { Length: > 0 } vu)
                return KeyFormatting.CacheBust(Engine.AbsolutizeMediaUrl(vu) ?? vu);
            if (v.Index is int vi)
                return KeyFormatting.CacheBust(Engine.LocationVariantUrl(_projectId, loc.Key, vi));
        }
        return null;
    }

    /// <summary>
    /// Which variant tile shows the locked badge (session last-lock, or sole variant).
    /// </summary>
    private bool IsPreferredVariant(int variantIndex)
    {
        if (_selected is null || !(_selected.HasPreferred || _selected.Locked)) return false;
        if (_lastLockedVariantIndex is int last && last == variantIndex) return true;
        var existing = _selected.Variants.Where(x => x.Exists).Select(x => x.Index ?? 0).Where(i => i > 0).ToList();
        if (existing.Count == 1 && existing[0] == variantIndex) return true;
        return false;
    }
    private LocationSummary? _selected;
    private string _editDescription = "";
    private string _editVisualLock = "";
    private string _imageEditInstruction = "";
    internal string? _plateUrl;
    internal string? _error;
    internal string? _message;
    internal string? _saveHint;
    internal bool _busy;
    internal bool _loading = true;
    private CancellationTokenSource? _saveCts;
    internal JobSnapshot? _job;
    private CancellationTokenSource? _pollCts;
    /// <summary>Last variant index locked this session (for lock badge on tiles).</summary>
    private int? _lastLockedVariantIndex;

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
        if (loc.HasPreferred || loc.Locked)
        {
            if (loc.PreferredUrl is { Length: > 0 } u)
                _plateUrl = KeyFormatting.CacheBust(Engine.AbsolutizeMediaUrl(u) ?? u);
            else
                _plateUrl = KeyFormatting.CacheBust(Engine.LocationRefUrl(_projectId, loc.Key));
        }
        else
        {
            _plateUrl = null;
        }
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
                AutoLockBest = true,
            });
            if (hasEdit)
                _imageEditInstruction = "";
            _message = "Generating 3 set looks — AI will lock the best…";
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

    private async Task StartPlanLooksAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _busy) return;
        if (_job is { Status: "running" or "queued" }) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await Engine.StartPlanLooksAsync(new StartPlanLooksRequest
            {
                ProjectId = _projectId,
                Count = 3,
                SkipAlreadyLocked = true,
                IncludeCast = true,
                IncludeLocations = true,
            });
            _message = "Plan looks: cast faces + places · 3 each · AI auto-locks best…";
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
                    (string.Equals(j.Kind, "location_variants", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(j.Kind, "plan_looks", StringComparison.OrdinalIgnoreCase)) &&
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
            _lastLockedVariantIndex = index;
            _message = $"Locked look #{index} as preferred.";
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
            await Engine.UploadLocationRefAsync(_projectId, _selected.Key, stream, file.Name, _saveCts?.Token ?? CancellationToken.None);
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
