namespace PageToMovie.Web;

/// <summary>
/// Single source of truth for the "App map" shown on About.razor.
/// Add a new page here when you add a new @page route so the About table can't drift out of sync.
/// </summary>
public sealed record AppRoute(string Href, string Label, string Purpose, bool AdminOnly = false);

public static class AppRoutes
{
    public static readonly IReadOnlyList<AppRoute> All = new List<AppRoute>
    {
        new("/", "Studio", "Projects, start scene gen, my jobs"),
        new("/demo", "Demo", "Public gallery of approved shorts"),
        new("/adaptation", "Adaptation", "Book prepare, Stage 1, Stage 2"),
        new("/characters", "Characters", "Pin cast likeness / plates before clips"),
        new("/scenes", "Scenes", "Browse clips, gen, client stitch play"),
        new("/configuration", "Configuration", "Per-project pipeline config"),
        new("/review", "Review", "Approve scenes, finish the cut, export"),
        new("/cut", "Cut", "Redirect to Review Finish"),
        new("/cost", "Cost", "Cost ledger / estimates"),
        new("/about", "About", "Architecture, app map, run notes"),
        new("/admin", "Admin", "Live server state (admin JWT)", AdminOnly: true),
        new("/admin/users", "Admin users", "Users, credits used / available", AdminOnly: true),
        new("/admin/config", "Admin config", "Hot capacity / fakes settings", AdminOnly: true),
        new("/admin/learning", "Admin Learning", "Learning from reviews", AdminOnly: true),
        new("/admin/demos", "Admin demos", "Approve / reject / remove demo submissions", AdminOnly: true),
    };
}
