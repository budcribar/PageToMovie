namespace PageToMovie.Engine.Collaboration;

public enum ProjectAccessLevel
{
    None = 0,
    Viewer = 1,
    Editor = 2,
    Owner = 3,
}

/// <summary>Persisted at {projectDir}/project-acl.json</summary>
public sealed class ProjectAclDocument
{
    public string OwnerUserId { get; set; } = "";
    public List<string> Editors { get; set; } = new();
    public List<string> Viewers { get; set; } = new();
    public List<PendingInvite> PendingInvites { get; set; } = new();
    /// <summary>Monotonic revision for optimistic concurrency on shared resources.</summary>
    public long Rev { get; set; }
    /// <summary>
    /// I5 / P2: <c>shared</c> = gen uses Owner's keys; <c>personal</c> = each actor uses their own keys.
    /// Default personal (safe for BYOK).
    /// </summary>
    public string KeyMode { get; set; } = ProjectKeyModes.Personal;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectLease
{
    public string ResourceKey { get; set; } = ""; // e.g. "project" | "scene:3" | "script" | "cast:Hero"
    public string HolderUserId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectPresenceEntry
{
    public string UserId { get; set; } = "";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? ConnectionId { get; set; }
}
