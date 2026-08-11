namespace PageToMovie.Engine.Collaboration;

/// <summary>Canonical project-lease resource keys (P4–P6 / I6–I9). Distinct from job <c>LockKeys</c>.</summary>
public static class ProjectLeaseKeys
{
    public const string Script = "script";
    public const string ProjectGen = "project:gen";

    public static string Scene(int sceneNumber) => $"scene:{Math.Max(1, sceneNumber)}";
    public static string Cast(string charKey) => "cast:" + (charKey ?? "").Trim();
    public static string Loc(string locKey) => "loc:" + (locKey ?? "").Trim();

    public static bool TryParseScene(string? resourceKey, out int sceneNumber)
    {
        sceneNumber = 0;
        if (string.IsNullOrWhiteSpace(resourceKey)) return false;
        var t = resourceKey.Trim();
        if (!t.StartsWith("scene:", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(t.AsSpan("scene:".Length), out sceneNumber) && sceneNumber > 0;
    }
}

/// <summary>Project key billing mode (P2 / I5).</summary>
public static class ProjectKeyModes
{
    public const string Shared = "shared";
    public const string Personal = "personal";

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode?.Trim(), Shared, StringComparison.OrdinalIgnoreCase))
            return Shared;
        return Personal;
    }

    public static bool IsShared(string? mode) =>
        string.Equals(Normalize(mode), Shared, StringComparison.OrdinalIgnoreCase);
}
