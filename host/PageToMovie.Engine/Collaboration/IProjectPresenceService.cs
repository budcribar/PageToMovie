namespace PageToMovie.Engine.Collaboration;

public interface IProjectPresenceService
{
    Task HeartbeatAsync(string projectId, string userId, string? connectionId, CancellationToken ct = default);
    Task LeaveAsync(string projectId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectPresenceEntry>> ListAsync(string projectId, CancellationToken ct = default);

    /// <summary>I11: find which project/user a SignalR connection belongs to (disconnect handoff).</summary>
    Task<(string ProjectId, string UserId)?> FindByConnectionIdAsync(string connectionId, CancellationToken ct = default);
}
