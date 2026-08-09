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

public partial class Configuration
{
    /// <summary>Media domain for the Configuration page. Owns related UI state and behavior.</summary>
    public sealed class ConfigurationMediaTheme
    {
        private readonly Configuration S;
        public ConfigurationMediaTheme(Configuration host) => S = host;

        internal string _uiTheme = "dark";


        /// <summary>Live-apply the theme pick to this browser tab so it's visible before Save.
        /// Only touches the DOM when editing the currently-active project — previewing a theme
        /// for some other project you happen to have selected here shouldn't repaint the whole app.</summary>
        internal async Task PreviewThemeAsync()
        {
            if (!string.Equals(S._projectId, S.ActiveProject.ProjectId, StringComparison.OrdinalIgnoreCase))
                return;
            S.Theme.Set(_uiTheme);
            try { await S.Js.InvokeVoidAsync("fsTheme.apply", _uiTheme); }
            catch { /* ignore */ }
        }


        internal void OnMediaFolderChanged()
        {
            _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal async Task OnThemeChangedAsync()
        {
            await PreviewThemeAsync();
            await S.Form.ScheduleAutoSaveAsync();
        }


        internal async Task ConnectMediaFolderAsync()
        {
            S._error = null;
            S._message = null;
            try
            {
                var ok = await S.MediaFolder.ConnectFolderAsync();
                if (!ok)
                {
                    S._error = S.MediaFolder.LastStatus
                             ?? "Could not open the folder picker. Use Chrome or Edge and allow folder access.";
                    return;
                }
                S._message = $"Media folder set to “{S.MediaFolder.FolderName ?? "selected folder"}”.";
                if (!string.IsNullOrWhiteSpace(S._projectId))
                    await S.MediaFolder.SyncProjectMediaToClientAsync(S._projectId);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }


        internal async Task ReconnectMediaFolderAsync()
        {
            S._error = null;
            S._message = null;
            try
            {
                var ok = await S.MediaFolder.ReconnectAsync();
                if (!ok)
                {
                    S._error = S.MediaFolder.LastStatus
                             ?? "Could not reconnect. Use Select folder to pick again.";
                    return;
                }
                S._message = $"Reconnected “{S.MediaFolder.FolderName ?? "folder"}”.";
                if (!string.IsNullOrWhiteSpace(S._projectId))
                    await S.MediaFolder.SyncProjectMediaToClientAsync(S._projectId);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }

    }
}
