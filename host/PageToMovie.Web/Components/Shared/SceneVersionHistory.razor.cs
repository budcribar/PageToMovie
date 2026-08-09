using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Shared;

public partial class SceneVersionHistory
{
    [Parameter] public string ProjectId { get; set; } = "";
    [Parameter] public string SceneKey { get; set; } = "";
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnRestored { get; set; }

    private List<VersionDto> _versions = new();
    private bool _loading;
    private bool _busy;
    private bool _ok;
    private string? _message;

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(SceneKey))
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _message = null;
        try
        {
            var resp = await Http.GetFromJsonAsync<ListResponse>(
                $"/api/projects/{ProjectId}/scenes/{Uri.EscapeDataString(SceneKey)}/versions");
            _versions = resp?.Versions ?? new();
        }
        catch (Exception ex)
        {
            _ok = false;
            _message = ex.Message;
            _versions = new();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SnapshotAsync()
    {
        _busy = true;
        _message = null;
        try
        {
            var resp = await Http.PostAsJsonAsync(
                $"/api/projects/{ProjectId}/scenes/{Uri.EscapeDataString(SceneKey)}/versions",
                new { note = "manual snapshot" });
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            _ok = json.TryGetProperty("ok", out var o) && o.GetBoolean();
            _message = _ok ? "Snapshot saved." : "Snapshot failed.";
            if (_ok) await LoadAsync();
        }
        catch (Exception ex)
        {
            _ok = false;
            _message = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RestoreAsync(string versionId)
    {
        _busy = true;
        _message = null;
        try
        {
            var resp = await Http.PostAsync(
                $"/api/projects/{ProjectId}/scenes/{Uri.EscapeDataString(SceneKey)}/versions/{Uri.EscapeDataString(versionId)}/restore",
                null);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            _ok = json.TryGetProperty("ok", out var o) && o.GetBoolean();
            if (_ok)
            {
                _message = "Restored. Reloading scene…";
                await OnRestored.InvokeAsync();
            }
            else
            {
                _message = json.TryGetProperty("error", out var e) ? e.GetString() : "Restore failed.";
            }
        }
        catch (Exception ex)
        {
            _ok = false;
            _message = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private sealed class ListResponse
    {
        public bool Ok { get; set; }
        public List<VersionDto> Versions { get; set; } = new();
    }

    private sealed class VersionDto
    {
        public string VersionId { get; set; } = "";
        public string SceneKey { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string? Note { get; set; }
        public string? CreatedBy { get; set; }
        public List<string> Files { get; set; } = new();
    }
}
