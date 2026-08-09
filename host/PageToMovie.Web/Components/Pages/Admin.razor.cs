using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    internal AdminStateDto? _state;
    internal string? _error;
    internal string? _actionMsg;
    internal bool _busy;
    internal bool _hubLive;
    internal PeriodicTimer? _timer;
    internal CancellationTokenSource? _pollCts;
    internal int _apiInFlight;
    internal int _capacityRejects;
    internal int _lockConflicts;
    internal List<AdminLockDto> _locks = new();
    internal LoadSimLiveStateDto? _loadSim;
    internal List<ProcessSampleDto> _processHistory = new();
    internal EngineApiClient.TimingTelemetryTrendDto? _timingTelemetry;
    /// <summary>Set when a chart upsert throws after we had real data to draw — surfaced in the UI so failures aren't silent.</summary>
    internal string? _chartWarning;

    internal List<EngineApiClient.GenerationErrorRowDto>? _genErrors;
    internal bool _genErrorsBusy;
    internal string _genErrorTypeFilter = "";
    internal string _genErrorProjectFilter = "";

    internal string? _logJobId;
    internal string _logJobIdInput = "";
    internal JobSnapshot? _jobLog;
    internal string? _logError;

    internal string JobLogText =>
        _jobLog?.Log is { Count: > 0 } lines
            ? string.Join("\n", lines)
            : "(no log lines — job may have finished and been pruned, or never wrote logs)";

    internal List<string> _projectOptions = new();
    internal List<string> _userList = new();
    internal string _exportProjectId = "";
    internal string _augmentProjectId = "";
    internal string _importPreferredId = "";
    internal string _importTargetUserId = "";
    internal bool _importOverwrite;
    internal IBrowserFile? _importFile;
    internal bool _archiveBusy;
    internal string? _archiveAction;
    internal string? _archiveMsg;
    internal string? _archiveError;
    internal const long MaxImportBytes = 512L * 1024 * 1024;

    internal bool _showTestEmailModal;

    internal void OpenTestEmailModal() => _showTestEmailModal = true;

    internal void CloseTestEmailModal() => _showTestEmailModal = false;

    internal bool _showJobsAndLocks = true;
    internal bool _showProjectArchiving = true;
    internal bool _showLoadSim = false;
    internal bool _showTimingTelemetry = false;
    internal bool _showGenErrors = false;
    internal bool _showStorageAndCapacity = false;

    internal void ToggleJobsAndLocks() => _showJobsAndLocks = !_showJobsAndLocks;
    internal void ToggleProjectArchiving() => _showProjectArchiving = !_showProjectArchiving;
    internal void ToggleLoadSim() => _showLoadSim = !_showLoadSim;
    internal void ToggleTimingTelemetry() => _showTimingTelemetry = !_showTimingTelemetry;
    internal void ToggleGenErrors() => _showGenErrors = !_showGenErrors;
    internal void ToggleStorageAndCapacity() => _showStorageAndCapacity = !_showStorageAndCapacity;

    internal void ExpandAllCards()
    {
        _showJobsAndLocks = true;
        _showProjectArchiving = true;
        _showLoadSim = true;
        _showTimingTelemetry = true;
        _showGenErrors = true;
        _showStorageAndCapacity = true;
    }

    internal void CollapseAllCards()
    {
        _showJobsAndLocks = false;
        _showProjectArchiving = false;
        _showLoadSim = false;
        _showTimingTelemetry = false;
        _showGenErrors = false;
        _showStorageAndCapacity = false;
    }

    internal bool _started;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started) return;
        _started = true;

        await Session.EnsureHydratedAsync();
        if (!Session.IsAdmin)
        {
            Nav.NavigateTo("/admin/login", forceLoad: true);
            return;
        }

        StateHasChanged();

        Hub.AdminState += OnAdminState;
        MediaFolder.Changed += OnMediaFolderChanged;
        // Explicit, not just relying on MainLayout's app-wide hook — makes the local-save pipeline
        // (auto-save-on-generate) definitely live before this page starts queuing gen jobs, and
        // surfaces its status/errors here (see OnMediaFolderChanged) instead of nowhere.
        await MediaFolder.EnsureHubHookAsync();
        // Do not block UI on SignalR
        _ = ConnectHubAsync();
        await RefreshAsync();
        StateHasChanged();

        _pollCts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _ = PollLoopAsync(_pollCts.Token);
    }

    internal async Task ConnectHubAsync()
    {
        try
        {
            await Hub.StartAsync();
            _hubLive = Hub.IsConnected;
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            _hubLive = false;
        }
    }

    internal void OnMediaFolderChanged() => InvokeAsync(StateHasChanged);

    internal void OnAdminState(object? payload)
    {
        _hubLive = true;
        if (payload is not null)
        {
            try
            {
                AdminStateDto? dto = null;
                if (payload is System.Text.Json.JsonElement elem)
                {
                    dto = System.Text.Json.JsonSerializer.Deserialize<AdminStateDto>(elem.GetRawText(), EngineApiClient.JsonOpts);
                }
                else
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(payload, EngineApiClient.JsonOpts);
                    dto = System.Text.Json.JsonSerializer.Deserialize<AdminStateDto>(json, EngineApiClient.JsonOpts);
                }

                if (dto is not null)
                {
                    _state = dto;
                    if (dto.ApiInFlight > 0) _apiInFlight = dto.ApiInFlight;
                    if (dto.CapacityRejects > 0) _capacityRejects = dto.CapacityRejects;
                    if (dto.LockConflicts > 0) _lockConflicts = dto.LockConflicts;
                    if (dto.Locks is { Count: > 0 }) _locks = dto.Locks;
                    if (dto.LoadSim is not null) _loadSim = dto.LoadSim;
                    if (dto.ProcessHistory is { Count: > 0 }) _processHistory = dto.ProcessHistory;
                    _ = InvokeAsync(async () =>
                    {
                        await UpdateChartsAsync();
                        StateHasChanged();
                    });
                    return;
                }
            }
            catch { /* fallback to HTTP refresh if payload shape differs */ }
        }
        _ = InvokeAsync(RefreshAsync);
    }

    internal async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    // Only poll over HTTP when SignalR is disconnected
                    if (!_hubLive)
                    {
                        await RefreshAsync();
                        await InvokeAsync(StateHasChanged);
                    }
                }
                catch { /* keep polling */ }
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    internal async Task RefreshAsync()
    {
        if (!Session.IsAdmin) return;
        _busy = true;
        try
        {
            _state = await Api.GetAdminStateAsync();
            if (_state is not null)
            {
                _apiInFlight = _state.ApiInFlight;
                _capacityRejects = _state.CapacityRejects;
                _lockConflicts = _state.LockConflicts;
                _locks = _state.Locks ?? new();
                _loadSim = _state.LoadSim;
                _processHistory = _state.ProcessHistory ?? new();
            }
            _timingTelemetry = await Api.GetAdminTimingTelemetryTrendAsync();
            _error = null;
            await RefreshProjectOptionsAsync();
            await UpdateChartsAsync();
            await RefreshGenerationErrorsAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            // Only hard-logout on clear auth failures (not every message containing "admin")
            var msg = ex.Message;
            if (msg.Contains("403", StringComparison.Ordinal) ||
                msg.Contains("401", StringComparison.Ordinal) ||
                msg.Contains("admin role required", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                await Session.ClearAsync();
                Nav.NavigateTo("/admin/login", forceLoad: true);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task RefreshGenerationErrorsAsync()
    {
        _genErrorsBusy = true;
        try
        {
            var dto = await Api.GetAdminGenerationErrorsAsync(
                errorType: string.IsNullOrWhiteSpace(_genErrorTypeFilter) ? null : _genErrorTypeFilter,
                projectId: string.IsNullOrWhiteSpace(_genErrorProjectFilter) ? null : _genErrorProjectFilter,
                take: 200);
            _genErrors = dto?.Rows ?? new();
        }
        catch
        {
            /* keep prior list — panel is best-effort visibility, not critical path */
        }
        finally
        {
            _genErrorsBusy = false;
        }
    }

    internal static string GetGenErrorTypeBadgeClass(string errorType) => errorType switch
    {
        "http_error" => "bg-danger",
        "exception" => "bg-danger",
        "structural_gate_failure" => "bg-warning text-dark",
        "empty_response" => "bg-warning text-dark",
        "partial_coverage" => "bg-info text-dark",
        _ => "bg-secondary",
    };

    internal async Task RefreshProjectOptionsAsync()
    {
        try
        {
            var projs = await Api.GetProjectsAsync();
            _projectOptions = projs?.Projects?
                                  .Select(p => p.Id ?? "")
                                  .Where(s => s.Length > 0)
                                  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                  .ToList()
                              ?? new List<string>();
            if (string.IsNullOrWhiteSpace(_exportProjectId) ||
                !_projectOptions.Contains(_exportProjectId, StringComparer.OrdinalIgnoreCase))
            {
                _exportProjectId = projs?.Active?.Id
                                   ?? _projectOptions.FirstOrDefault()
                                   ?? "";
            }
            if (string.IsNullOrWhiteSpace(_augmentProjectId) ||
                !_projectOptions.Contains(_augmentProjectId, StringComparer.OrdinalIgnoreCase))
            {
                _augmentProjectId = projs?.Active?.Id
                                    ?? _projectOptions.FirstOrDefault()
                                    ?? "";
            }
        }
        catch
        {
            /* keep prior list */
        }

        try
        {
            var usersOverview = await Api.GetAdminUsersCreditsAsync();
            if (usersOverview?.Users is { Count: > 0 } ulist)
            {
                _userList = ulist.Select(u => u.UserId)
                                 .Where(u => !string.IsNullOrWhiteSpace(u))
                                 .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            }
        }
        catch
        {
            /* keep prior list */
        }
    }

    internal void OnImportFileSelected(InputFileChangeEventArgs e)
    {
        _importFile = e.FileCount > 0 ? e.File : null;
        _archiveError = null;
        _archiveMsg = _importFile is null
            ? null
            : $"Selected {_importFile.Name} ({_importFile.Size / (1024.0 * 1024.0):F1} MB)";
    }

    // Shared error/busy wrapper for the archive actions (export / import / logs / augment). The
    // per-action prologue (_archiveBusy/_archiveAction/_archiveMsg) stays in each method; this only
    // wraps the body so the identical catch/finally reset lives in one place.
    internal async Task RunArchiveActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _archiveError = ex.Message;
            _archiveMsg = null;
        }
        finally
        {
            _archiveBusy = false;
            _archiveAction = null;
        }
    }

    internal async Task ExportProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(_exportProjectId)) return;
        _archiveBusy = true;
        _archiveAction = "export";
        _archiveError = null;
        _archiveMsg = MediaFolder.IsConnected
            ? "Building server zip, then merging local media…"
            : "Building server zip (media folder not connected — MP4/MP3 may be missing)…";
        await RunArchiveActionAsync(async () =>
        {
            // Soft prompt: still export server files if media disconnected
            if (!MediaFolder.IsConnected)
            {
                _archiveMsg = "Media folder not connected — exporting server files only. Connect media folder for MP4/MP3.";
            }

            var (resp, fileName) = await Api.ExportProjectZipAsync(_exportProjectId);
            using (resp)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var streamRef = new DotNetStreamReference(stream);
                // Two-stage: server zip stream → browser merges client media → download
                var result = await Js.InvokeAsync<JsonElement>(
                    "PageToMovieExport.mergeServerZipWithLocalMediaAsync",
                    fileName,
                    streamRef,
                    _exportProjectId);
                if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    _archiveMsg = result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : $"Downloaded {fileName}";
                }
                else
                {
                    var err = result.TryGetProperty("error", out var e) ? e.GetString() : "download failed";
                    // Fallback: plain server zip if merge fails
                    _archiveMsg = "Merge failed — downloading server zip only…";
                    StateHasChanged();
                    var (resp2, fileName2) = await Api.ExportProjectZipAsync(_exportProjectId);
                    using (resp2)
                    {
                        await using var stream2 = await resp2.Content.ReadAsStreamAsync();
                        using var streamRef2 = new DotNetStreamReference(stream2);
                        var plain = await Js.InvokeAsync<JsonElement>(
                            "PageToMovieExport.downloadStreamAsync",
                            fileName2,
                            streamRef2);
                        if (plain.TryGetProperty("success", out var ok2) && ok2.GetBoolean())
                            _archiveMsg = $"Downloaded {fileName2} (server only). Merge error: {err}";
                        else
                        {
                            _archiveError = err;
                            _archiveMsg = null;
                        }
                    }
                }
            }
        });
    }

    internal async Task ExportLogsAsync()
    {
        _archiveBusy = true;
        _archiveAction = "export_logs";
        _archiveError = null;
        _archiveMsg = "Building server log archive zip…";
        await RunArchiveActionAsync(async () =>
        {
            var (resp, fileName) = await Api.ExportServerLogsZipAsync();
            using (resp)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var streamRef = new DotNetStreamReference(stream);
                var result = await Js.InvokeAsync<JsonElement>(
                    "PageToMovieExport.downloadStreamAsync",
                    fileName,
                    streamRef);
                if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
                    _archiveMsg = $"Downloaded {fileName}";
                else
                {
                    var err = result.TryGetProperty("error", out var e) ? e.GetString() : "download failed";
                    _archiveError = err;
                    _archiveMsg = null;
                }
            }
        });
    }

    internal async Task ImportProjectAsync()
    {
        if (_importFile is null) return;
        if (_importFile.Size > MaxImportBytes)
        {
            _archiveError = $"File too large ({_importFile.Size / (1024.0 * 1024):F0} MB). Max is 512 MB.";
            return;
        }

        _archiveBusy = true;
        _archiveAction = "import";
        _archiveError = null;
        _archiveMsg = MediaFolder.IsConnected
            ? "Stage 1/2: uploading project to server…"
            : "Uploading project to server (connect media folder to restore MP4/MP3 locally)…";
        await RunArchiveActionAsync(async () =>
        {
            // Buffer once — server import + client media extract both need the bytes.
            await using var upload = _importFile.OpenReadStream(MaxImportBytes);
            using var ms = new MemoryStream();
            await upload.CopyToAsync(ms);
            ms.Position = 0;

            var result = await Api.ImportProjectZipAsync(
                ms,
                _importFile.Name,
                preferredId: string.IsNullOrWhiteSpace(_importPreferredId) ? null : _importPreferredId.Trim(),
                overwrite: _importOverwrite,
                targetUserId: string.IsNullOrWhiteSpace(_importTargetUserId) ? null : _importTargetUserId.Trim());

            var pid = result?.ProjectId?.Trim();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(result?.Message))
                parts.Add(result!.Message!);
            else if (!string.IsNullOrWhiteSpace(pid))
                parts.Add($"Imported {pid}");

            // Stage 2: media from the same zip → local media folder (source of truth for playback).
            if (!string.IsNullOrWhiteSpace(pid))
            {
                _archiveMsg = "Stage 2/2: restoring media to local folder…";
                StateHasChanged();
                try
                {
                    ms.Position = 0;
                    using var streamRef = new DotNetStreamReference(ms);
                    var media = await Js.InvokeAsync<JsonElement>(
                        "PageToMovieExport.importZipMediaToClientFolderAsync",
                        streamRef,
                        pid);
                    if (media.TryGetProperty("success", out var ok) && ok.GetBoolean())
                    {
                        var written = media.TryGetProperty("written", out var w) && w.ValueKind == JsonValueKind.Number
                            ? w.GetInt32()
                            : 0;
                        if (media.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(mm.GetString()))
                            parts.Add(mm.GetString()!);
                        else if (written > 0)
                            parts.Add($"{written} media file(s) restored locally");
                    }
                    else
                    {
                        var err = media.TryGetProperty("error", out var e) ? e.GetString() : "local media restore failed";
                        parts.Add($"Local media: {err}");
                        // Fallback: pull whatever landed on the server
                        await MediaFolder.SyncProjectMediaToClientAsync(pid);
                    }
                }
                catch (Exception mex)
                {
                    parts.Add($"Local media restore error: {mex.Message}");
                    try { await MediaFolder.SyncProjectMediaToClientAsync(pid); }
                    catch { /* best effort */ }
                }
            }

            _archiveMsg = string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            _importFile = null;
            await RefreshProjectOptionsAsync();
            await RefreshAsync();
        });
    }

    internal async Task AugmentMusicAsync()
    {
        if (string.IsNullOrWhiteSpace(_augmentProjectId)) return;

        _archiveBusy = true;
        _archiveAction = "augment";
        _archiveError = null;
        _archiveMsg = $"Composing AI background music scores for {_augmentProjectId}...";
        await RunArchiveActionAsync(async () =>
        {
            var ok = await Api.AugmentProjectMusicAsync(_augmentProjectId);
            if (ok)
            {
                _archiveMsg = $"Successfully augmented blueprint.clips.grok.json for {_augmentProjectId} with AI background music scores.";
            }
            else
            {
                _archiveError = $"Failed to augment background music scores for {_augmentProjectId}. Ensure blueprint.clips.grok.json exists.";
                _archiveMsg = null;
            }
            await RefreshAsync();
        });
    }

    internal int _synthesizeCurrent;
    internal int _synthesizeTotal;

    internal async Task SynthesizeAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(_augmentProjectId)) return;

        // Generated music can only reach disk via the connected local media folder (the browser's
        // File System Access picker needs a real click, which this early-in-the-handler call still
        // has). Without it, jobs would run (and cost real API spend) but the audio could never be
        // saved — the only visible sign was a warning banner that only renders on the Scenes page,
        // so from here it looked like nothing happened at all.
        if (!MediaFolder.IsConnected)
        {
            var connected = await MediaFolder.ConnectFolderAsync();
            if (!connected)
            {
                _archiveError = "Connect a local media folder first (Scenes page → Connect folder) — background music can't be saved without one.";
                return;
            }
        }

        _archiveBusy = true;
        _archiveAction = "synthesize";
        _archiveError = null;
        _synthesizeCurrent = 0;
        _synthesizeTotal = 0;
        _archiveMsg = $"Fetching scene list for {_augmentProjectId}…";
        StateHasChanged();

        try
        {
            var scenesDto = await Api.GetScenesAsync(_augmentProjectId);
            var scenes = scenesDto?.Scenes;
            if (scenes is null || scenes.Count == 0)
            {
                _archiveError = $"No scenes found for project {_augmentProjectId}. Build shot plan first.";
                _archiveMsg = null;
                return;
            }

            _synthesizeTotal = scenes.Count;
            int queuedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            var failureMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sc in scenes)
            {
                _synthesizeCurrent++;
                _archiveMsg = $"Checking background music for {_augmentProjectId} Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                StateHasChanged();

                // First music segment's relative path (see MediaRegistryService.MusicSegmentRelativePath) —
                // segment 1 always exists once a scene has any background music synced locally.
                var audioPath = $"assets/music/scene_{sc.SceneNumber:D2}_seg_01.wav";
                var hasLocalAudio = MediaFolder.IsConnected && (await MediaFolder.StatLocalFileAsync(_augmentProjectId, audioPath)).Found;

                if (hasLocalAudio)
                {
                    skippedCount++;
                    continue;
                }

                _archiveMsg = $"Queuing background music synthesis for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                StateHasChanged();

                // One job at a time — FilmJobService caps queued jobs per user (MaxQueuePerUser),
                // and this loop can easily exceed that firing every scene back-to-back.
                var started = await Api.StartSceneMusicGenAsync(_augmentProjectId, sc.SceneNumber);
                queuedCount++;

                if (!string.IsNullOrWhiteSpace(started?.JobId))
                {
                    _archiveMsg = $"Generating background music for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                    StateHasChanged();
                    try
                    {
                        var final = await Api.WaitForJobTerminalAsync(started!.JobId, timeout: TimeSpan.FromMinutes(8));
                        // The job "succeeding" server-side only means audio bytes were fetched from the
                        // provider — it says nothing about whether this browser tab actually wrote them
                        // to the local media folder (SaveJobMediaAsync runs off a SignalR JobUpdated
                        // event this loop doesn't observe). Without this check, a wrong/unconfigured
                        // audio provider (e.g. only an AI Music API key set while audio_model_name is
                        // still the fal-ai/stable-audio default) fails every scene silently — the loop
                        // would report a false "Completed" with no sign anything went wrong.
                        if (!string.Equals(final?.Status, "done", StringComparison.OrdinalIgnoreCase))
                        {
                            failedCount++;
                            var msg = string.IsNullOrWhiteSpace(final?.Error) ? (final?.Message ?? "Unknown error") : final!.Error!;
                            failureMessages.Add(msg);
                        }
                        else
                        {
                            // Don't rely solely on the passive SignalR-triggered auto-save (OnJobUpdated
                            // → SaveJobMediaAsync) — its reliability here is unconfirmed. `final` already
                            // carries the same ClientMediaUrl/ClientRelativePath that path would react to
                            // (the last-generated segment's proxy ticket), so call the save directly, the
                            // same way SyncProjectMediaToClientAsync explicitly pulls each file rather
                            // than waiting on an event. SaveJobMediaAsync's own in-flight/dedup guard
                            // makes this a safe no-op if the passive path already grabbed it.
                            _archiveMsg = $"Saving audio for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                            StateHasChanged();
                            if (final is not null)
                                await MediaFolder.SaveJobMediaAsync(final);

                            _archiveMsg = $"Confirming local save for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                            StateHasChanged();
                            var confirmed = false;
                            for (var attempt = 0; attempt < 10; attempt++)
                            {
                                if ((await MediaFolder.StatLocalFileAsync(_augmentProjectId, audioPath)).Found)
                                {
                                    confirmed = true;
                                    break;
                                }
                                await Task.Delay(1000);
                            }
                            if (!confirmed)
                            {
                                failedCount++;
                                failureMessages.Add(
                                    $"Generated but never saved locally for Scene S{sc.SceneNumber:D2} — " +
                                    (MediaFolder.IsConnected
                                        ? $"check media folder connection ({MediaFolder.LastStatus})"
                                        : "media folder disconnected"));
                            }
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Move on — the job keeps running server-side; this loop just stops waiting on it.
                    }
                }
            }

            _archiveMsg = $"Completed background music synthesis checks for {_augmentProjectId}. Queued: {queuedCount}, Skipped (Existing): {skippedCount}, Failed: {failedCount}.";
            if (failedCount > 0)
            {
                _archiveError = $"{failedCount} scene(s) failed to synthesize: {string.Join(" | ", failureMessages)}";
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _archiveError = $"Failed to synthesize scene audio tracks: {ex.Message}";
            _archiveMsg = null;
        }
        finally
        {
            _archiveBusy = false;
            _archiveAction = null;
            StateHasChanged();
        }
    }

    internal async Task CancelJobAsync(string jobId)
    {
        _busy = true;
        _actionMsg = null;
        try
        {
            await Api.AdminCancelJobAsync(jobId);
            _actionMsg = $"Cancel requested for {jobId}";
            await RefreshAsync();
        }
        catch (Exception ex) { _actionMsg = ex.Message; }
        finally { _busy = false; }
    }

    internal async Task LoadJobLogAsync(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;
        _logError = null;
        _logJobId = jobId.Trim();
        _logJobIdInput = _logJobId;
        try
        {
            var detail = await Api.GetJobByIdAsync(_logJobId);
            _jobLog = detail?.Job;
            if (_jobLog is null)
                _logError = "Job not found (finished jobs are pruned from memory after a while). Check Railway logs for older work.";
            else
                _actionMsg = $"Loaded log for {ShortId(_logJobId)} · {_jobLog.Log?.Count ?? 0} line(s)";
        }
        catch (Exception ex)
        {
            _jobLog = null;
            _logError = ex.Message;
        }
    }

    internal void ClearJobLog()
    {
        _jobLog = null;
        _logJobId = null;
        _logError = null;
    }

    internal async Task ReleaseLockAsync(string resource)
    {
        _busy = true;
        _actionMsg = null;
        try
        {
            await Api.AdminReleaseLockAsync(resource, force: true);
            _actionMsg = $"Released {resource}";
            await RefreshAsync();
        }
        catch (Exception ex) { _actionMsg = ex.Message; }
        finally { _busy = false; }
    }

    internal bool _seedingTiming;

    internal async Task LogoutAsync()
    {
        await Api.LogoutAsync();
        Nav.NavigateTo("/admin/login");
    }

    internal async Task SeedTimingDatabaseAsync()
    {
        _seedingTiming = true;
        _actionMsg = null;
        try
        {
            var res = await Api.PostAdminTimingTelemetrySeedAsync();
            _actionMsg = res?.Message ?? "Seeded empirical benchmark entries into database.";
        }
        catch (Exception ex)
        {
            _actionMsg = $"Failed to seed database: {ex.Message}";
        }
        finally
        {
            _seedingTiming = false;
        }
    }

    internal async Task UpdateChartsAsync()
    {
        try
        {
            if (_loadSim?.History is { Count: > 0 } hist)
            {
                var labels = hist.Select(h =>
                    TimeSpan.FromSeconds(Math.Max(0, h.ElapsedSec)).ToString(@"m\:ss")).ToArray();
                var actionsPerSec = hist.Select(h => h.ActionsPerSec).ToArray();
                var actionsTotal = hist.Select(h => (double)h.ActionsTotal).ToArray();
                var p50 = hist.Select(h => (double)h.P50Ms).ToArray();
                var browseP95 = hist.Select(h => (double)h.BrowseP95Ms).ToArray();
                var errPct = hist.Select(h => h.ErrorRate * 100.0).ToArray();

                await Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                    "chartLoadSimThroughput",
                    labels,
                    new object[]
                    {
                        new { label = "actions/s", data = actionsPerSec, color = "#38bdf8", yAxisID = "y" },
                        new { label = "total actions", data = actionsTotal, color = "#a78bfa", yAxisID = "y1" },
                    },
                    new { dualY = true, yTitle = "actions/s", y2Title = "total" });

                await Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                    "chartLoadSimLatency",
                    labels,
                    new object[]
                    {
                        new { label = "p50 ms", data = p50, color = "#34d399", yAxisID = "y" },
                        new { label = "browse p95 ms", data = browseP95, color = "#fbbf24", yAxisID = "y" },
                        new { label = "error %", data = errPct, color = "#f87171", yAxisID = "y1" },
                    },
                    new { dualY = true, yTitle = "latency ms", y2Title = "error %" });
            }

            if (_processHistory is { Count: > 0 } mem)
            {
                var memLabels = mem.Select(s => s.At.ToLocalTime().ToString("HH:mm:ss")).ToArray();
                var ws = mem.Select(s => s.WorkingSetMb).ToArray();
                var gc = mem.Select(s => s.GcHeapMb).ToArray();
                var threads = mem.Select(s => (double)s.ThreadCount).ToArray();

                await Js.InvokeVoidAsync("filmStudioCharts.upsertLine",
                    "chartProcessMemory",
                    memLabels,
                    new object[]
                    {
                        new { label = "Working set (MB)", data = ws, color = "#22d3ee", yAxisID = "y" },
                        new { label = "GC heap (MB)", data = gc, color = "#c084fc", yAxisID = "y" },
                        new { label = "Threads", data = threads, color = "#fb7185", yAxisID = "y1" },
                    },
                    new { dualY = true, yTitle = "MB", y2Title = "threads" });
            }

            _chartWarning = null;
        }
        catch (Exception ex)
        {
            // First render can race Chart.js module init — only surface it to the admin
            // once we've actually seen data to chart (otherwise every idle poll would show a false alarm).
            _chartWarning = (_loadSim?.History is { Count: > 0 } || _processHistory is { Count: > 0 })
                ? ex.Message
                : null;
        }
    }

    internal static string GetDiskProgressBarClass(double pct) => pct switch
    {
        >= 90.0 => "bg-danger",
        >= 75.0 => "bg-warning",
        _ => "bg-success"
    };

    internal static string FormatUptime(long sec)
    {
        if (sec < 60) return $"{sec}s";
        if (sec < 3600) return $"{sec / 60}m {sec % 60}s";
        return $"{sec / 3600}h {(sec % 3600) / 60}m";
    }

    internal static string FormatAge(long? ms)
    {
        if (ms is null or < 0) return "—";
        var s = ms.Value / 1000.0;
        if (s < 60) return $"{s:0}s";
        if (s < 3600) return $"{s / 60:0.0}m";
        return $"{s / 3600:0.0}h";
    }

    internal string GetHitRatePolylinePoints()
    {
        var trend = _timingTelemetry?.Trend;
        if (trend is null || trend.Count == 0)
            return "50,100 440,100";

        int count = trend.Count;
        double startX = 50.0;
        double endX = 440.0;
        double stepX = count > 1 ? (endX - startX) / (count - 1) : 0;

        var points = new List<string>();
        for (int i = 0; i < count; i++)
        {
            double x = startX + (i * stepX);
            double hitRate = Math.Clamp(trend[i].HitRatePercent, 0.0, 100.0);
            double y = 100.0 - (hitRate / 100.0 * 80.0);
            points.Add($"{x:F1},{y:F1}");
        }
        return string.Join(" ", points);
    }

    internal string GetMaePolylinePoints()
    {
        var trend = _timingTelemetry?.Trend;
        if (trend is null || trend.Count == 0)
            return "50,100 440,100";

        int count = trend.Count;
        double startX = 50.0;
        double endX = 440.0;
        double stepX = count > 1 ? (endX - startX) / (count - 1) : 0;

        var points = new List<string>();
        for (int i = 0; i < count; i++)
        {
            double x = startX + (i * stepX);
            double mae = Math.Clamp(trend[i].MeanAbsoluteErrorSec, 0.0, 2.0);
            double y = 100.0 - (mae / 2.0 * 80.0);
            points.Add($"{x:F1},{y:F1}");
        }
        return string.Join(" ", points);
    }

    internal static string FormatTrendTimestamp(string ts)
    {
        if (DateTime.TryParse(ts, out var dt))
            return dt.ToString("MM/dd");
        return ts;
    }

    internal static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "—";
        return id.Length <= 10 ? id : id[..8] + "…";
    }

    public ValueTask DisposeAsync()
    {
        Hub.AdminState -= OnAdminState;
        MediaFolder.Changed -= OnMediaFolderChanged;
        _pollCts?.Cancel();
        _timer?.Dispose();
        _pollCts?.Dispose();
        // Do NOT Hub.DisposeAsync() here — JobHubClient is a shared, app-wide singleton (every
        // page's SignalR subscriptions, including ClientMediaFolderService's auto-save-on-
        // generate hook, ride the same connection). Disposing it on navigating away from /admin
        // killed that connection for the rest of the session: ClientMediaFolderService.
        // EnsureHubHookAsync() latches "_hubHooked" true on first call and never retries, so once
        // the underlying connection was torn down here it never came back — every job's generated
        // media (music, clips, anything) would stop reaching the local media folder app-wide,
        // silently, until a full page reload. This page merely stops listening to it.
        return ValueTask.CompletedTask;
    }
}

