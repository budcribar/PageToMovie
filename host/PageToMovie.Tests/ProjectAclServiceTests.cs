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

    [Fact]
    public async Task Email_session_is_owner_of_handle_namespaced_project()
    {
        // Production shape: folder + ownerUserId are the handle; JWT / X-User-Id is the email.
        var project = await _store.CreateProjectAsync("Mary3", ownerUserId: "budcribar");
        Assert.Equal("budcribar/Mary3", project.Id);
        Assert.Equal("budcribar", project.OwnerUserId);

        const string emailCaller = "budcribar@example.com";
        Assert.NotEqual(emailCaller, project.OwnerUserId);

        var level = await _acl.GetAccessLevelAsync(project.Id, emailCaller);
        Assert.Equal(ProjectAccessLevel.Owner, level);
        Assert.True(await _acl.CanAccessAsync(project.Id, emailCaller, ProjectAccessLevel.Editor));
        Assert.False(await _acl.CanAccessAsync(project.Id, "stranger@example.com", ProjectAccessLevel.Viewer));
    }

    [Fact]
    public async Task Admin_flag_grants_access_without_being_the_owner()
    {
        var project = await _store.CreateProjectAsync("Mary3Admin", ownerUserId: "budcribar");
        const string adminId = "other-admin";

        Assert.False(await _acl.CanAccessAsync(project.Id, adminId, ProjectAccessLevel.Viewer));
        Assert.True(await _acl.CanAccessAsync(project.Id, adminId, ProjectAccessLevel.Viewer, isAdmin: true));
    }

    [Fact]
    public async Task SaveAclAsync_writes_encoded_composite_id_to_the_normalized_path()
    {
        var project = await _store.CreateProjectAsync("NormId", ownerUserId: OwnerUserId);
        var encoded = project.Id.Replace("/", "%2F", StringComparison.Ordinal);
        Assert.Contains("%2F", encoded, StringComparison.Ordinal);

        await _acl.SaveAclAsync(encoded, new ProjectAclDocument { OwnerUserId = OwnerUserId });

        var loaded = await _acl.GetAclAsync(project.Id);
        Assert.NotNull(loaded);
        Assert.Equal(OwnerUserId, loaded.OwnerUserId);

        var projectsRoot = Path.Combine(_root, "projects");
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(projectsRoot, "*", SearchOption.AllDirectories),
            d => Path.GetFileName(d).Contains("%2F", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("%2e%2e/%2e%2e/escape")]
    [InlineData("foo/../../escape")]
    public async Task SaveAclAsync_rejects_path_traversal_project_id(string evilId)
    {
        var sentinel = Path.Combine(_root, "sentinel-outside.txt");
        File.WriteAllText(sentinel, "untouched");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _acl.SaveAclAsync(evilId, new ProjectAclDocument { OwnerUserId = OwnerUserId }));

        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(_root, "project-acl.json")));
        Assert.False(File.Exists(Path.Combine(_root, "escape", "project-acl.json")));
    }

    [Fact]
    public async Task SaveAclAsync_does_not_write_to_an_absolute_path_outside_projects_root()
    {
        // NormalizeProjectId trims leading slashes, so "/tmp/escape" becomes the relative
        // slug "tmp/escape" — confinement must still keep I/O under the projects root.
        var absoluteEscape = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "escape", "project-acl.json");
        var existedBefore = File.Exists(absoluteEscape);

        await _acl.SaveAclAsync("/tmp/escape", new ProjectAclDocument { OwnerUserId = OwnerUserId });

        Assert.Equal(existedBefore, File.Exists(absoluteEscape));
        var projectsRoot = Path.GetFullPath(Path.Combine(_root, "projects"));
        var written = Directory.GetFiles(projectsRoot, "project-acl.json", SearchOption.AllDirectories);
        Assert.NotEmpty(written);
        Assert.All(written, p => Assert.StartsWith(
            projectsRoot + Path.DirectorySeparatorChar, Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("%2e%2e/escape")]
    public async Task GetAclAsync_does_not_read_outside_projects_root(string evilId)
    {
        var loaded = await _acl.GetAclAsync(evilId);
        Assert.Null(loaded);
        Assert.Equal(ProjectAccessLevel.None, await _acl.GetAccessLevelAsync(evilId, OwnerUserId));
    }
}
