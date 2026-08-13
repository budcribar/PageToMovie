using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Components.Pages;

public partial class Home
{
    /// <summary>Import domain for the Home page. Owns related UI state and behavior.</summary>
    public sealed class HomeImport
    {
        private readonly Home S;
        public HomeImport(Home host) => S = host;

        internal bool _backingUp;

        /// <summary>0–100 overall backup progress; null when idle or indeterminate server wait.</summary>
        internal double? _backupPercent;

        internal bool _backupIndeterminate;

        internal string? _backupPhaseLabel;

        internal IBrowserFile? _importFile;


        internal string _importName = "";

        internal bool _importing;

        internal bool _showImport;


        /// <summary>
        /// Full project backup: user-mode server export → the browser merges in local media-folder bytes
        /// (video/audio the server offloaded) → download one complete zip. Falls back to a server-only zip
        /// when the media folder isn't connected or the client merge is unavailable.
        /// </summary>
        internal async Task BackupProjectAsync()
        {
            var id = S.Projects._projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id)) return;
            _backingUp = true;
            _backupPercent = null;
            _backupIndeterminate = true;
            _backupPhaseLabel = "Starting backup…";
            S._busy = true;
            S._error = null;
            DotNetObjectReference<ExportProgressSink>? progressRef = null;
            try
            {
                await EnsureMediaFolderForBackupAsync(id);
                SetBackupProgress(null, "Building backup on server (images + project files)…", indeterminate: true);
                var (body, fileName) = await S.Engine.ExportProjectZipBodyAsUserWithProgressAsync(
                    id, OnServerZipDownloadProgressAsync);
                await using (body)
                {
                    progressRef = DotNetObjectReference.Create(new ExportProgressSink(OnMergeProgressAsync));
                    await MergeOrFallbackDownloadAsync(id, body, fileName, progressRef);
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                S._message = null;
                _backupPhaseLabel = null;
            }
            finally
            {
                FinishBackup(progressRef);
            }
        }

        private async Task EnsureMediaFolderForBackupAsync(string id)
        {
            if (S.MediaFolder.IsConnected) return;
            SetBackupProgress(null, "Connect your media folder to include all video in the backup…", indeterminate: true);
            var connected = await S.MediaFolder.ConnectFolderAsync();
            if (connected)
            {
                SetBackupProgress(null, "Syncing media into the folder…", indeterminate: true);
                try { await S.MediaFolder.SyncProjectMediaToClientAsync(id); }
                catch { /* best effort — merge takes whatever is present */ }
            }
            else
            {
                SetBackupProgress(null, "Media folder not connected — backing up project files only.", indeterminate: true);
            }
        }

        private Task OnServerZipDownloadProgressAsync(long loaded, long? total)
        {
            if (total is > 0)
            {
                SetBackupProgress(
                    5 + 60.0 * loaded / total.Value,
                    $"Downloading backup… {FormatBytes(loaded)} / {FormatBytes(total.Value)}");
            }
            else
            {
                SetBackupProgress(null, $"Downloading backup… {FormatBytes(loaded)}", indeterminate: true);
            }
            return Task.CompletedTask;
        }

        private async Task MergeOrFallbackDownloadAsync(
            string id, Stream body, string fileName, DotNetObjectReference<ExportProgressSink> progressRef)
        {
            SetBackupProgress(65, "Merging local media into backup…");
            using var streamRef = new DotNetStreamReference(body);
            var result = await S.Js.InvokeAsync<JsonElement>(
                "PageToMovieExport.mergeServerZipWithLocalMediaAsync",
                fileName, streamRef, id, progressRef);
            if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
            {
                SetBackupProgress(100, MergeSuccessMessage(result, fileName));
                return;
            }

            var err = result.TryGetProperty("error", out var e) ? e.GetString() : "media merge failed";
            await DownloadProjectFilesOnlyAsync(id, err, progressRef);
        }

        private static string MergeSuccessMessage(JsonElement result, string fileName) =>
            result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? $"Backed up {fileName}"
                : $"Backed up {fileName}";

        private async Task DownloadProjectFilesOnlyAsync(
            string id, string? err, DotNetObjectReference<ExportProgressSink>? progressRef)
        {
            SetBackupProgress(null, "Media merge unavailable — downloading project files only…", indeterminate: true);
            var (body2, fileName2) = await S.Engine.ExportProjectZipBodyAsUserWithProgressAsync(
                id, OnProjectFilesDownloadProgressAsync);
            await using (body2)
            {
                using var streamRef2 = new DotNetStreamReference(body2);
                var plain = await S.Js.InvokeAsync<JsonElement>(
                    "PageToMovieExport.downloadStreamAsync", fileName2, streamRef2, progressRef);
                if (plain.TryGetProperty("success", out var ok2) && ok2.GetBoolean())
                    SetBackupProgress(
                        100,
                        $"Backed up {fileName2} (project files only — connect your media folder to include video). Note: {err}");
                else
                {
                    S._error = err;
                    S._message = null;
                    _backupPhaseLabel = null;
                }
            }
        }

        private Task OnProjectFilesDownloadProgressAsync(long loaded, long? total)
        {
            if (total is > 0)
                SetBackupProgress(
                    10 + 80.0 * loaded / total.Value,
                    $"Downloading project files… {FormatBytes(loaded)} / {FormatBytes(total.Value)}");
            else
                SetBackupProgress(null, $"Downloading… {FormatBytes(loaded)}", true);
            return Task.CompletedTask;
        }

        private void FinishBackup(DotNetObjectReference<ExportProgressSink>? progressRef)
        {
            progressRef?.Dispose();
            _backingUp = false;
            _backupIndeterminate = false;
            if (_backupPercent is not 100)
                _backupPercent = null;
            S._busy = false;
        }

        private static string FormatBytes(long n)
        {
            if (n < 1024) return $"{n} B";
            if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
            if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):0.#} MB";
            return $"{n / (1024.0 * 1024 * 1024):0.##} GB";
        }

        private void SetBackupProgress(double? overallPercent, string label, bool indeterminate = false)
        {
            _backupPercent = overallPercent;
            _backupIndeterminate = indeterminate;
            _backupPhaseLabel = label;
            S._message = label;
            S.StateHasChanged();
        }

        private Task OnMergeProgressAsync(string phase, double? phasePct, string? message)
        {
            double overall = phase switch
            {
                "merge" => 65 + Math.Clamp(phasePct ?? 0, 0, 100) * 0.25,
                "pack" => 90 + Math.Clamp(phasePct ?? 0, 0, 100) * 0.09,
                "done" => 100,
                "download" => 62,
                _ => _backupPercent ?? 65,
            };
            SetBackupProgress(phase is "done" ? 100 : overall, message ?? phase);
            return Task.CompletedTask;
        }


        internal void ToggleImport()
        {
            _showImport = !_showImport;
            if (_showImport)
            {
                S.Projects._showNew = false;
                S.Projects._showRename = false;
            }
            _importFile = null;
            _importName = "";
            S._error = null;
            S._message = null;
        }


        /// <summary>Stage 1 of import: a file was picked. Hold it and pre-fill an editable default name,
        /// then reveal the Import button — the actual import waits for the user to confirm.</summary>
        internal void OnImportFileSelected(InputFileChangeEventArgs e)
        {
            _importFile = e.File;
            S._error = null;
            S._message = null;
            if (_importFile is not null)
                _importName = DefaultNameFromFileName(_importFile.Name);
            S.StateHasChanged();
        }


        /// <summary>Friendly default project name from an export file name — strips the .zip extension,
        /// any owner/slug prefix, and the "PageToMovie_…_export"/timestamp decorations.</summary>
        internal static string DefaultNameFromFileName(string? fileName)
        {
            var name = (fileName ?? "").Trim();
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
            name = name.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
            if (name.StartsWith("PageToMovie_", StringComparison.OrdinalIgnoreCase))
                name = name["PageToMovie_".Length..];
            name = CommonRegex.Replace(name, @"_\d{8}_\d{6}$", "");
            if (name.EndsWith("_export", StringComparison.OrdinalIgnoreCase))
                name = name[..^"_export".Length];
            name = name.Trim(' ', '_', '-');
            return name.Length > 80 ? name[..80] : name;
        }


        /// <summary>Stage 2 of import: user confirmed. Import the held file under the (editable) name.</summary>
        internal async Task HandleImportAsync()
        {
            var file = _importFile;
            if (file is null) return;
            _importing = true;
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                // Match the server/Kestrel multipart cap (512 MB) — project zips carry video.
                const long maxBytes = 512L * 1024 * 1024;
                if (file.Size > maxBytes)
                {
                    S._error = $"File too large ({file.Size / (1024.0 * 1024):F0} MB). Max is 512 MB.";
                    return;
                }
                await using var stream = file.OpenReadStream(maxAllowedSize: file.Size);
                var res = await S.Engine.ImportProjectZipAsUserAsync(
                    stream, file.Name, name: string.IsNullOrWhiteSpace(_importName) ? null : _importName.Trim());
                _showImport = false;
                _importFile = null;
                _importName = "";
                S._message = res?.Message ?? $"Imported “{res?.ProjectId}”.";
                await S.Projects.LoadAsync();
                if (S.ActiveProject.HasProject)
                    await S.ActiveProject.RefreshFromServerAsync(S.Engine);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                _importing = false;
                S._busy = false;
            }
        }

    }
}
