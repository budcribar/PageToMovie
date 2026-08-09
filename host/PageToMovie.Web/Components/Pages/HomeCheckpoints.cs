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
    /// <summary>Checkpoints domain for the Home page. Owns related UI state and behavior.</summary>
    public sealed class HomeCheckpoints
    {
        private readonly Home S;
        public HomeCheckpoints(Home host) => S = host;

        internal bool _checkpointBusy;

        internal string _checkpointName = "";

        internal List<CheckpointDto> _checkpoints = new();

        internal bool _showCheckpoints;

        internal async Task ToggleCheckpointsAsync()
        {
            _showCheckpoints = !_showCheckpoints;
            S._error = null;
            S._message = null;
            if (_showCheckpoints)
            {
                S.Projects._showRename = false;
                S.Import._showImport = false;
                await LoadCheckpointsAsync();
            }
        }


        internal async Task LoadCheckpointsAsync()
        {
            var id = S.Projects._projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id)) return;
            _checkpoints = await S.Engine.ListCheckpointsAsync(id);
            S.StateHasChanged();
        }


        internal async Task CreateCheckpointAsync()
        {
            var id = S.Projects._projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_checkpointName)) return;
            _checkpointBusy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.CreateCheckpointAsync(id, _checkpointName.Trim());
                if (res.Ok)
                {
                    S._message = $"Checkpoint “{_checkpointName.Trim()}” saved.";
                    _checkpointName = "";
                    await LoadCheckpointsAsync();
                }
                else
                {
                    S._error = res.Error ?? "Could not save checkpoint.";
                }
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { _checkpointBusy = false; }
        }


        internal async Task RevertCheckpointAsync(CheckpointDto cp)
        {
            var id = S.Projects._projects?.Active?.Id ?? S.ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cp.CommitHash)) return;
            _checkpointBusy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.RevertToCheckpointAsync(id, cp.CommitHash);
                if (res.Ok)
                {
                    S._message = res.Message ?? "Rolled back to the checkpoint (your clips are unchanged).";
                    await LoadCheckpointsAsync();
                    await S.Projects.LoadAsync();
                    if (S.ActiveProject.HasProject)
                        await S.ActiveProject.RefreshFromServerAsync(S.Engine);
                }
                else
                {
                    S._error = res.Error ?? "Could not roll back.";
                }
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { _checkpointBusy = false; }
        }

    }
}
