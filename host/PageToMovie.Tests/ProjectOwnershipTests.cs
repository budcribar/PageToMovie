using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectOwnershipTests
{
    [Fact]
    public void SanitizeOwnerSegment_replaces_dots_and_at()
    {
        Assert.Equal("budcribarmsn_com", ProjectOwnership.SanitizeOwnerSegment("budcribarmsn.com"));
        Assert.Equal("budcribar_msn_com", ProjectOwnership.SanitizeOwnerSegment("budcribar@msn.com"));
    }

    [Fact]
    public void IsOwnedBy_matches_folder_owner_segment_alias()
    {
        var p = new ProjectInfo
        {
            Id = "budcribarmsn_com/Mary",
            OwnerUserId = "budcribarmsn.com",
        };
        var aliases = ProjectOwnership.CollectAliases(
            requestUserId: "budcribarmsn.com",
            canonicalUserId: "budcribarmsn.com",
            username: "budcribarmsn.com");
        Assert.True(ProjectOwnership.IsOwnedBy(p, aliases));
    }

    [Fact]
    public void IsOwnedBy_matches_when_jwt_is_username_and_owner_is_userid()
    {
        var p = new ProjectInfo
        {
            Id = "budcribar/Buster",
            OwnerUserId = "budcribar",
        };
        var aliases = ProjectOwnership.CollectAliases(
            requestUserId: "BudCribar",
            canonicalUserId: "budcribar",
            username: "BudCribar");
        Assert.True(ProjectOwnership.IsOwnedBy(p, aliases));
    }

    [Fact]
    public void CollectAliases_does_not_derive_handle_from_email_or_local_part()
    {
        var aliases = ProjectOwnership.CollectAliases(requestUserId: "budcribar@example.com");
        Assert.Contains("budcribar@example.com", aliases, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("budcribar", aliases, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectAliases_ignores_email_shaped_username()
    {
        var aliases = ProjectOwnership.CollectAliases(
            requestUserId: "alice",
            canonicalUserId: "alice",
            username: "alice@example.com");
        Assert.Contains("alice", aliases, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice@example.com", aliases, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsOwnedBy_rejects_email_session_for_handle_folder()
    {
        var p = new ProjectInfo
        {
            Id = "budcribar/Mary3",
            OwnerUserId = "budcribar",
        };
        var aliases = ProjectOwnership.CollectAliases(requestUserId: "budcribar@example.com");
        Assert.False(ProjectOwnership.IsOwnedBy(p, aliases));
    }

    [Fact]
    public void IsOwnedBy_rejects_when_only_contact_email_would_have_matched()
    {
        var p = new ProjectInfo
        {
            Id = "other/Project",
            OwnerUserId = "alice@example.com",
        };
        var aliases = ProjectOwnership.CollectAliases(
            requestUserId: "alice",
            canonicalUserId: "alice",
            username: "alice");
        Assert.False(ProjectOwnership.IsOwnedBy(p, aliases));
    }

    [Fact]
    public void IsOwnedBy_rejects_other_users()
    {
        var p = new ProjectInfo { Id = "other/Mary", OwnerUserId = "other" };
        var aliases = ProjectOwnership.CollectAliases("budcribar", "budcribar", "Bud");
        Assert.False(ProjectOwnership.IsOwnedBy(p, aliases));
    }

    [Fact]
    public void PickActiveInList_ignores_stale_id_not_in_list()
    {
        var list = new List<ProjectInfo>
        {
            new() { Id = "tester/Demo" },
            new() { Id = "tester/Other" },
        };
        // Stale pointer to another account's Odyssey — must not win.
        var active = ProjectOwnership.PickActiveInList(list, "budcribar/The_Odyssey2");
        Assert.NotNull(active);
        Assert.Equal("tester/Demo", active!.Id);
    }

    [Fact]
    public void PickActiveInList_uses_user_active_when_present()
    {
        var list = new List<ProjectInfo>
        {
            new() { Id = "tester/Demo" },
            new() { Id = "tester/Other" },
        };
        var active = ProjectOwnership.PickActiveInList(list, "tester/Other");
        Assert.Equal("tester/Other", active!.Id);
    }

    [Fact]
    public void PickActiveInList_empty_list_returns_null()
    {
        Assert.Null(ProjectOwnership.PickActiveInList(Array.Empty<ProjectInfo>(), "x"));
    }
}
