using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectForkTests
{
    private static (ProjectStore Store, string Root) MakeStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_fork_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
        return (new ProjectStore(opts), root);
    }

    /// <summary>Git objects are often read-only on Windows — clear attrs before delete.</summary>
    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(root, true);
        }
        catch { /* best effort on Windows file locks */ }
    }

    [Fact]
    public async Task ForkProjectAsync_copies_text_and_excludes_video()
    {
        var (store, root) = MakeStore();
        try
        {
            var source = await store.CreateProjectAsync("Original", ownerUserId: "owner1");
            Assert.Equal("owner1/Original", source.Id);
            Assert.Contains(Path.Combine("projects", "owner1", "Original"), source.Path);
            await store.SetProjectVisibilityModeAsync(source.Id, "Open");
            var sourceDir = source.Path;
            Directory.CreateDirectory(Path.Combine(sourceDir, "source"));
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "source", "screenplay.fountain"), "INT. HOUSE - DAY");
            Directory.CreateDirectory(Path.Combine(sourceDir, "assets", "video"));
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "assets", "video", "scene_01_clip_01.mp4"), "fake video bytes");
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "assets", "video", "scene_01_clip_01.clip.json"),
                """{"source_file_id":"file_abc123"}""");
            // Owner-only artefacts: a local-folder marker (their disk) and an extend-source marker
            // (a file_id minted with their key). Neither means anything to the fork.
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "assets", "video", "scene_01_clip_01.mp4.client.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "assets", "video", "_extend_src_s01c02.json"), """{"file_id":"file_owner_only"}""");

            var fork = await store.ForkProjectAsync(source.Id, "collaborator1");

            Assert.Equal("collaborator1", fork.OwnerUserId);
            Assert.Equal(source.Id, fork.ParentProjectId);
            Assert.NotEqual(source.Id, fork.Id);
            Assert.StartsWith("collaborator1/", fork.Id, StringComparison.OrdinalIgnoreCase);

            Assert.True(File.Exists(Path.Combine(fork.Path, "source", "screenplay.fountain")));
            Assert.False(File.Exists(Path.Combine(fork.Path, "assets", "video", "scene_01_clip_01.mp4")));
            Assert.True(File.Exists(Path.Combine(fork.Path, "assets", "video", "scene_01_clip_01.clip.json")));
            Assert.Equal("file_abc123", store.TryReadClipSourceFileId(fork.Id, 1, 1));
            Assert.False(File.Exists(Path.Combine(fork.Path, "assets", "video", "scene_01_clip_01.mp4.client.json")), "fork must not inherit the source owner's local-folder marker");
            Assert.False(File.Exists(Path.Combine(fork.Path, "assets", "video", "_extend_src_s01c02.json")), "fork must not inherit the source owner's extend-source file_id");
            // Fork has its own Git package (text only) with an initial commit
            Assert.True(Directory.Exists(Path.Combine(fork.Path, ".git")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ForkProjectAsync_inherits_max_master_and_index()
    {
        var (store, root) = MakeStore();
        try
        {
            var source = await store.CreateProjectAsync("Epic", ownerUserId: "owner1");
            await store.SetProjectVisibilityModeAsync(source.Id, "Open");
            var srcDir = Path.Combine(source.Path, "source");
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "screenplay.fountain"), "INT. CUT - DAY\n\nShort cut.\n");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "screenplay.max.fountain"), "Title: Epic\n\nINT. HALL - DAY\n\nFull master.\n");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "screenplay.index.json"),
                """{"schema_version":"screenplay.index.v1","movie_title":"Epic","acts":[{"id":"a1","title":"A","sequences":[{"id":"s1","title":"Open","scenes":[{"id":"c1","order":1,"heading":"INT. HALL - DAY","location_key":"Loc_Hall","speaking_cast":["HERO"],"beat":"Start","book_anchor_start":"a","book_anchor_end":"b"}]}]}]}""");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "screenplay.cut.json"), """{"keep_all":false}""");

            var fork = await store.ForkProjectAsync(source.Id, "reader1");
            var share = ProjectScreenplayShare.Inspect(fork.Path);
            Assert.True(share.HasMax);
            Assert.True(share.HasIndex);
            Assert.True(share.HasDraft);
            Assert.Equal("trim", share.Next);
            Assert.True(File.Exists(Path.Combine(fork.Path, "source", "screenplay.index.json")));
            Assert.Contains("Full master", await File.ReadAllTextAsync(Path.Combine(fork.Path, "source", "screenplay.max.fountain")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ForkProjectAsync_does_not_change_the_active_project()
    {
        var (store, root) = MakeStore();
        try
        {
            var source = await store.CreateProjectAsync("Original", ownerUserId: "owner1");
            await store.SetProjectVisibilityModeAsync(source.Id, "Open");
            var other = await store.CreateProjectAsync("StillActive", ownerUserId: "owner1");
            // CreateProjectAsync activates each project it makes — "StillActive" is now active.

            await store.ForkProjectAsync(source.Id, "collaborator1");

            Assert.Equal(other.Id, store.ActiveProjectId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Fork_shares_history_and_sync_origin_merges_disjoint_edits_keeping_identity()
    {
        var (store, root) = MakeStore();
        try
        {
            var git = new ProjectGitRepositoryService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectGitRepositoryService>.Instance);

            var source = await store.CreateProjectAsync("Original", ownerUserId: "owner1");
            await store.SetProjectVisibilityModeAsync(source.Id, "Open");
            var sourceDir = source.Path;
            Directory.CreateDirectory(Path.Combine(sourceDir, "source"));
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "source", "screenplay.fountain"),
                "INT. A - DAY\n\nAlpha line.\n\nINT. B - DAY\n\nBeta line.\n");
            await git.CommitProjectStateAsync(sourceDir, "owner1", "Origin baseline");

            var fork = await store.ForkProjectAsync(source.Id, "collaborator1");

            // Disjoint edits: origin changes scene A's line, fork changes scene B's line.
            var originDraft = Path.Combine(sourceDir, "source", "screenplay.fountain");
            await File.WriteAllTextAsync(originDraft,
                (await File.ReadAllTextAsync(originDraft)).Replace("Alpha line.", "Alpha line, revised by origin."));
            await git.CommitProjectStateAsync(sourceDir, "owner1", "Origin edit");

            var forkDraft = Path.Combine(fork.Path, "source", "screenplay.fountain");
            await File.WriteAllTextAsync(forkDraft,
                (await File.ReadAllTextAsync(forkDraft)).Replace("Beta line.", "Beta line, revised by fork."));
            await git.CommitProjectStateAsync(fork.Path, "collaborator1", "Fork edit");

            var identityBefore = await File.ReadAllTextAsync(Path.Combine(fork.Path, "project.json"));

            var res = await git.SyncForkFromOriginAsync(fork.Path, sourceDir);

            Assert.True(res.Success, "sync failed: " + res.Message);
            Assert.False(res.HasConflicts, "disjoint edits must merge cleanly (shared history): " + res.Message);
            var merged = await File.ReadAllTextAsync(forkDraft);
            Assert.Contains("revised by origin", merged);
            Assert.Contains("revised by fork", merged);
            // The fork's identity file never takes origin content.
            Assert.Equal(identityBefore, await File.ReadAllTextAsync(Path.Combine(fork.Path, "project.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ForkProjectAsync_throws_for_unknown_source_project()
    {
        var (store, root) = MakeStore();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ForkProjectAsync("DoesNotExist", "collaborator1"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
