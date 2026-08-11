using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components;

public partial class FilmLengthCard : IDisposable
{
    [Parameter] public string ProjectId { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    /// <summary>Render the control flat (no card wrapper) so a host page can group it in one card.</summary>
    [Parameter] public bool Embedded { get; set; }

    [Inject] private StudioUserPrefsService UserPrefs { get; set; } = null!;

    internal readonly string _inputId = "film-target-" + Guid.NewGuid().ToString("N")[..8];
    internal string? _loadedFor;

    internal bool _visible;
    internal bool _busy;
    internal int _natural = 1;
    internal int _target = 1;
    internal int _edit = 1;
    internal string? _error;
    internal CancellationTokenSource? _saveCts;
    internal bool _saving; // a save loop is in flight (coalesces rapid bumps)
    private bool _appliedUserRuntimePref;

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            _visible = false;
            _loadedFor = null;
            return;
        }
        if (string.Equals(_loadedFor, ProjectId, StringComparison.OrdinalIgnoreCase) && _visible)
            return;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        _error = null;
        try
        {
            var dto = await Engine.GetFilmRuntimeAsync(ProjectId, _saveCts?.Token ?? CancellationToken.None);
            _loadedFor = ProjectId;
            // Length is only meaningful after the book is imported / prepared.
            if (dto is null || !dto.Ok || !dto.HasBookText || dto.NaturalMinutes <= 0)
            {
                _visible = false;
                return;
            }
            _natural = Math.Clamp(dto.NaturalMinutes, 1, 180);
            // Project target wins when set; otherwise G1 lastRuntimeTargetMin prefill, then natural.
            if (dto.TargetMinutes > 0)
            {
                _target = Math.Clamp(dto.TargetMinutes, 1, 180);
            }
            else
            {
                _target = _natural;
                await TryApplyUserRuntimePrefAsync();
            }
            _edit = _target;
            _visible = true;
        }
        catch (Exception ex)
        {
            _loadedFor = ProjectId;
            _visible = false;
            _error = ex.Message;
        }
    }

    /// <summary>
    /// C6/G1: when the project has no saved runtime target, seed from the user's last choice.
    /// Applies once per project load; persists so estimate uses the same minutes.
    /// </summary>
    private async Task TryApplyUserRuntimePrefAsync()
    {
        if (_appliedUserRuntimePref) return;
        _appliedUserRuntimePref = true;
        try
        {
            if (!UserPrefs.Loaded)
                await UserPrefs.LoadAsync();
            var pref = UserPrefs.LastRuntimeTargetMin;
            if (pref is not int mins || mins < 1 || mins > 180)
                return;
            if (mins == _natural)
            {
                _target = mins;
                _edit = mins;
                return;
            }
            _edit = mins;
            _target = mins;
            // Persist as project target so CostReport and other surfaces agree
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var saved = await Engine.SetFilmRuntimeAsync(ProjectId, mins, cts.Token);
            if (saved is { Ok: true } && saved.TargetMinutes > 0)
                _target = Math.Clamp(saved.TargetMinutes, 1, 180);
            _edit = _target;
            if (OnChanged.HasDelegate)
            {
                try { await OnChanged.InvokeAsync(); } catch { /* host owns errors */ }
            }
        }
        catch
        {
            // Pref is optional — leave natural
            _target = _natural;
            _edit = _natural;
        }
    }

    /// <summary>Debounced autosave after the user edits the target.</summary>
    internal void ScheduleAutosave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var ct = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, ct);
            }
            catch (OperationCanceledException) { return; /* superseded by a newer edit */ }
            await InvokeAsync(SaveAsync);
        }, ct);
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        if (_saving) return; // a save loop is already running; it will pick up the latest edit
        _saving = true;
        try
        {
            // Save until the persisted target matches the latest edit — this coalesces rapid
            // bumps (each edit reschedules, but we never run two saves at once).
            while (true)
            {
                var minutes = Math.Clamp(_edit, 1, 180);
                if (minutes == _target) break; // up to date
                _busy = true;
                _error = null;
                StateHasChanged();
                try
                {
                    // Hard timeout so a stalled request can never leave the control stuck on "Saving…".
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var dto = await Engine.SetFilmRuntimeAsync(ProjectId, minutes, cts.Token);
                    if (dto is null || !dto.Ok)
                        throw new InvalidOperationException("Save failed.");
                    _natural = dto.NaturalMinutes > 0 ? dto.NaturalMinutes : _natural;
                    _target = dto.TargetMinutes > 0 ? dto.TargetMinutes : minutes;
                    try { await UserPrefs.SetLastRuntimeTargetMinAsync(_target); } catch { /* soft */ }
                }
                catch (OperationCanceledException)
                {
                    _error = "Saving is taking too long — check your connection and try again.";
                    break;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    break;
                }
                finally
                {
                    _busy = false;
                    StateHasChanged();
                }
            }
        }
        finally
        {
            _saving = false;
        }

        // Refresh the estimate OUTSIDE the busy/save lock: OnChanged (the host page's reload) does its
        // own network calls, and it must never be able to freeze this control if it stalls.
        if (_error is null && OnChanged.HasDelegate)
        {
            try { await OnChanged.InvokeAsync(); }
            catch { /* the host owns its own error surface */ }
        }
    }

    internal async Task UseEstimateAsync()
    {
        _edit = _natural;
        await SaveAsync();
    }

    public void Dispose()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
    }
}
