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

public partial class ProjectSharePanel
{
    AclDto? _acl;
    List<PresenceDto>? _presence;
    string _inviteUserId = "";
    string _inviteRole = "editor";
    int _sceneNumber = 1;
    string? _leaseStatus;
    string? _error;
    string? _info;
    bool _busy;

    string EncodedId => Uri.EscapeDataString(ActiveProject.ProjectId ?? "");

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(ActiveProject.ProjectId) && _acl is null)
            await LoadAclAsync();
    }

    async Task LoadAclAsync()
    {
        await Run(async () =>
        {
            _acl = await Http.GetFromJsonAsync<AclDto>($"api/projects/{EncodedId}/acl");
            _info = "ACL loaded.";
        });
    }

    async Task InviteAsync()
    {
        await Run(async () =>
        {
            var path = _inviteRole == "viewer"
                ? $"api/projects/{EncodedId}/acl/viewers"
                : $"api/projects/{EncodedId}/acl/invite";
            var res = await Http.PostAsJsonAsync(path, new { username = _inviteUserId.Trim(), role = "editor" });
            if (!res.IsSuccessStatusCode)
            {
                _error = await res.Content.ReadAsStringAsync();
                return;
            }
            _acl = await res.Content.ReadFromJsonAsync<AclDto>();
            _info = $"Invited {_inviteUserId} as {_inviteRole}.";
            _inviteUserId = "";
        });
    }

    async Task AcquireLeaseAsync()
    {
        await Run(async () =>
        {
            var key = $"scene:{_sceneNumber}";
            var res = await Http.PostAsync($"api/projects/{EncodedId}/leases/{Uri.EscapeDataString(key)}/acquire", null);
            var body = await res.Content.ReadAsStringAsync();
            if (res.StatusCode == System.Net.HttpStatusCode.Locked || (int)res.StatusCode == 423)
                _leaseStatus = $"Locked by another user: {body}";
            else if (!res.IsSuccessStatusCode)
                _error = body;
            else
                _leaseStatus = $"Acquired {key}: {body}";
        });
    }

    async Task ReleaseLeaseAsync()
    {
        await Run(async () =>
        {
            var key = $"scene:{_sceneNumber}";
            var res = await Http.PostAsync($"api/projects/{EncodedId}/leases/{Uri.EscapeDataString(key)}/release", null);
            _leaseStatus = res.IsSuccessStatusCode ? $"Released {key}" : await res.Content.ReadAsStringAsync();
        });
    }

    async Task RefreshLeaseAsync()
    {
        await Run(async () =>
        {
            var key = $"scene:{_sceneNumber}";
            var res = await Http.GetAsync($"api/projects/{EncodedId}/leases/{Uri.EscapeDataString(key)}");
            if (res.StatusCode == System.Net.HttpStatusCode.NoContent)
                _leaseStatus = $"{key}: free";
            else
                _leaseStatus = await res.Content.ReadAsStringAsync();
        });
    }

    async Task LoadPresenceAsync()
    {
        await Run(async () =>
        {
            _presence = await Http.GetFromJsonAsync<List<PresenceDto>>($"api/projects/{EncodedId}/presence")
                        ?? new();
        });
    }

    async Task Run(Func<Task> action)
    {
        _busy = true;
        _error = null;
        _info = null;
        try { await action(); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Share panel action failed");
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    sealed class AclDto
    {
        public string OwnerUserId { get; set; } = "";
        public List<string> Editors { get; set; } = new();
        public List<string> Viewers { get; set; } = new();
        public long Rev { get; set; }
    }

    sealed class PresenceDto
    {
        public string UserId { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; }
    }
}
