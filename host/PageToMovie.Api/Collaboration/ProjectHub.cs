using Microsoft.AspNetCore.SignalR;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Api.Collaboration;

public sealed class ProjectHub : Hub
{
    private readonly IProjectPresenceService _presence;
    private readonly IProjectAclService _acl;
    private readonly IProjectLeaseService _leases;
    private readonly IUserContext _user;

    public ProjectHub(
        IProjectPresenceService presence,
        IProjectAclService acl,
        IProjectLeaseService leases,
        IUserContext user)
    {
        _presence = presence;
        _acl = acl;
        _leases = leases;
        _user = user;
    }

    public async Task JoinProject(string projectId)
    {
        var userId = _user.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            throw new HubException("Not authenticated");
        if (!await _acl.CanAccessAsync(projectId, userId, ProjectAccessLevel.Viewer, _user.IsAdmin))
            throw new HubException("Forbidden");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));
        await _presence.HeartbeatAsync(projectId, userId, Context.ConnectionId);
        await Clients.Group(GroupName(projectId)).SendAsync("PresenceChanged", projectId);
    }

    public async Task LeaveProject(string projectId)
    {
        var userId = _user.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await _presence.LeaveAsync(projectId, userId);
            // I7/I11: logout/disconnect handoff — release this user's leases
            var n = await _leases.ReleaseAllForUserAsync(projectId, userId);
            if (n > 0)
                await Clients.Group(GroupName(projectId)).SendAsync("LeaseChanged", projectId, "*", null);
        }
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
        await Clients.Group(GroupName(projectId)).SendAsync("PresenceChanged", projectId);
    }

    public async Task Heartbeat(string projectId)
    {
        var userId = _user.UserId;
        if (string.IsNullOrWhiteSpace(userId)) return;
        await _presence.HeartbeatAsync(projectId, userId, Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            // I11: connection drop → release leases so the other editor can continue
            var hit = await _presence.FindByConnectionIdAsync(Context.ConnectionId);
            if (hit is { } found)
            {
                await _presence.LeaveAsync(found.ProjectId, found.UserId);
                var n = await _leases.ReleaseAllForUserAsync(found.ProjectId, found.UserId);
                if (n > 0)
                    await Clients.Group(GroupName(found.ProjectId))
                        .SendAsync("LeaseChanged", found.ProjectId, "*", null);
                await Clients.Group(GroupName(found.ProjectId))
                    .SendAsync("PresenceChanged", found.ProjectId);
            }
        }
        catch
        {
            /* best-effort handoff */
        }
        await base.OnDisconnectedAsync(exception);
    }

    public static string GroupName(string projectId) => "project:" + projectId;
}
