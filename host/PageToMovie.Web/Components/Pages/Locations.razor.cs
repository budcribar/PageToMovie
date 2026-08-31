using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Locations : IDisposable
{

    private string _projectId = "";
    private List<LocationSummary> _locations = new();
    private List<CharacterSummary> _characters = new();
    private bool _charactersKnown;
    private bool _showUnusedInPlan = false;
    private string? _selectedKey;

    /// <summary>
    /// Plan locations only, unless the operator asked for everything — or the plan claims none at
    /// all, where filtering would leave the page blank with no hint that locations exist.
    /// </summary>
    private IEnumerable<LocationSummary> LocationsForUi
    {
        get
        {
            var showAll = _showUnusedInPlan || UsedInPlanCount == 0;
            return showAll ? _locations : _locations.Where(l => l.UsedInPlan);
        }
    }

    private int UnusedInPlanCount =>
        _locations.Count(l => !l.UsedInPlan);

    private int UsedInPlanCount =>
        _locations.Count(l => l.UsedInPlan);

    private int LockedPlateCount =>
        LocationsForUi.Count(l => l.Locked || l.HasPreferred);

    private int NeedPlateCount =>
        LocationsForUi.Count(l => !l.Locked && !l.HasPreferred);

    /// <summary>
    /// Hide plan-looks once every used-in-plan face and place is locked (job would be a no-op).
    /// Keep the button visible (disabled) while a plan_looks job is running.
    /// </summary>
    private bool PlanLooksAlreadyDone =>
        _charactersKnown && PlanLooksWork.AllUsedLooksLocked(_characters, _locations);

    private bool PlanLooksJobRunning =>
        _job is { Status: "running" or "queued" } j
        && string.Equals(j.Kind, "plan_looks", StringComparison.OrdinalIgnoreCase);

    private bool ShowPlanLooksButton =>
        PlanLooksJobRunning || !PlanLooksAlreadyDone;

    private async Task RefreshCharactersAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            _characters = new();
            _charactersKnown = true;
            return;
        }

        try
        {
            var chars = await Engine.GetCharactersAsync(_projectId);
            _characters = chars?.Characters ?? new List<CharacterSummary>();
            _charactersKnown = true;
        }
        catch
        {
            _charactersKnown = false;
        }
    }

    private static string ListMeta(LocationSummary loc, bool isLocked)
    {
        if (isLocked) return "Locked";
        var n = loc.Variants.Count(v => v.Exists);
        if (n > 0) return $"{n} looks";
        return "No plate";
    }

    private string? ListThumbUrl(LocationSummary loc)
    {
        if (loc.HasPreferred || (loc.Locked && loc.PreferredUrl is { Length: > 0 }))
        {
            if (loc.PreferredUrl is { Length: > 0 } u)
                return BustMediaUrl(Engine.AbsolutizeMediaUrl(u) ?? u);
            return BustMediaUrl(Engine.LocationRefUrl(_projectId, loc.Key));
        }
        var v = loc.Variants.Where(x => x.Exists).OrderBy(x => x.Index).FirstOrDefault();
        if (v is not null)
        {
            if (v.Url is { Length: > 0 } vu)
                return BustMediaUrl(Engine.AbsolutizeMediaUrl(vu) ?? vu);
            if (v.Index is int vi)
                return BustMediaUrl(Engine.LocationVariantUrl(_projectId, loc.Key, vi));
        }
        return null;
    }

    /// <summary>Same path after a looks job would otherwise keep showing the empty/old tile.</summary>
    internal string BustMediaUrl(string url) =>
        KeyFormatting.CacheBust(url, _plateBust);

    /// <summary>
    /// Which variant tile shows the locked badge (session last-lock, or sole variant).
    /// </summary>
    private bool IsPreferredVariant(int variantIndex)
    {
        if (_selected is null || !(_selected.HasPreferred || _selected.Locked)) return false;
        if (_lastLockedVariantIndex is int last && last == variantIndex) return true;
        // Server-derived: the ref plate is a byte-copy of the chosen variant, so after a reload the
        // locked look is still known (before this, tile #1 showed unlocked while it was the plate).
        if (_selected.PreferredVariantIndex is int pv) return pv == variantIndex;
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
    private string? _polledJobId;
    private string? _appliedTerminalKey;
    /// <summary>True after we started a looks job and before the first snapshot arrives.</summary>
    private bool _expectingLocationJob;
    private bool _hubHooked;
    /// <summary>Bumped when plates land so the same variant URL does not keep an empty/old tile.</summary>
    private long _plateBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>Last variant index locked this session (for lock badge on tiles).</summary>
    private int? _lastLockedVariantIndex;

    [Inject] private JobHubClient Hub { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        ActiveProject.Changed += OnProjectChanged;
        HookJobHub();
        try { await Hub.StartAsync(); } catch { /* poll backup still watches */ }
        await LoadAsync();
        await AdoptInFlightLocationJobAsync();
    }

    // Re-render AFTER the reload finishes: a bare InvokeAsync(LoadAsync) leaves the last render at
    // _loading == true (fire-and-forget completion doesn't re-render), freezing the page on
    // "Loading locations…" whenever readiness refresh fires Changed right after navigation.
    private void OnProjectChanged() => _ = InvokeAsync(async () =>
    {
        StopJobPoll();
        _job = null;
        _appliedTerminalKey = null;
        _expectingLocationJob = false;
        await LoadAsync();
        await AdoptInFlightLocationJobAsync();
        StateHasChanged();
    });

    private async Task LoadAsync(bool clearOperatorCopy = true)
    {
        if (clearOperatorCopy)
        {
            _error = null;
            _message = null;
        }
        _projectId = ActiveProject.ProjectId ?? "";
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            _locations = new();
            _characters = new();
            _charactersKnown = true;
            _selected = null;
            _loading = false;
            return;
        }

        _loading = true;
        try
        {
            var dto = await Engine.GetLocationsAsync(_projectId);
            _locations = dto?.Locations ?? new List<LocationSummary>();
            await RefreshCharactersAsync();

            // Server may have lost plates after deploy/import while the browser media folder still has them.
            if (NeedPlateCount > 0 && MediaFolder.IsConnected)
            {
                var restored = await TryRestorePlatesFromMediaFolderCoreAsync(silent: true);
                if (restored > 0)
                {
                    dto = await Engine.GetLocationsAsync(_projectId);
                    _locations = dto?.Locations ?? new List<LocationSummary>();
                    await RefreshCharactersAsync();
                    _message = $"Restored {restored} set plate(s) from your media folder.";
                }
            }

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
            _charactersKnown = false;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>User-clicked restore — always reports result.</summary>
    private async Task RestorePlatesFromMediaFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            if (!MediaFolder.IsConnected)
            {
                var ok = await MediaFolder.ConnectFolderAsync();
                if (!ok)
                {
                    _error = "Connect your media folder (the Videos/PageToMovie folder) to restore plates.";
                    return;
                }
            }

            var restored = await TryRestorePlatesFromMediaFolderCoreAsync(silent: false);
            var dto = await Engine.GetLocationsAsync(_projectId);
            _locations = dto?.Locations ?? new List<LocationSummary>();
            await RefreshCharactersAsync();
            if (!string.IsNullOrWhiteSpace(_selectedKey))
                await SelectAsync(_selectedKey);
            else if (_locations.Count > 0)
                await SelectAsync(_locations[0].Key);

            if (restored > 0)
                _message = $"Restored {restored} set plate(s) from your media folder onto the server.";
            else if (string.IsNullOrWhiteSpace(_error))
                _message = "No matching *_ref.png files found under assets/locations in the media folder (or all plates already on server).";
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

    /// <summary>
    /// Upload local media-folder plates for locations that have no server preferred image.
    /// Paths match server naming: assets/locations/{key_lower}_ref.png
    /// </summary>
    private async Task<int> TryRestorePlatesFromMediaFolderCoreAsync(bool silent)
    {
        var need = _locations.Where(l => !l.Locked && !l.HasPreferred).ToList();
        if (need.Count == 0) return 0;

        var restored = 0;
        var missingLocal = 0;
        foreach (var locKey in need.Select(loc => loc.Key))
        {
            var rel = await FindLocalLocationRefRelativeAsync(locKey);
            if (rel is null)
            {
                missingLocal++;
                continue;
            }

            var bytes = await MediaFolder.ReadLocalBytesAsync($"{_projectId}/{rel}", minBytes: 64);
            if (bytes is null || bytes.Length < 64)
            {
                missingLocal++;
                continue;
            }

            await using var ms = new MemoryStream(bytes);
            var fileName = Path.GetFileName(rel);
            await Engine.UploadLocationRefAsync(_projectId, locKey, ms, fileName);
            restored++;
        }

        if (!silent && restored == 0 && missingLocal > 0 && string.IsNullOrWhiteSpace(_error))
        {
            // leave message to caller
        }

        return restored;
    }

    /// <summary>Relative path under project (assets/locations/…) if a ref file exists locally.</summary>
    private async Task<string?> FindLocalLocationRefRelativeAsync(string locKey)
    {
        foreach (var name in LocationRefFileNameCandidates(locKey))
        {
            var rel = $"assets/locations/{name}";
            var (found, size) = await MediaFolder.StatLocalFileAsync(_projectId, rel);
            if (found && size >= 64)
                return rel;
        }

        // Fall back to first variant if preferred was never written but variants remain.
        foreach (var name in LocationVariantFileNameCandidates(locKey))
        {
            var rel = $"assets/locations/{name}";
            var (found, size) = await MediaFolder.StatLocalFileAsync(_projectId, rel);
            if (found && size >= 64)
                return rel;
        }
        return null;
    }

    /// <summary>Same naming rules as server ProjectStore.LocationRefFileName (+ Loc_ alias).</summary>
    private static IEnumerable<string> LocationRefFileNameCandidates(string locKey)
    {
        foreach (var stem in LocationKeyStems(locKey))
        {
            yield return $"{stem}_ref.png";
            yield return $"{stem}_ref.jpg";
            yield return $"{stem}_ref.webp";
        }
    }

    private static IEnumerable<string> LocationVariantFileNameCandidates(string locKey)
    {
        foreach (var stem in LocationKeyStems(locKey))
        {
            for (var i = 1; i <= 3; i++)
                yield return $"{stem}_variant_{i:D2}.png";
        }
    }

    private static IEnumerable<string> LocationKeyStems(string locKey)
    {
        var raw = (locKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        raw = Path.GetFileName(raw);
        var k = raw.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(k) || k is "." or "..")
            yield break;

        if (k.EndsWith("_ref.png", StringComparison.OrdinalIgnoreCase))
            k = k[..^"_ref.png".Length];

        yield return k;
        if (k.StartsWith("loc_", StringComparison.Ordinal))
            yield return k["loc_".Length..];
        else
            yield return "loc_" + k;
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
        if (loc.HasPreferred || (loc.Locked && loc.PreferredUrl is { Length: > 0 }))
        {
            if (loc.PreferredUrl is { Length: > 0 } u)
                _plateUrl = BustMediaUrl(Engine.AbsolutizeMediaUrl(u) ?? u);
            else
                _plateUrl = BustMediaUrl(Engine.LocationRefUrl(_projectId, loc.Key));
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

    private Task OnTweakRequestedAsync(string instruction)
    {
        _imageEditInstruction = instruction ?? "";
        return StartGenerateAsync();
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
        catch (TaskCanceledException)
        {
            // Debounce cancelled before save ran.
        }
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
        var hasEdit = !string.IsNullOrWhiteSpace(_imageEditInstruction)
                      && (_selected.HasPreferred || _selected.Locked);
        if (!hasEdit
            && string.IsNullOrWhiteSpace(_editDescription)
            && string.IsNullOrWhiteSpace(_editVisualLock))
        {
            _error = "Add a description first (or type a plate tweak if a plate is already locked).";
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await Engine.StartLocationVariantsAsync(new StartLocationVariantsRequest
            {
                ProjectId = _projectId,
                LocKey = _selected.Key,
                Count = hasEdit ? 1 : 3,
                DescriptionOverride = _editDescription,
                VisualLockOverride = _editVisualLock,
                ImageEditInstruction = hasEdit ? _imageEditInstruction : null,
                // Tweak path must not overwrite description with an empty edit instruction side-effect.
                PersistDescription = !hasEdit,
                AutoLockBest = true,
            });
            if (hasEdit)
                _imageEditInstruction = "";
            _message = hasEdit
                ? "New look is ready next to the current lock — click a lock to keep old or switch."
                : "Generating 3 set looks — AI will lock the best…";

            await WatchStartedLocationJobAsync();
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
            await WatchStartedLocationJobAsync();
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

    private async Task WatchStartedLocationJobAsync()
    {
        _appliedTerminalKey = null;
        _expectingLocationJob = true;
        try { await Hub.StartAsync(); } catch { /* poll backup still watches */ }
        StartJobPoll();
        await RaiseCurrentJobIfTrackedAsync();
    }

    private void HookJobHub()
    {
        if (_hubHooked) return;
        _hubHooked = true;
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        Hub.Reconnected += OnHubReconnected;
    }

    internal void OnJobUpdated(JobSnapshot snap)
    {
        if (!LocationJobWatch.IsTrackedForProject(snap, _projectId))
            return;
        _ = InvokeAsync(async () =>
        {
            await ApplyLocationJobSnapshotAsync(snap);
            StateHasChanged();
        });
    }

    private void OnJobLog(string line)
    {
        if (_job is null || !LocationJobWatch.IsTrackedKind(_job.Kind))
            return;
        _job.Message = line;
        if (_job.Log.Count == 0 || _job.Log[^1] != line)
        {
            _job.Log.Add(line);
            if (_job.Log.Count > 80)
                _job.Log = _job.Log.TakeLast(80).ToList();
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnHubReconnected() => _ = InvokeAsync(async () =>
    {
        await AdoptInFlightLocationJobAsync();
        StateHasChanged();
    });

    private async Task AdoptInFlightLocationJobAsync()
    {
        try
        {
            var snap = (await Engine.GetJobAsync())?.Job;
            if (!LocationJobWatch.IsTrackedForProject(snap, _projectId) || snap is null)
                return;
            await ApplyLocationJobSnapshotAsync(snap);
        }
        catch
        {
            // Hub subscription still covers a later terminal event.
        }
    }

    private async Task RaiseCurrentJobIfTrackedAsync()
    {
        try
        {
            var snap = (await Engine.GetJobAsync())?.Job;
            if (LocationJobWatch.IsTrackedForProject(snap, _projectId))
                Hub.RaiseJobUpdated(snap);
        }
        catch
        {
            // Backup poll will pick it up.
        }
    }

    private void StartJobPoll()
    {
        var jobId = _job?.JobId;
        if (_pollCts is not null
            && (_expectingLocationJob || LocationJobWatch.ShouldWatch(_job, _projectId))
            && (string.IsNullOrWhiteSpace(jobId)
                || string.Equals(_polledJobId, jobId, StringComparison.OrdinalIgnoreCase)))
            return;

        StopJobPoll();
        _polledJobId = jobId;
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = PollJobAsync(token);
    }

    private void StopJobPoll()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _polledJobId = null;
    }

    private void EnsureBackupPoll(JobSnapshot job)
    {
        if (!LocationJobWatch.ShouldWatch(job, _projectId))
            return;
        if (_pollCts is not null
            && string.Equals(_polledJobId, job.JobId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(job.JobId))
            return;
        StartJobPoll();
    }

    /// <summary>
    /// Unbounded backup for a looks run that can last 20–30 minutes. The hub
    /// <see cref="OnJobUpdated"/> path is primary; this loop never stops on a tick cap.
    /// Dispose / a new poll cancels the token so leaving the page does not leak.
    /// </summary>
    private async Task PollJobAsync(CancellationToken token)
    {
        try
        {
            while (LocationJobWatch.ShouldContinuePoll(
                       _job, _projectId, _expectingLocationJob, token.IsCancellationRequested))
            {
                await Task.Delay(LocationJobWatch.BackupPollInterval, token);
                if (token.IsCancellationRequested)
                    return;
                if (!LocationJobWatch.ShouldContinuePoll(
                        _job, _projectId, _expectingLocationJob, cancelled: false))
                    return;

                var snap = (await Engine.GetJobAsync(token))?.Job;
                if (LocationJobWatch.IsTrackedForProject(snap, _projectId))
                    Hub.RaiseJobUpdated(snap);
            }
        }
        catch (OperationCanceledException)
        {
            // Polling stopped (component disposed or a new poll started).
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ApplyLocationJobSnapshotAsync(JobSnapshot j)
    {
        var finish = LocationJobWatch.Classify(j, _projectId);
        if (finish == LocationJobWatch.Finish.Ignore)
            return;

        _job = j;
        _expectingLocationJob = false;
        if (finish == LocationJobWatch.Finish.StillRunning)
        {
            EnsureBackupPoll(j);
            return;
        }

        if (AlreadyApplied(j))
            return;
        MarkApplied(j);
        StopJobPoll();
        await ApplyFinishedLocationJobAsync(j, finish);
    }

    private bool AlreadyApplied(JobSnapshot j) =>
        string.Equals(_appliedTerminalKey, TerminalKey(j), StringComparison.Ordinal);

    private void MarkApplied(JobSnapshot j) =>
        _appliedTerminalKey = TerminalKey(j);

    private static string TerminalKey(JobSnapshot j) =>
        string.IsNullOrWhiteSpace(j.JobId)
            ? $"{j.Kind}|{j.Status}|{j.FinishedAt:O}"
            : $"{j.JobId}|{j.Status}";

    private async Task ApplyFinishedLocationJobAsync(JobSnapshot j, LocationJobWatch.Finish finish)
    {
        if (finish == LocationJobWatch.Finish.ReloadSuccess)
        {
            _plateBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = LocationJobWatch.SuccessBanner(j);
            await LoadAsync(clearOperatorCopy: false);
            if (!string.IsNullOrWhiteSpace(_selectedKey))
                await SelectAsync(_selectedKey);
            _error = null;
            _message = LocationJobWatch.BannerAfterReload(pending, _message);
        }
        else if (finish == LocationJobWatch.Finish.Failed)
        {
            _message = null;
            if (!string.IsNullOrWhiteSpace(j.Error))
                _error = j.Error;
        }
        else if (finish == LocationJobWatch.Finish.Cancelled)
        {
            _error = null;
            _message = "Cancelled.";
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
            _plateBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = $"Locked look #{index} as preferred.";
            await LoadAsync(clearOperatorCopy: false);
            await SelectAsync(_selected.Key);
            _message = LocationJobWatch.BannerAfterReload(pending, _message);
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
            await using var stream = file.OpenReadStream(maxAllowedSize: 8_000_000, cancellationToken: _saveCts?.Token ?? CancellationToken.None);
            await Engine.UploadLocationRefAsync(_projectId, _selected.Key, stream, file.Name, _saveCts?.Token ?? CancellationToken.None);
            _plateBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string pending = "Location plate locked.";
            await LoadAsync(clearOperatorCopy: false);
            await SelectAsync(_selected.Key);
            _message = LocationJobWatch.BannerAfterReload(pending, _message);
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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        ActiveProject.Changed -= OnProjectChanged;
        if (_hubHooked)
        {
            Hub.JobUpdated -= OnJobUpdated;
            Hub.JobLog -= OnJobLog;
            Hub.Reconnected -= OnHubReconnected;
            _hubHooked = false;
        }
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        StopJobPoll();
    }
}
