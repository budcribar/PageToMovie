using System.Collections.Concurrent;

namespace PageToMovie.Engine.Collaboration;

/// <summary>In-memory presence (per-process). Fine for single-node host.</summary>
public sealed class ProjectPresenceService : IProjectPresenceService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProjectPresenceEntry>> _byProject = new();

    public Task HeartbeatAsync(string projectId, string userId, string? connectionId, CancellationToken ct = default)
    {
        var map = _byProject.GetOrAdd(projectId, _ => new ConcurrentDictionary<string, ProjectPresenceEntry>(StringComparer.OrdinalIgnoreCase));
        map[userId] = new ProjectPresenceEntry
        {
            UserId = userId,
            LastSeenUtc = DateTimeOffset.UtcNow,
            ConnectionId = connectionId,
        };
        return Task.CompletedTask;
    }

    public Task LeaveAsync(string projectId, string userId, CancellationToken ct = default)
    {
        if (_byProject.TryGetValue(projectId, out var map))
            map.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectPresenceEntry>> ListAsync(string projectId, CancellationToken ct = default)
    {
        if (!_byProject.TryGetValue(projectId, out var map))
            return Task.FromResult<IReadOnlyList<ProjectPresenceEntry>>(Array.Empty<ProjectPresenceEntry>());
        var cutoff = DateTimeOffset.UtcNow - StaleAfter;
        var list = map.Values.Where(e => e.LastSeenUtc >= cutoff).OrderBy(e => e.UserId).ToList();
        return Task.FromResult<IReadOnlyList<ProjectPresenceEntry>>(list);
    }

    public Task<(string ProjectId, string UserId)?> FindByConnectionIdAsync(string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Task.FromResult<(string, string)?>(null);
        var cutoff = DateTimeOffset.UtcNow - StaleAfter;
        foreach (var (projectId, map) in _byProject)
        {
            foreach (var e in map.Values)
            {
                if (e.LastSeenUtc < cutoff) continue;
                if (string.Equals(e.ConnectionId, connectionId, StringComparison.Ordinal))
                    return Task.FromResult<(string, string)?>((projectId, e.UserId));
            }
        }
        return Task.FromResult<(string, string)?>(null);
    }
}
