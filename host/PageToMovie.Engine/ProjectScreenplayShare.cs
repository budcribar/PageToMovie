namespace PageToMovie.Engine;

/// <summary>Max master + index as shareable artifacts (export / fork).</summary>
public static class ProjectScreenplayShare
{
    public sealed class Manifest
    {
        public bool HasMax { get; init; }
        public bool HasIndex { get; init; }
        public bool HasDraft { get; init; }
        public int SceneCards { get; init; }
        /// <summary>trim = pick a length; film = already has a draft; adapt = still need a screenplay.</summary>
        public string Next { get; init; } = "adapt";
    }

    public static Manifest Inspect(string projectDir)
    {
        var source = Path.Combine(projectDir, "source");
        var max = File.Exists(Path.Combine(source, ScreenplayService.MaxBaseFileName));
        var draft = File.Exists(Path.Combine(source, "screenplay.fountain"));
        var index = ProjectScreenplayIndex.TryReadSummary(projectDir);
        var hasIndex = index?.HasIndex == true;
        return new Manifest
        {
            HasMax = max,
            HasIndex = hasIndex,
            HasDraft = draft,
            SceneCards = index?.SceneCards ?? 0,
            Next = ResolveNext(max, hasIndex, draft),
        };
    }

    public static string ResolveNext(bool hasMax, bool hasIndex, bool hasDraft)
    {
        if ((hasMax || hasIndex) && hasDraft)
            return "trim";
        if (hasDraft)
            return "film";
        return "adapt";
    }
}
