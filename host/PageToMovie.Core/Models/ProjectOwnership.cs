namespace PageToMovie.Core.Models;

/// <summary>
/// Who may see a project in GET /api/projects. Matches stable user id, username handle,
/// email, and sanitized folder owner segments so identity drift (email vs handle, dots
/// in names) does not hide projects that still exist on disk.
/// </summary>
public static class ProjectOwnership
{
    /// <summary>
    /// Folder / id segment sanitize aligned with ProjectStore.SanitizeUserSegment
    /// (letters, digits, _ -; whitespace/dot/slash → _; lowercased).
    /// </summary>
    public static string SanitizeOwnerSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '.' or '/' or '@')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
            }
        }
        var id = sb.ToString().Trim('_').ToLowerInvariant();
        if (id.Length > 64) id = id[..64].Trim('_');
        return id;
    }

    public static IReadOnlyList<string> CollectAliases(
        string? requestUserId,
        string? canonicalUserId = null,
        string? username = null,
        string? email = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var t = v.Trim();
            set.Add(t);
            var seg = SanitizeOwnerSegment(t);
            if (seg.Length > 0) set.Add(seg);
        }

        Add(requestUserId);
        Add(canonicalUserId);
        Add(username);
        Add(email);
        // Email local-part (before @) — some older sessions used it as handle.
        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@', StringComparison.Ordinal))
        {
            var local = email.Split('@')[0];
            Add(local);
        }
        return set.ToList();
    }

    public static bool IsOwnedBy(ProjectInfo project, IEnumerable<string> aliases)
    {
        if (project is null) return false;
        var aliasSet = aliases as HashSet<string>
                       ?? new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
        if (aliasSet.Count == 0) return false;

        if (!string.IsNullOrWhiteSpace(project.OwnerUserId))
        {
            var owner = project.OwnerUserId.Trim();
            if (aliasSet.Contains(owner)) return true;
            var ownerSeg = SanitizeOwnerSegment(owner);
            if (ownerSeg.Length > 0 && aliasSet.Contains(ownerSeg)) return true;
        }

        // Path projects/{ownerSeg}/{slug}
        var id = (project.Id ?? "").Replace('\\', '/').Trim('/');
        var slash = id.IndexOf('/');
        if (slash > 0)
        {
            var folderOwner = id[..slash];
            if (aliasSet.Contains(folderOwner)) return true;
            var folderSeg = SanitizeOwnerSegment(folderOwner);
            if (folderSeg.Length > 0 && aliasSet.Contains(folderSeg)) return true;
        }

        return false;
    }

    public static bool IsOwnedBy(
        ProjectInfo project,
        string? requestUserId,
        string? canonicalUserId = null,
        string? username = null,
        string? email = null) =>
        IsOwnedBy(project, CollectAliases(requestUserId, canonicalUserId, username, email));

    /// <summary>
    /// Pick the active project from a list the caller already scoped to this user
    /// (owned projects for non-admin, or full inventory for admin).
    /// Uses the per-user <paramref name="userActiveProjectId"/> when it is still in the list;
    /// never falls back to process-wide ProjectStore.ActiveProjectId (that leaks the last
    /// activation from another account).
    /// </summary>
    public static ProjectInfo? PickActiveInList(
        IReadOnlyList<ProjectInfo> list,
        string? userActiveProjectId)
    {
        if (list is null || list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(userActiveProjectId))
        {
            var hit = list.FirstOrDefault(p =>
                string.Equals(p.Id, userActiveProjectId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return list[0];
    }
}
