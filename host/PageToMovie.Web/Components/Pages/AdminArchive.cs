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
    /// <summary>Archive/import/export/augment/synthesize domain for the Admin page.</summary>
    public sealed class AdminArchive
    {
        private readonly Admin S;
        public AdminArchive(Admin host) => S = host;

        internal List<string> _projectOptions = new();
        internal List<string> _userList = new();
        internal string _exportProjectId = "";
        internal string _augmentProjectId = "";
        /// <summary>Target for admin one-shot screenplay enrich.</summary>
        internal string _enrichProjectId = "";
        internal string _importPreferredId = "";
        internal string _importTargetUserId = "";
        internal bool _importOverwrite;
        internal IBrowserFile? _importFile;
        internal bool _archiveBusy;
        internal string? _archiveAction;
        internal string? _archiveMsg;
        internal string? _archiveError;
        internal const long MaxImportBytes = 512L * 1024 * 1024;
        internal int _synthesizeCurrent;
        internal int _synthesizeTotal;

        /// <summary>0–100 export progress; null when idle/indeterminate server wait.</summary>
        internal double? _exportPercent;
        internal bool _exportIndeterminate;
        internal string? _exportPhaseLabel;

        internal async Task RefreshProjectOptionsAsync()
        {
            try
            {
                var projs = await S.Api.GetProjectsAsync();
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
                if (string.IsNullOrWhiteSpace(_enrichProjectId) ||
                    !_projectOptions.Contains(_enrichProjectId, StringComparer.OrdinalIgnoreCase))
                {
                    _enrichProjectId = projs?.Active?.Id
                                       ?? _exportProjectId
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
                var usersOverview = await S.Api.GetAdminUsersCreditsAsync();
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
            _exportPercent = null;
            _exportIndeterminate = true;
            _exportPhaseLabel = "Building server zip…";
            _archiveMsg = S.MediaFolder.IsConnected
                ? "Building server zip, then merging local media…"
                : "Building server zip (media folder not connected — MP4/MP3 may be missing)…";
            await RunArchiveActionAsync(async () =>
            {
                if (!S.MediaFolder.IsConnected)
                {
                    _archiveMsg = "Media folder not connected — exporting server files only. Connect media folder for MP4/MP3.";
                }

                DotNetObjectReference<ExportProgressSink>? progressRef = null;
                try
                {
                    SetExportProgress(null, "Building zip on server (images + project files)…", true);
                    var (body, fileName) = await S.Api.ExportProjectZipBodyAdminWithProgressAsync(
                        _exportProjectId,
                        async (loaded, total) =>
                        {
                            if (total is > 0)
                                SetExportProgress(
                                    5 + 60.0 * loaded / total.Value,
                                    $"Downloading… {FormatExportBytes(loaded)} / {FormatExportBytes(total.Value)}");
                            else
                                SetExportProgress(null, $"Downloading… {FormatExportBytes(loaded)}", true);
                            await Task.CompletedTask;
                        });

                    await using (body)
                    {
                        SetExportProgress(65, "Merging local media…");
                        progressRef = DotNetObjectReference.Create(new ExportProgressSink(OnExportMergeProgressAsync));
                        using var streamRef = new DotNetStreamReference(body);
                        var result = await S.Js.InvokeAsync<JsonElement>(
                            "PageToMovieExport.mergeServerZipWithLocalMediaAsync",
                            fileName,
                            streamRef,
                            _exportProjectId,
                            progressRef);
                        if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
                        {
                            SetExportProgress(
                                100,
                                result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                                    ? m.GetString()
                                    : $"Downloaded {fileName}");
                        }
                        else
                        {
                            var err = result.TryGetProperty("error", out var e) ? e.GetString() : "download failed";
                            SetExportProgress(null, "Merge failed — downloading server zip only…", true);
                            var (body2, fileName2) = await S.Api.ExportProjectZipBodyAdminWithProgressAsync(
                                _exportProjectId,
                                async (loaded, total) =>
                                {
                                    if (total is > 0)
                                        SetExportProgress(10 + 80.0 * loaded / total.Value,
                                            $"Downloading server zip… {FormatExportBytes(loaded)} / {FormatExportBytes(total.Value)}");
                                    else
                                        SetExportProgress(null, $"Downloading… {FormatExportBytes(loaded)}", true);
                                    await Task.CompletedTask;
                                });
                            await using (body2)
                            {
                                using var streamRef2 = new DotNetStreamReference(body2);
                                var plain = await S.Js.InvokeAsync<JsonElement>(
                                    "PageToMovieExport.downloadStreamAsync",
                                    fileName2,
                                    streamRef2,
                                    progressRef);
                                if (plain.TryGetProperty("success", out var ok2) && ok2.GetBoolean())
                                    SetExportProgress(100, $"Downloaded {fileName2} (server only). Merge error: {err}");
                                else
                                {
                                    _archiveError = err;
                                    _archiveMsg = null;
                                    _exportPhaseLabel = null;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    progressRef?.Dispose();
                    _exportIndeterminate = false;
                    if (_exportPercent is not 100)
                        _exportPercent = null;
                }
            });
        }

        private static string FormatExportBytes(long n)
        {
            if (n < 1024) return $"{n} B";
            if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
            if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):0.#} MB";
            return $"{n / (1024.0 * 1024 * 1024):0.##} GB";
        }

        private void SetExportProgress(double? overallPercent, string? label, bool indeterminate = false)
        {
            _exportPercent = overallPercent;
            _exportIndeterminate = indeterminate;
            _exportPhaseLabel = label;
            _archiveMsg = label;
            S.StateHasChanged();
        }

        private Task OnExportMergeProgressAsync(string phase, double? phasePct, string? message)
        {
            double overall = phase switch
            {
                "merge" => 65 + Math.Clamp(phasePct ?? 0, 0, 100) * 0.25,
                "pack" => 90 + Math.Clamp(phasePct ?? 0, 0, 100) * 0.09,
                "done" => 100,
                "download" => 62,
                _ => _exportPercent ?? 65,
            };
            SetExportProgress(phase is "done" ? 100 : overall, message ?? phase);
            return Task.CompletedTask;
        }

        internal async Task ExportLogsAsync()
        {
            _archiveBusy = true;
            _archiveAction = "export_logs";
            _archiveError = null;
            _archiveMsg = "Building server log archive zip…";
            await RunArchiveActionAsync(async () =>
            {
                var (resp, fileName) = await S.Api.ExportServerLogsZipAsync();
                using (resp)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var streamRef = new DotNetStreamReference(stream);
                    var result = await S.Js.InvokeAsync<JsonElement>(
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
            _archiveMsg = S.MediaFolder.IsConnected
                ? "Stage 1/2: uploading project to server…"
                : "Uploading project to server (connect media folder to restore MP4/MP3 locally)…";
            await RunArchiveActionAsync(async () =>
            {
                // Buffer once — server import + client media extract both need the bytes.
                await using var upload = _importFile.OpenReadStream(MaxImportBytes);
                using var ms = new MemoryStream();
                await upload.CopyToAsync(ms);
                ms.Position = 0;

                var result = await S.Api.ImportProjectZipAsync(
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
                    S.StateHasChanged();
                    try
                    {
                        ms.Position = 0;
                        using var streamRef = new DotNetStreamReference(ms);
                        var media = await S.Js.InvokeAsync<JsonElement>(
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
                            await S.MediaFolder.SyncProjectMediaToClientAsync(pid);
                        }
                    }
                    catch (Exception mex)
                    {
                        parts.Add($"Local media restore error: {mex.Message}");
                        try { await S.MediaFolder.SyncProjectMediaToClientAsync(pid); }
                        catch { /* best effort */ }
                    }
                }

                _archiveMsg = string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
                _importFile = null;
                await RefreshProjectOptionsAsync();
                await S.State.RefreshAsync();
            });
        }

        
        internal async Task EnrichScreenplayAsync()
        {
            if (string.IsNullOrWhiteSpace(_enrichProjectId)) return;
            _archiveBusy = true;
            _archiveAction = "enrich";
            _archiveError = null;
            _archiveMsg = $"Enriching screenplay for {_enrichProjectId} (visual detail from book; dialogue unchanged)…";
            await RunArchiveActionAsync(async () =>
            {
                var result = await S.Api.EmbellishScreenplayAsync(_enrichProjectId);
                if (result is null)
                {
                    _archiveError = "Enrich failed — no response.";
                    _archiveMsg = null;
                    return;
                }
                if (result.Ok)
                {
                    _archiveMsg = result.Message
                        ?? $"Enriched {_enrichProjectId}. Re-approve the screenplay if it was already approved.";
                }
                else
                {
                    _archiveError = result.Error ?? "Enrich failed.";
                    _archiveMsg = null;
                }
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
                var ok = await S.Api.AugmentProjectMusicAsync(_augmentProjectId);
                if (ok)
                {
                    _archiveMsg = $"Successfully augmented blueprint.clips.grok.json for {_augmentProjectId} with AI background music scores.";
                }
                else
                {
                    _archiveError = $"Failed to augment background music scores for {_augmentProjectId}. Ensure blueprint.clips.grok.json exists.";
                    _archiveMsg = null;
                }
                await S.State.RefreshAsync();
            });
        }

        internal async Task SynthesizeAudioAsync()
        {
            if (string.IsNullOrWhiteSpace(_augmentProjectId)) return;

            // Generated music can only reach disk via the connected local media folder (the browser's
            // File System Access picker needs a real click, which this early-in-the-handler call still
            // has). Without it, jobs would run (and cost real API spend) but the audio could never be
            // saved — the only visible sign was a warning banner that only renders on the Scenes page,
            // so from here it looked like nothing happened at all.
            if (!S.MediaFolder.IsConnected)
            {
                var connected = await S.MediaFolder.ConnectFolderAsync();
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
            S.StateHasChanged();

            try
            {
                var scenesDto = await S.Api.GetScenesAsync(_augmentProjectId);
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
                    S.StateHasChanged();

                    // First music segment's relative path (see MediaRegistryService.MusicSegmentRelativePath) —
                    // segment 1 always exists once a scene has any background music synced locally.
                    var audioPath = $"assets/music/scene_{sc.SceneNumber:D2}_seg_01.wav";
                    var hasLocalAudio = S.MediaFolder.IsConnected && (await S.MediaFolder.StatLocalFileAsync(_augmentProjectId, audioPath)).Found;

                    if (hasLocalAudio)
                    {
                        skippedCount++;
                        continue;
                    }

                    _archiveMsg = $"Queuing background music synthesis for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                    S.StateHasChanged();

                    // One job at a time — FilmJobService caps queued jobs per user (MaxQueuePerUser),
                    // and this loop can easily exceed that firing every scene back-to-back.
                    var started = await S.Api.StartSceneMusicGenAsync(_augmentProjectId, sc.SceneNumber);
                    queuedCount++;

                    if (!string.IsNullOrWhiteSpace(started?.JobId))
                    {
                        _archiveMsg = $"Generating background music for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                        S.StateHasChanged();
                        try
                        {
                            var final = await S.Api.WaitForJobTerminalAsync(started!.JobId, timeout: TimeSpan.FromMinutes(8));
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
                                S.StateHasChanged();
                                if (final is not null)
                                    await S.MediaFolder.SaveJobMediaAsync(final);

                                _archiveMsg = $"Confirming local save for Scene S{sc.SceneNumber:D2} ({_synthesizeCurrent}/{_synthesizeTotal})…";
                                S.StateHasChanged();
                                var confirmed = false;
                                for (var attempt = 0; attempt < 10; attempt++)
                                {
                                    if ((await S.MediaFolder.StatLocalFileAsync(_augmentProjectId, audioPath)).Found)
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
                                        (S.MediaFolder.IsConnected
                                            ? $"check media folder connection ({S.MediaFolder.LastStatus})"
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
                await S.State.RefreshAsync();
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
                S.StateHasChanged();
            }
        }
    }
}
