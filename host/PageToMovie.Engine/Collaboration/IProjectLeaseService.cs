namespace PageToMovie.Engine.Collaboration;

public interface IProjectLeaseService
{
    /// <summary>Acquire lease. Returns existing holder lease if conflict (caller maps to 423).</summary>
    Task<(bool Acquired, ProjectLease Lease)> TryAcquireAsync(
        string projectId, string resourceKey, string userId, TimeSpan ttl, CancellationToken ct = default);

    Task<bool> ReleaseAsync(string projectId, string resourceKey, string userId, CancellationToken ct = default);
    Task<(bool Renewed, ProjectLease? Lease)> TryRenewAsync(
        string projectId, string resourceKey, string userId, TimeSpan ttl, CancellationToken ct = default);
    Task<(bool Transferred, ProjectLease? Lease)> TryTransferAsync(
        string projectId, string resourceKey, string fromUserId, string toUserId, TimeSpan ttl, CancellationToken ct = default);
    Task<ProjectLease?> GetAsync(string projectId, string resourceKey, CancellationToken ct = default);

    /// <summary>I7/I11: release every non-expired lease held by this user on the project (logout / leave).</summary>
    Task<int> ReleaseAllForUserAsync(string projectId, string userId, CancellationToken ct = default);

    /// <summary>List active (non-expired) leases for a project.</summary>
    Task<IReadOnlyList<ProjectLease>> ListAsync(string projectId, CancellationToken ct = default);
}
