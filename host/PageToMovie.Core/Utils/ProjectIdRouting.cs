namespace PageToMovie.Core.Utils;

/// <summary>
/// URL encoding and path rewrite for project ids that contain a slash
/// (<c>owner/Name</c>). ASP.NET <c>{id}</c> / <c>{projectId}</c> capture one
/// segment; encoded <c>%2F</c> is often rejected by the host as 403 or decoded
/// into two segments so ACL sees only the owner.
/// </summary>
public static class ProjectIdRouting
{
    /// <summary>Single-segment stand-in for <c>/</c> that <c>{id}</c> can capture.</summary>
    public const string EncodedSlash = "%2F";

    /// <summary>
    /// First path segment after <c>/api/projects/</c> that is a collection action,
    /// not a project id.
    /// </summary>
    private static readonly HashSet<string> CollectionSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "import",
        "forkable",
        "model-selections",
    };

    /// <summary>
    /// Resource name that appears immediately after <c>/api/projects/{id}/</c>.
    /// Keep in sync when adding a new project-scoped route.
    /// </summary>
    private static readonly HashSet<string> ResourceSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "acl",
        "activate",
        "adaptation",
        "artifacts",
        "augment-music",
        "book-images",
        "characters",
        "clip-reviews",
        "clips",
        "commit",
        "config",
        "contribution-diff",
        "contribution-sync-media",
        "cost",
        "costs",
        "credits-content",
        "dialogue",
        "edit-log",
        "export",
        "film-build",
        "film-runtime",
        "fork",
        "git",
        "invites",
        "learning-package",
        "learning-packages",
        "leases",
        "locations",
        "media",
        "media-renames",
        "movie",
        "presence",
        "push",
        "rename",
        "resolution-lock",
        "rev",
        "review",
        "scenes",
        "screenplay",
        "studio-path",
        "sync-origin",
        "takes-telemetry",
        "visibility",
        "visual-medium",
        "voice-alignment",
        "voice-capture",
    };

    /// <summary>
    /// Encode a project id for a URL path: each segment is escaped, slashes stay
    /// as <c>/</c> so proxies/Kestrel are not asked to accept <c>%2F</c>.
    /// </summary>
    public static string ToUrlPath(string? projectId)
    {
        var id = NormalizeSlashes(projectId);
        if (id.Length == 0) return "";
        return string.Join('/', id.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Prefix <paramref name="projectId"/> as <c>/api/projects/{path}</c>.
    /// </summary>
    public static string ProjectApi(string? projectId) =>
        "/api/projects/" + ToUrlPath(projectId);

    /// <summary>
    /// Prefix <paramref name="projectId"/> as <c>/api/admin/projects/{path}</c>.
    /// </summary>
    public static string AdminProjectApi(string? projectId) =>
        "/api/admin/projects/" + ToUrlPath(projectId);

    /// <summary>
    /// Collapse <c>/api/projects/owner/Name/…</c> to <c>/api/projects/owner%2FName/…</c>
    /// so existing single-segment route templates match. No-op when the id is
    /// already one segment (including a literal <c>%2F</c>).
    /// </summary>
    public static bool TryRewriteRequestPath(string? path, out string rewritten)
    {
        rewritten = path ?? "";
        if (!TrySplitProjectsPath(path, out var prefix, out var rest))
            return false;

        if (!TryReadNamespacedId(rest, out var owner, out var name, out var tail))
            return false;

        rewritten = prefix + Uri.EscapeDataString(owner) + EncodedSlash + Uri.EscapeDataString(name) + tail;
        return !string.Equals(rewritten, path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Project id from a request path, accepting both <c>owner%2FName</c> and
    /// <c>owner/Name</c> forms. Returns false for collection URLs.
    /// </summary>
    public static bool TryExtractProjectId(string? path, out string projectId)
    {
        projectId = "";
        if (!TrySplitProjectsPath(path, out _, out var rest))
            return false;

        var segs = SplitSegments(rest);
        if (segs.Length == 0)
            return false;

        var first = Unescape(segs[0]);
        if (first.Length == 0)
            return false;
        if (CollectionSegments.Contains(first) && segs.Length < 2)
            return false;

        if (first.Contains('/', StringComparison.Ordinal))
        {
            projectId = first.Trim('/');
            return projectId.Length > 0;
        }

        if (TryReadNamespacedId(rest, out var owner, out var name, out _))
        {
            projectId = owner + "/" + name;
            return true;
        }

        projectId = first;
        return true;
    }

    /// <summary>Stop presence polling — access will not recover without a project change.</summary>
    public static bool ShouldStopPresencePolling(int? httpStatus) =>
        httpStatus is 401 or 403;

    private static bool TrySplitProjectsPath(string? path, out string prefix, out string rest)
    {
        prefix = "";
        rest = "";
        if (string.IsNullOrEmpty(path))
            return false;

        const string admin = "/api/admin/projects/";
        const string user = "/api/projects/";
        if (path.StartsWith(admin, StringComparison.OrdinalIgnoreCase))
        {
            prefix = path[..admin.Length];
            rest = path[admin.Length..];
            return true;
        }

        if (path.StartsWith(user, StringComparison.OrdinalIgnoreCase))
        {
            prefix = path[..user.Length];
            rest = path[user.Length..];
            return true;
        }

        return false;
    }

    private static bool TryReadNamespacedId(string rest, out string owner, out string name, out string tail)
    {
        owner = "";
        name = "";
        tail = "";
        var segs = SplitSegments(rest);
        if (segs.Length < 2)
            return false;

        var s0 = Unescape(segs[0]);
        var s1 = Unescape(segs[1]);
        if (s0.Contains('/', StringComparison.Ordinal))
            return false;
        if (!IsProjectIdSegment(s0) || !IsProjectIdSegment(s1))
            return false;
        if (ResourceSegments.Contains(s1) || CollectionSegments.Contains(s0))
            return false;
        if (segs.Length >= 3 && !ResourceSegments.Contains(Unescape(segs[2])))
            return false;

        owner = s0;
        name = s1;
        tail = segs.Length > 2 ? "/" + string.Join('/', segs.Skip(2)) : "";
        return true;
    }

    private static string[] SplitSegments(string rest) =>
        rest.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeSlashes(string? projectId)
    {
        var id = (projectId ?? "").Trim();
        if (id.Contains('%', StringComparison.Ordinal))
        {
            try { id = Uri.UnescapeDataString(id); }
            catch { /* leave encoded if malformed */ }
        }

        id = id.Replace('\\', '/');
        while (id.Contains("//", StringComparison.Ordinal))
            id = id.Replace("//", "/", StringComparison.Ordinal);
        return id.Trim('/');
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
            return value;
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    /// <summary>Same character set as <c>ProjectStore.ValidateProjectId</c> per segment.</summary>
    private static bool IsProjectIdSegment(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
            return false;
        foreach (var ch in part)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                return false;
        }

        return true;
    }
}
