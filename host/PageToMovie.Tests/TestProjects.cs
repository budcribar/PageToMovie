using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;

namespace PageToMovie.Tests;

/// <summary>
/// Shared test fixtures for the common "temp workspace + ProjectStore" boilerplate: creates a
/// unique temp workspace root containing <c>projects/&lt;id&gt;/project.json</c> and (via the
/// overloads) the read-caches-disabled <see cref="ProjectStore"/> plus the review/edit-log stores
/// most store tests need. The caller keeps ownership of <c>root</c> for cleanup.
/// </summary>
internal static class TestProjects
{
    /// <summary>
    /// Creates <c>&lt;temp&gt;/&lt;prefix&gt;&lt;guid&gt;/projects/&lt;projectId&gt;/project.json</c>
    /// (with <c>{"id":"&lt;projectId&gt;"}</c>) and returns the workspace root path.
    /// </summary>
    public static string CreateWorkspace(string prefix, string projectId = "Demo")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", projectId));
        File.WriteAllText(
            Path.Combine(root, "projects", projectId, "project.json"),
            $$"""{"id":"{{projectId}}"}""");
        return root;
    }

    /// <summary>Workspace + a read-caches-disabled <see cref="ProjectStore"/>.</summary>
    public static ProjectStore CreateStore(string prefix, out string root, string projectId = "Demo")
    {
        root = CreateWorkspace(prefix, projectId);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
        var store = new ProjectStore(opts);
        // Stage 2 / generate fail-fast without a decided medium. Engine harnesses stamp it here.
        OfflineTestModelConfig.WriteDecidedVision(store, projectId);
        return store;
    }

    /// <summary>Workspace + <see cref="ProjectStore"/> + <see cref="ReviewEventStore"/>.</summary>
    public static (ProjectStore Store, ReviewEventStore Events) CreateStoreWithEvents(
        string prefix, out string root, string projectId = "Demo")
    {
        var store = CreateStore(prefix, out root, projectId);
        var events = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);
        return (store, events);
    }

    /// <summary>Workspace + <see cref="ProjectStore"/> + <see cref="ReviewEventStore"/> + <see cref="EditLogService"/>.</summary>
    public static (ProjectStore Store, ReviewEventStore Events, EditLogService EditLogs) CreateStoreWithEditLogs(
        string prefix, out string root, string projectId = "Demo")
    {
        var (store, events) = CreateStoreWithEvents(prefix, out root, projectId);
        var editLogs = new EditLogService(store, events, NullLogger<EditLogService>.Instance);
        return (store, events, editLogs);
    }
}
