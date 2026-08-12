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

namespace PageToMovie.Web.Components.Pages;

public partial class ContributionReview
{
    [Parameter] public string Id { get; set; } = "";

    private DiffDto? _diff;
    internal string? _message;
    internal string? _error;
    private bool _messageOk;
    internal bool _busy;
    private bool _hasConflicts;
    internal int _autoResolvedCount;
    private List<string> _remainingConflictPaths = new();
    private bool _lastSyncWasFromOrigin = true;

    private string? OriginId =>
        !string.IsNullOrWhiteSpace(_diff?.OriginProjectId) ? _diff!.OriginProjectId
        : _diff?.ParentProjectId;

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(Id))
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _error = null;
        try
        {
            _diff = await Http.GetFromJsonAsync<DiffDto>($"/api/projects/{Id}/contribution-diff");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _diff = null;
        }
    }

    private async Task AcceptSceneAsync(string sceneKey)
    {
        if (string.IsNullOrWhiteSpace(OriginId)) { _message = "No origin project."; return; }
        _busy = true;
        try
        {
            var resp = await Http.PostAsJsonAsync($"/api/projects/{OriginId}/contribution-sync-media",
                new { parentProjectId = Id, sceneKey });
            _messageOk = resp.IsSuccessStatusCode;
            _message = _messageOk ? $"Accepted {sceneKey}." : await resp.Content.ReadAsStringAsync();
            if (_messageOk) await LoadAsync();
        }
        catch (Exception ex) { _messageOk = false; _message = ex.Message; }
        finally { _busy = false; }
    }

    private Task ResolvePreferOurs() => ResolveConflictsAsync("PreferOurs");
    private Task ResolvePreferTheirs() => ResolveConflictsAsync("PreferTheirs");
    private Task ResolveAuto() => ResolveConflictsAsync("Auto");
    private Task ResolveUnion() => ResolveConflictsAsync("Union");

    private Task SyncFromOriginAsync()
    {
        _lastSyncWasFromOrigin = true;
        return RunGitSyncAsync(Id, OriginId, null);
    }

    private Task AcceptMergeAsync()
    {
        _lastSyncWasFromOrigin = false;
        return RunGitSyncAsync(OriginId, Id, null);
    }

    private Task ResolveConflictsAsync(string strategy) =>
        _lastSyncWasFromOrigin
            ? RunGitSyncAsync(Id, OriginId, strategy)
            : RunGitSyncAsync(OriginId, Id, strategy);

    private async Task SyncMediaAsync()
    {
        if (string.IsNullOrWhiteSpace(OriginId)) return;
        _busy = true;
        try
        {
            var resp = await Http.PostAsJsonAsync($"/api/projects/{Id}/contribution-sync-media",
                new { parentProjectId = OriginId });
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            _messageOk = json.TryGetProperty("ok", out var o) && o.GetBoolean();
            _message = json.TryGetProperty("message", out var m) ? m.GetString() : (_messageOk ? "Media synced." : "Failed.");
            await LoadAsync();
        }
        catch (Exception ex) { _messageOk = false; _message = ex.Message; }
        finally { _busy = false; }
    }

    private async Task RunGitSyncAsync(string? target, string? source, string? strategy)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
        {
            _messageOk = false;
            _message = "Origin project id is missing.";
            return;
        }
        _busy = true;
        try
        {
            var body = new Dictionary<string, string?> { ["parentProjectId"] = source };
            if (!string.IsNullOrWhiteSpace(strategy))
                body["autoResolveStrategy"] = strategy;
            var resp = await Http.PostAsJsonAsync($"/api/projects/{target}/sync-origin", body);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var ok = json.TryGetProperty("ok", out var o) && o.GetBoolean();
            _hasConflicts = json.TryGetProperty("hasConflicts", out var c) && c.GetBoolean();
            _autoResolvedCount = json.TryGetProperty("autoResolvedCount", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetInt32() : 0;
            _remainingConflictPaths = new();
            if (json.TryGetProperty("remainingConflictPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in paths.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        _remainingConflictPaths.Add(s);
                }
            }
            _messageOk = ok && !_hasConflicts;
            _message = json.TryGetProperty("message", out var m) ? m.GetString() : (ok ? "Synced." : "Problems.");
            if (_hasConflicts && string.IsNullOrWhiteSpace(strategy))
                _message += " Pick a strategy below to auto-resolve.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _messageOk = false;
            _message = ex.Message;
            _hasConflicts = false;
        }
        finally { _busy = false; }
    }

    private sealed class DiffDto
    {
        public string? ProjectId { get; set; }
        public string? ParentProjectId { get; set; }
        public string? OriginProjectId { get; set; }
        public List<SceneDto> Scenes { get; set; } = new();
    }

    private sealed class SceneDto
    {
        public string SceneKey { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
