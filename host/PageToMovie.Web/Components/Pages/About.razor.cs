using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class About
{

    internal bool _busy;
    internal string? _error;
    internal HealthInfo? _health;
    internal CapacityDto? _capacity;

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        if (Session.IsAdmin)
            await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            await Engine.EnsureHealthyAsync();
            // health endpoint returns anonymous shape — call via Http through a thin parse
            using var http = new HttpClient { BaseAddress = new Uri(Engine.ApiBaseUrl.TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", Session.UserId);
            if (!string.IsNullOrWhiteSpace(Session.Token))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Session.Token);

            var json = await http.GetStringAsync("health");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            _health = new HealthInfo
            {
                Service = r.TryGetProperty("service", out var s) ? s.GetString() : null,
                Workspace = r.TryGetProperty("workspace", out var w) ? w.GetString() : null,
                ActiveProject = r.TryGetProperty("activeProject", out var a) ? a.GetString() : null,
                UseFakes = r.TryGetProperty("useFakes", out var f) && f.GetBoolean(),
                XaiConfigured = r.TryGetProperty("xaiConfigured", out var x) && x.GetBoolean(),
                UserId = r.TryGetProperty("userId", out var u) ? u.GetString() : null,
                IsAdmin = r.TryGetProperty("isAdmin", out var ia) && ia.GetBoolean(),
            };
            _capacity = await Engine.GetCapacityAsync();
        }
        catch (Exception ex)
        {
            _health = null;
            _capacity = null;
            _error = $"API not reachable at {Engine.ApiBaseUrl}. Start PageToMovie.Api. ({ex.Message})";
        }
        finally
        {
            _busy = false;
        }
    }

    internal sealed class HealthInfo
    {
        public string? Service { get; set; }
        public string? Workspace { get; set; }
        public string? ActiveProject { get; set; }
        public bool UseFakes { get; set; }
        public bool XaiConfigured { get; set; }
        public string? UserId { get; set; }
        public bool IsAdmin { get; set; }
    }
}
