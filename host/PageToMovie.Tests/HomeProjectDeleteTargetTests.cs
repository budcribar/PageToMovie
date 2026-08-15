using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
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
    public void FindProject_is_case_insensitive_and_returns_null_for_unknown()
    {
        var list = new[] { P("Chosen-Id", "Chosen Label") };
        Assert.Equal("Chosen Label", Home.HomeProjects.FindProject(list, "chosen-id")?.Label);
        Assert.Null(Home.HomeProjects.FindProject(list, "missing"));
        Assert.Null(Home.HomeProjects.FindProject(list, null));
    }

    [Fact]
    public void RemoveDeletedFromList_drops_row_and_clears_stale_active()
    {
        var gone = P("drop-id", "Drop Me");
        var keep = P("keep-id", "Keep Me");
        var dto = new ProjectsDto
        {
            Ok = true,
            Active = gone,
            Projects = new List<ProjectInfo> { gone, keep },
        };

        Home.HomeProjects.RemoveDeletedFromList(dto, "DROP-ID");

        Assert.Single(dto.Projects);
        Assert.Equal("keep-id", dto.Projects[0].Id);
        Assert.Null(dto.Active);
        Assert.Null(Home.HomeProjects.FindProject(dto.Projects, "drop-id"));
    }

    [Fact]
    public void RemoveDeletedFromList_keeps_active_when_it_is_a_remaining_project()
    {
        var gone = P("drop-id", "Drop Me");
        var keep = P("keep-id", "Keep Me");
        var dto = new ProjectsDto
        {
            Ok = true,
            Active = keep,
            Projects = new List<ProjectInfo> { gone, keep },
        };

        Home.HomeProjects.RemoveDeletedFromList(dto, gone.Id);

        Assert.Equal("keep-id", dto.Active?.Id);
        Assert.DoesNotContain(dto.Projects, p => p.Id == gone.Id);
    }

    [Fact]
    public void RemoveDeletedFromList_is_a_no_op_for_null_or_unknown()
    {
        Home.HomeProjects.RemoveDeletedFromList(null, "x");
        var empty = new ProjectsDto { Ok = true };
        Home.HomeProjects.RemoveDeletedFromList(empty, null);
        Home.HomeProjects.RemoveDeletedFromList(empty, "missing");
        Assert.Empty(empty.Projects);
        Assert.Null(empty.Active);
    }

    [Fact]
    public void PickNextAfterDelete_prefers_list_active_when_it_survived()
    {
        var gone = P("drop-id", "Drop Me");
        var keep = P("keep-id", "Keep Me");
        var other = P("other-id", "Other");

        var next = Home.HomeProjects.PickNextAfterDelete(
            new[] { gone, keep, other },
            listActive: keep,
            deletedId: gone.Id);

        Assert.Equal("keep-id", next?.Id);
    }

    [Fact]
    public void PickNextAfterDelete_skips_deleted_active_and_takes_first_remaining()
    {
        var gone = P("drop-id", "Drop Me");
        var keep = P("keep-id", "Keep Me");

        var next = Home.HomeProjects.PickNextAfterDelete(
            new[] { gone, keep },
            listActive: gone,
            deletedId: "drop-id");

        Assert.Equal("keep-id", next?.Id);
    }

    [Fact]
    public void PickNextAfterDelete_returns_null_when_none_remain()
    {
        var gone = P("only-id", "Only");
        Assert.Null(Home.HomeProjects.PickNextAfterDelete(new[] { gone }, gone, gone.Id));
        Assert.Null(Home.HomeProjects.PickNextAfterDelete(Array.Empty<ProjectInfo>(), gone, gone.Id));
        Assert.Null(Home.HomeProjects.PickNextAfterDelete(null, gone, gone.Id));
    }

    [Fact]
    public void After_delete_stale_payload_still_loses_deleted_id_and_selects_remaining()
    {
        // Delete API / GET can briefly echo the deleted row; the picker must still drop it
        // and move off that id (RefreshFromServerAsync will not clobber an already-set id).
        var gone = P("drop-id", "Drop Me");
        var keep = P("keep-id", "Keep Me");
        var stale = new ProjectsDto
        {
            Ok = true,
            Active = gone,
            Projects = new List<ProjectInfo> { gone, keep },
        };

        Home.HomeProjects.RemoveDeletedFromList(stale, gone.Id);
        var next = Home.HomeProjects.PickNextAfterDelete(stale.Projects, stale.Active, gone.Id);

        Assert.DoesNotContain(stale.Projects, p =>
            string.Equals(p.Id, gone.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(keep.Id, next?.Id);
        Assert.NotEqual(gone.Id, next?.Id);
    }
}
