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

public partial class AcceptInvite
{
    [Parameter] public string Token { get; set; } = "";
    internal bool _loading = true, _busy, _accepted;
    internal string? _error, _status;
    private PreviewDto? _preview;

    protected override async Task OnInitializedAsync()
    {
        try {
            var resp = await Http.GetAsync($"/api/invites/{Uri.EscapeDataString(Token)}");
            if (!resp.IsSuccessStatusCode) { _error = "Invite not found or already used."; return; }
            _preview = await resp.Content.ReadFromJsonAsync<PreviewDto>();
        } catch (Exception ex) { _error = ex.Message; }
        finally { _loading = false; }
    }

    private async Task AcceptAsync()
    {
        _busy = true;
        try {
            var resp = await Http.PostAsync($"/api/invites/{Uri.EscapeDataString(Token)}/accept", null);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("ok", out var o) && o.GetBoolean()) {
                _accepted = true;
                _status = "Invite accepted.";
                if (json.TryGetProperty("projectId", out var pid) && pid.ValueKind == JsonValueKind.String && _preview is not null)
                    _preview.ProjectId = pid.GetString() ?? _preview.ProjectId;
            } else {
                _status = json.TryGetProperty("error", out var e) ? e.GetString() : "Accept failed.";
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    _status = "Sign in first, then accept this invite.";
            }
        } catch (Exception ex) { _status = ex.Message; }
        finally { _busy = false; }
    }

    private sealed class PreviewDto {
        public string ProjectId { get; set; } = "";
        public string Role { get; set; } = "editor";
        public string? InvitedBy { get; set; }
    }
}
