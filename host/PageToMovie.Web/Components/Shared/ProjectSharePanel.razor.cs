using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Shared;

public partial class ProjectSharePanel
{
    ProjectAclClientDto? _acl;
    List<ProjectPresenceClientDto>? _presence;
    string _inviteUserId = "";
    string _inviteRole = "editor";
    int _sceneNumber = 1;
    string? _leaseStatus;
    string? _error;
    string? _info;
    bool _busy;
    string _keyMode = "personal";
    bool _isOwner;

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
            _acl = await Engine.GetProjectAclAsync(ActiveProject.ProjectId ?? "");
            if (_acl is not null)
            {
                _keyMode = string.IsNullOrWhiteSpace(_acl.KeyMode) ? "personal" : _acl.KeyMode.Trim().ToLowerInvariant();
                var uid = (Session.UserId ?? "").Trim();
                _isOwner = string.IsNullOrWhiteSpace(_acl.OwnerUserId)
                    || string.Equals(_acl.OwnerUserId, uid, StringComparison.OrdinalIgnoreCase);
                _info = "ACL loaded.";
            }
        });
    }

    async Task SaveKeyModeAsync()
    {
        await Run(async () =>
        {
            var (ok, err) = await Engine.SetProjectKeyModeAsync(ActiveProject.ProjectId ?? "", _keyMode);
            if (!ok)
            {
                _error = err ?? "Failed to save key mode.";
                return;
            }
            _info = $"Key mode set to {_keyMode}.";
            await LoadAclAsync();
        });
    }

    async Task InviteAsync()
    {
        await Run(async () =>
        {
            var path = _inviteRole == "viewer"
                ? $"api/projects/{EncodedId}/acl/viewers"
                : $"api/projects/{EncodedId}/acl/editors";
            var res = await Http.PostAsJsonAsync(path, new { userId = _inviteUserId.Trim() });
            if (!res.IsSuccessStatusCode)
            {
                _error = await res.Content.ReadAsStringAsync();
                return;
            }
            _acl = await res.Content.ReadFromJsonAsync<ProjectAclClientDto>();
            _info = $"Invited {_inviteUserId} as {_inviteRole}.";
            _inviteUserId = "";
        });
    }

    async Task RemoveUserAsync(string userId)
    {
        await Run(async () =>
        {
            var res = await Http.DeleteAsync($"api/projects/{EncodedId}/acl/users/{Uri.EscapeDataString(userId)}");
            if (!res.IsSuccessStatusCode)
            {
                _error = await res.Content.ReadAsStringAsync();
                return;
            }
            _acl = await res.Content.ReadFromJsonAsync<ProjectAclClientDto>();
            _info = $"Removed {userId}.";
        });
    }

    async Task AcquireLeaseAsync()
    {
        await Run(async () =>
        {
            var key = $"scene:{_sceneNumber}";
            var res = await Http.PostAsync($"api/projects/{EncodedId}/leases/{Uri.EscapeDataString(key)}", null);
            if (!res.IsSuccessStatusCode)
            {
                _error = await res.Content.ReadAsStringAsync();
                return;
            }
            _leaseStatus = $"{key}: acquired";
        });
    }

    async Task ReleaseLeaseAsync()
    {
        await Run(async () =>
        {
            var key = $"scene:{_sceneNumber}";
            var res = await Http.DeleteAsync($"api/projects/{EncodedId}/leases/{Uri.EscapeDataString(key)}");
            if (!res.IsSuccessStatusCode)
            {
                _error = await res.Content.ReadAsStringAsync();
                return;
            }
            _leaseStatus = $"{key}: released";
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
            _presence = await Engine.ListPresenceAsync(ActiveProject.ProjectId ?? "");
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
}
