using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Collaboration;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Ownership resolution when a project has no <c>project-acl.json</c> yet (legacy / brand-new
/// projects). Must come from the project's real <c>project.json</c> ownerUserId, never from
/// splitting the project id path — the path's owner segment is a filesystem-sanitized slug (e.g.
/// an old email "budcribar@gmail.com" turned into "budcribargmail_com"), not the real account id.
/// </summary>
public class ProjectAclServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly ProjectAclService _acl;
    private const string OwnerUserId = "budcribar@gmail.com";

    public ProjectAclServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-project-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
        _acl = new ProjectAclService(Path.Combine(_root, "projects"), projects: _store);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Real_owner_is_granted_editor_before_any_acl_file_exists()
    {
        var project = await _store.CreateProjectAsync("Smoke", ownerUserId: OwnerUserId);

        // Owner segment in the id/path is sanitized and must differ from the raw id for this to be
        // a meaningful test of the bug (email-derived path slug vs. the real account id).
        Assert.NotEqual(OwnerUserId, project.Id.Split('/')[0]);

        var level = await _acl.GetAccessLevelAsync(project.Id, OwnerUserId);
        Assert.Equal(ProjectAccessLevel.Owner, level);
        Assert.True(await _acl.CanAccessAsync(project.Id, OwnerUserId, ProjectAccessLevel.Editor));
    }

    [Fact]
    public async Task GetOrCreateAclAsync_seeds_the_real_owner_not_the_path_slug()
    {
        var project = await _store.CreateProjectAsync("Smoke2", ownerUserId: OwnerUserId);

        var doc = await _acl.GetOrCreateAclAsync(project.Id, "someone-else-entirely");

        Assert.Equal(OwnerUserId, doc.OwnerUserId);
    }

    [Fact]
    public async Task A_different_user_is_not_granted_owner_access()
    {
        var project = await _store.CreateProjectAsync("Smoke3", ownerUserId: OwnerUserId);

        var level = await _acl.GetAccessLevelAsync(project.Id, "someone-else-entirely");
        Assert.Equal(ProjectAccessLevel.None, level);
    }
}
