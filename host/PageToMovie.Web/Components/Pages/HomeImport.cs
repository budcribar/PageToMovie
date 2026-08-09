using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Home
{
    /// <summary>Import domain for the Home page. Owns related UI state and behavior.</summary>
    internal sealed class HomeImport
    {
        private readonly Home S;
        public HomeImport(Home host) => S = host;

        internal bool _backingUp;

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
            var id = S._projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id)) return;
            _backingUp = true;
            S._busy = true;
            S._error = null;
            try
            {
                // Offloaded video/audio lives in the browser media folder. If it isn't connected, prompt
                // to connect it now (the folder picker needs THIS click's user activation, so do it before
                // any network await), then sync so the folder is complete before the merge.
                if (!S.MediaFolder.IsConnected)
                {
                    S._message = "Connect your media folder to include all video in the backup…";
                    var connected = await S.MediaFolder.ConnectFolderAsync();
                    if (connected)
                    {
                        S._message = "Syncing media into the folder…";
                        S.StateHasChanged();
                        try { await S.MediaFolder.SyncProjectMediaToClientAsync(id); }
                        catch { /* best effort — merge takes whatever is present */ }
                    }
                    else
                    {
                        S._message = "Media folder not connected — backing up project files only.";
                        S.StateHasChanged();
                    }
                }

                S._message = "Building backup (project files + all media)…";
                S.StateHasChanged();
                var (resp, fileName) = await S.Engine.ExportProjectZipAsUserAsync(id);
                using (resp)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var streamRef = new Microsoft.JSInterop.DotNetStreamReference(stream);
                    var result = await S.Js.InvokeAsync<System.Text.Json.JsonElement>(
                        "PageToMovieExport.mergeServerZipWithLocalMediaAsync", fileName, streamRef, id);
                    if (result.TryGetProperty("success", out var ok) && ok.GetBoolean())
                    {
                        S._message = result.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                            ? m.GetString()
                            : $"Backed up {fileName}";
                    }
                    else
                    {
                        var err = result.TryGetProperty("error", out var e) ? e.GetString() : "media merge failed";
                        S._message = "Media merge unavailable — downloading project files only…";
                        S.StateHasChanged();
                        var (resp2, fileName2) = await S.Engine.ExportProjectZipAsUserAsync(id);
                        using (resp2)
                        {
                            await using var stream2 = await resp2.Content.ReadAsStreamAsync();
                            using var streamRef2 = new Microsoft.JSInterop.DotNetStreamReference(stream2);
                            var plain = await S.Js.InvokeAsync<System.Text.Json.JsonElement>(
                                "PageToMovieExport.downloadStreamAsync", fileName2, streamRef2);
                            if (plain.TryGetProperty("success", out var ok2) && ok2.GetBoolean())
                                S._message = $"Backed up {fileName2} (project files only — connect your media folder to include video). Note: {err}";
                            else
                            {
                                S._error = err;
                                S._message = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                S._message = null;
            }
            finally
            {
                _backingUp = false;
                S._busy = false;
            }
        }


        internal void ToggleImport()
        {
            _showImport = !_showImport;
            if (_showImport)
            {
                S._showNew = false;
                S._showRename = false;
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
            name = System.Text.RegularExpressions.Regex.Replace(name, @"_\d{8}_\d{6}$", "");
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
                await using var stream = file.OpenReadStream(maxBytes);
                var res = await S.Engine.ImportProjectZipAsUserAsync(
                    stream, file.Name, name: string.IsNullOrWhiteSpace(_importName) ? null : _importName.Trim());
                _showImport = false;
                _importFile = null;
                _importName = "";
                S._message = res?.Message ?? $"Imported “{res?.ProjectId}”.";
                await S.LoadAsync();
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
