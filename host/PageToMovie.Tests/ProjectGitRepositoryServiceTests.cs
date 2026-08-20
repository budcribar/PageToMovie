using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests
{
    public class ProjectGitRepositoryServiceTests
    {
        private static string NewTempDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDir(string dir)
        {
            try { Directory.Delete(dir, true); } catch { /* best effort on Windows file locks */ }
        }

        private static ProjectGitRepositoryService NewService(GitOptions? git = null)
        {
            if (git is null)
                return new ProjectGitRepositoryService(NullLogger<ProjectGitRepositoryService>.Instance);
            var opts = Options.Create(new PageToMovieOptions { Git = git });
            return new ProjectGitRepositoryService(NullLogger<ProjectGitRepositoryService>.Instance, opts);
        }

        [Fact]
        public void BuildRemoteBranchName_uses_prefix_and_composite_id()
        {
            Assert.Equal("proj/alice/Buster", ProjectGitRepositoryService.BuildRemoteBranchName("alice/Buster"));
            Assert.Equal("proj/alice/Buster", ProjectGitRepositoryService.BuildRemoteBranchName("alice/Buster", "proj/"));
            Assert.Equal("backup/flat", ProjectGitRepositoryService.BuildRemoteBranchName("flat", "backup"));
        }

        [Fact]
        public void BuildGitHubHistoryUrl_builds_commits_link()
        {
            var url = ProjectGitRepositoryService.BuildGitHubHistoryUrl(
                "https://github.com/PageToMovie/Projects.git", "proj/alice/Buster");
            Assert.Equal("https://github.com/PageToMovie/Projects/commits/proj/alice/Buster", url);
        }

        [Fact]
        public async Task PushProjectAsync_fails_clearly_when_git_backup_disabled()
        {
            var dir = NewTempDir("ptm_git_push");
            try
            {
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                var service = NewService(new GitOptions { Enabled = false });
                await service.CommitProjectStateAsync(dir, "Alice", "Initial");

                var res = await service.PushProjectAsync(dir, "alice/Demo");

                Assert.False(res.Success);
                Assert.Contains("not enabled", res.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task PushProjectAsync_fails_when_remote_not_configured()
        {
            var dir = NewTempDir("ptm_git_push2");
            try
            {
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                var service = NewService(new GitOptions
                {
                    Enabled = true,
                    ProjectsRepoUrl = "",
                    Token = "",
                });
                await service.CommitProjectStateAsync(dir, "Alice", "Initial");

                var res = await service.PushProjectAsync(dir, "alice/Demo");

                Assert.False(res.Success);
                Assert.Contains("ProjectsRepoUrl", res.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task CommitProjectStateAsync_creates_a_real_commit()
        {
            var dir = NewTempDir("ptm_git");
            try
            {
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                var service = NewService();

                var info = await service.CommitProjectStateAsync(dir, "Alice", "Initial project state");

                Assert.NotNull(info);
                Assert.Equal(40, info.CommitHash.Length); // real SHA-1 hex, not a fake "git_" prefix
                Assert.Equal("Alice", info.Author);

                using var repo = new Repository(dir);
                Assert.Single(repo.Commits);
                Assert.Equal(info.CommitHash, repo.Head.Tip.Sha);
                var blob = (Blob)repo.Head.Tip["project.json"].Target;
                Assert.Contains("Demo", blob.GetContentText());
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task CommitProjectStateAsync_is_a_noop_when_nothing_changed()
        {
            var dir = NewTempDir("ptm_git");
            try
            {
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                var service = NewService();

                var first = await service.CommitProjectStateAsync(dir, "Alice", "Initial");
                var second = await service.CommitProjectStateAsync(dir, "Alice", "Nothing changed");

                Assert.Equal(first.CommitHash, second.CommitHash);
                using var repo = new Repository(dir);
                Assert.Single(repo.Commits); // no empty second commit was created
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task CommitProjectStateAsync_never_tracks_video_binaries()
        {
            var dir = NewTempDir("ptm_git");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "assets", "video"));
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                File.WriteAllText(Path.Combine(dir, "assets", "video", "scene_01_clip_01.mp4"), "fake video bytes");

                var service = NewService();
                await service.CommitProjectStateAsync(dir, "Alice", "Initial");

                using var repo = new Repository(dir);
                Assert.NotNull(repo.Head.Tip["project.json"]);
                Assert.Null(repo.Head.Tip["assets/video/scene_01_clip_01.mp4"]);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task CommitProjectStateAsync_tracks_clip_sidecars_under_video_dir()
        {
            // Sidecars are the project's only pointers to provider-hosted video — a repo that
            // ignored all of assets/video/ restored with no clips at all.
            var dir = NewTempDir("ptm_git");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "assets", "video"));
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                File.WriteAllText(
                    Path.Combine(dir, "assets", "video", "scene_01_clip_01_take_01.clip.json"),
                    """{"source_url":"https://example.test/v.mp4"}""");
                File.WriteAllText(Path.Combine(dir, "assets", "video", "scene_01_clip_01.mp4"), "fake video bytes");

                var service = NewService();
                await service.CommitProjectStateAsync(dir, "Alice", "Initial");

                using var repo = new Repository(dir);
                Assert.NotNull(repo.Head.Tip["assets/video/scene_01_clip_01_take_01.clip.json"]);
                Assert.Null(repo.Head.Tip["assets/video/scene_01_clip_01.mp4"]);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task CommitProjectStateAsync_heals_legacy_gitignore_that_hid_the_video_dir()
        {
            var dir = NewTempDir("ptm_git");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "assets", "video"));
                File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
                File.WriteAllText(Path.Combine(dir, ".gitignore"),
                    "assets/video/\n*.mp4\n*.webm\n*.mov\n*.wav\n*.avi\n");
                File.WriteAllText(
                    Path.Combine(dir, "assets", "video", "scene_01_clip_01_take_01.clip.json"),
                    """{"source_url":"https://example.test/v.mp4"}""");

                var service = NewService();
                await service.CommitProjectStateAsync(dir, "Alice", "Initial");

                Assert.DoesNotContain("assets/video/",
                    File.ReadAllLines(Path.Combine(dir, ".gitignore")).Select(l => l.Trim()));
                using var repo = new Repository(dir);
                Assert.NotNull(repo.Head.Tip["assets/video/scene_01_clip_01_take_01.clip.json"]);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task SyncForkFromOriginAsync_fails_cleanly_when_parent_has_no_commits()
        {
            var fork = NewTempDir("ptm_fork");
            var parent = NewTempDir("ptm_parent");
            try
            {
                var service = NewService();
                var res = await service.SyncForkFromOriginAsync(fork, parent);

                Assert.False(res.Success);
                Assert.False(res.HasConflicts);
            }
            finally
            {
                DeleteDir(fork);
                DeleteDir(parent);
            }
        }

        [Fact]
        public async Task SyncForkFromOriginAsync_merges_non_conflicting_changes_from_parent()
        {
            var fork = NewTempDir("ptm_fork");
            var parent = NewTempDir("ptm_parent");
            try
            {
                var service = NewService();

                File.WriteAllText(Path.Combine(parent, "parent_only.txt"), "from parent");
                await service.CommitProjectStateAsync(parent, "Owner", "Parent update");

                File.WriteAllText(Path.Combine(fork, "fork_only.txt"), "from fork");
                await service.CommitProjectStateAsync(fork, "Collaborator", "Fork edit");

                var res = await service.SyncForkFromOriginAsync(fork, parent);

                Assert.True(res.Success);
                Assert.False(res.HasConflicts);
                Assert.True(File.Exists(Path.Combine(fork, "parent_only.txt")), "parent's file must be merged in");
                Assert.True(File.Exists(Path.Combine(fork, "fork_only.txt")), "fork's own file must be preserved");
            }
            finally
            {
                DeleteDir(fork);
                DeleteDir(parent);
            }
        }

        [Fact]
        public async Task SyncForkFromOriginAsync_reports_conflicts_without_committing()
        {
            var fork = NewTempDir("ptm_fork");
            var parent = NewTempDir("ptm_parent");
            try
            {
                var service = NewService();

                File.WriteAllText(Path.Combine(parent, "shared.txt"), "parent version");
                await service.CommitProjectStateAsync(parent, "Owner", "Parent edit");

                File.WriteAllText(Path.Combine(fork, "shared.txt"), "fork version");
                await service.CommitProjectStateAsync(fork, "Collaborator", "Fork edit");

                using var forkRepoBefore = new Repository(fork);
                var headBefore = forkRepoBefore.Head.Tip.Sha;

                var res = await service.SyncForkFromOriginAsync(fork, parent);

                Assert.False(res.Success);
                Assert.True(res.HasConflicts);

                using var forkRepoAfter = new Repository(fork);
                // Must not have silently committed a resolution on the caller's behalf.
                Assert.Equal(headBefore, forkRepoAfter.Head.Tip.Sha);
            }
            finally
            {
                DeleteDir(fork);
                DeleteDir(parent);
            }
        }

        [Fact]
        public async Task UndoLastCommitAsync_reverts_project_to_previous_state()
        {
            var dir = NewTempDir("ptm_git_undo");
            try
            {
                var service = NewService();

                // Commit 1: Initial state
                File.WriteAllText(Path.Combine(dir, "scene_1.txt"), "Original scene content");
                var commit1 = await service.CommitProjectStateAsync(dir, "Operator", "Initial scene 1");

                // Commit 2: Accidental edit
                File.WriteAllText(Path.Combine(dir, "scene_1.txt"), "Accidental broken content");
                var commit2 = await service.CommitProjectStateAsync(dir, "Operator", "Accidental edit");
                Assert.Equal("Accidental broken content", File.ReadAllText(Path.Combine(dir, "scene_1.txt")));

                // Undo
                var undoCommit = await service.UndoLastCommitAsync(dir, "Operator");

                Assert.NotNull(undoCommit);
                Assert.Equal("Original scene content", File.ReadAllText(Path.Combine(dir, "scene_1.txt")));
                Assert.Contains("Undo: Revert", undoCommit.Message);

                // Check history
                var history = await service.GetCommitHistoryAsync(dir);
                Assert.Equal(3, history.Count);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Fact]
        public async Task RevertToCommitAsync_reverts_project_to_specific_hash()
        {
            var dir = NewTempDir("ptm_git_revert");
            try
            {
                var service = NewService();

                File.WriteAllText(Path.Combine(dir, "scene_1.txt"), "Version 1");
                var c1 = await service.CommitProjectStateAsync(dir, "Operator", "Version 1");

                File.WriteAllText(Path.Combine(dir, "scene_1.txt"), "Version 2");
                await service.CommitProjectStateAsync(dir, "Operator", "Version 2");

                File.WriteAllText(Path.Combine(dir, "scene_1.txt"), "Version 3");
                await service.CommitProjectStateAsync(dir, "Operator", "Version 3");

                // Revert to Version 1
                var revertCommit = await service.RevertToCommitAsync(dir, c1.CommitHash, "Operator");

                Assert.NotNull(revertCommit);
                Assert.Equal("Version 1", File.ReadAllText(Path.Combine(dir, "scene_1.txt")));
            }
            finally
            {
                DeleteDir(dir);
            }
        }
    }
}
