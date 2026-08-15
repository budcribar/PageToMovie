using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Home Delete must name (and remove) the project in the picker — not a stale list-Active leftover.
/// </summary>
public class HomeProjectDeleteTargetTests
{
    private static ProjectInfo P(string id, string? label = null, string? title = null) =>
        new() { Id = id, Label = label, Title = title };

    [Fact]
    public void ResolveManageTarget_prefers_picker_selection_over_stale_list_active()
    {
        var leftover = P("leftover-id", "Leftover Label");
        var chosen = P("chosen-id", "Chosen Label");
        var list = new[] { leftover, chosen };

        var (id, label) = Home.HomeProjects.ResolveManageTarget(
            clientSelectedId: chosen.Id,
            listActiveId: leftover.Id,
            projects: list);

        Assert.Equal(chosen.Id, id);
        Assert.Equal("Chosen Label", label);
    }

    [Fact]
    public void ResolveManageTarget_falls_back_to_list_active_when_picker_empty()
    {
        var leftover = P("leftover-id", "Leftover Label");
        var other = P("other-id", "Other Label");

        var (id, label) = Home.HomeProjects.ResolveManageTarget(
            clientSelectedId: null,
            listActiveId: leftover.Id,
            projects: new[] { leftover, other });

        Assert.Equal(leftover.Id, id);
        Assert.Equal("Leftover Label", label);
    }

    [Fact]
    public void ResolveManageTarget_uses_title_then_id_when_label_missing()
    {
        var titled = P("proj-id", label: null, title: "Display Title");
        var (id, label) = Home.HomeProjects.ResolveManageTarget(
            "proj-id", "stale-id", new[] { titled });

        Assert.Equal("proj-id", id);
        Assert.Equal("Display Title", label);

        var unnamed = P("bare-id");
        var bare = Home.HomeProjects.ResolveManageTarget("bare-id", null, new[] { unnamed });
        Assert.Equal("bare-id", bare.Id);
        Assert.Equal("bare-id", bare.Label);
    }

    [Fact]
    public void ResolveManageTarget_empty_when_nothing_selected()
    {
        var (id, label) = Home.HomeProjects.ResolveManageTarget(null, null, Array.Empty<ProjectInfo>());
        Assert.Null(id);
        Assert.Equal("", label);
        Assert.Equal("Select a project to delete.", Home.HomeProjects.NoProjectSelectedToDelete);
    }

    [Fact]
    public void SelectionGoneAfterDelete_when_client_selection_is_the_deleted_project()
    {
        var remaining = new[] { P("other-id") };
        Assert.True(Home.HomeProjects.SelectionGoneAfterDelete("Doomed-Id", "doomed-id", remaining));
    }

    [Fact]
    public void SelectionGoneAfterDelete_when_client_selection_missing_from_refreshed_list()
    {
        var remaining = new[] { P("other-id") };
        Assert.True(Home.HomeProjects.SelectionGoneAfterDelete("vanished-id", "doomed-id", remaining));
    }

    [Fact]
    public void SelectionGoneAfterDelete_keeps_a_surviving_selection()
    {
        var remaining = new[] { P("keep-id"), P("other-id") };
        Assert.False(Home.HomeProjects.SelectionGoneAfterDelete("keep-id", "doomed-id", remaining));
        Assert.False(Home.HomeProjects.SelectionGoneAfterDelete(null, "doomed-id", remaining));
        // No refreshed list to check against — only the deleted id itself counts as gone.
        Assert.False(Home.HomeProjects.SelectionGoneAfterDelete("keep-id", "doomed-id", null));
    }

    [Fact]
    public void FindProject_is_case_insensitive_and_returns_null_for_unknown()
    {
        var list = new[] { P("Chosen-Id", "Chosen Label") };
        Assert.Equal("Chosen Label", Home.HomeProjects.FindProject(list, "chosen-id")?.Label);
        Assert.Null(Home.HomeProjects.FindProject(list, "missing"));
        Assert.Null(Home.HomeProjects.FindProject(list, null));
    }
}
