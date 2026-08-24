using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ClipVersionAndUncommittedTests
{
    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ClipVersions_and_UncommittedStatus_work_end_to_end()
    {
        var root = NewTempDir("ptm_clip_versions");
        try
        {
            var projectDir = Path.Combine(root, "projects", "TestProj");
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
            var store = new ProjectStore(opts);

            File.WriteAllText(Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4"), "take_1");
            File.WriteAllText(Path.Combine(videoDir, "scene_01_clip_01_take_01.clip.json"), """{"take":1,"visual_prompt":"Take 1 Prompt"}""");
            File.WriteAllText(Path.Combine(videoDir, "scene_01_clip_01_take_02.mp4"), "take_2");
            File.WriteAllText(Path.Combine(videoDir, "scene_01_clip_01_take_02.clip.json"), """{"take":2,"visual_prompt":"Active Prompt"}""");
            ClipSidecarService.WriteCurrentTake(videoDir, 1, 1, 2);

            var versions = await store.GetClipVersionsAsync("TestProj", 1, 1);
            Assert.NotNull(versions);
            Assert.True(versions.Count >= 2);
            Assert.Contains(versions, v => v.IsCurrent && v.VisualPrompt == "Active Prompt");
            Assert.Contains(versions, v => !v.IsCurrent && v.VisualPrompt == "Take 1 Prompt");

            var promoted = await store.PromoteClipVersionAsync("TestProj", 1, 1, "scene_01_clip_01_take_01.mp4");
            Assert.True(promoted);
            Assert.Equal(1, ClipSidecarService.ReadCurrentTake(videoDir, 1, 1));
            Assert.Equal(
                Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4"),
                ClipSidecarService.CurrentTakePath(videoDir, 1, 1));
            Assert.Equal("take_2", File.ReadAllText(Path.Combine(videoDir, "scene_01_clip_01_take_02.mp4")));
        }
        finally
        {
            DeleteDir(root);
        }
    }

    [Fact]
    public async Task Current_pointer_stays_on_stable_take_when_duplicate_take_numbers_are_repaired()
    {
        var root = NewTempDir("ptm_clip_current_collision");
        try
        {
            var projectDir = Path.Combine(root, "projects", "TestProj");
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);
            var leftover = Path.Combine(videoDir, "scene_01_clip_01_take_01_20260820_120000.clip.json");
            File.WriteAllText(leftover, """{"take":1,"visual_prompt":"Leftover"}""");
            File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllText(
                Path.Combine(videoDir, "scene_01_clip_01_take_01.clip.json"),
                """{"take":1,"visual_prompt":"Stable current"}""");
            ClipSidecarService.WriteCurrentTake(videoDir, 1, 1, 1);

            var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = root }));
            var versions = await store.GetClipVersionsAsync("TestProj", 1, 1);

            var current = Assert.Single(versions, v => v.IsCurrent);
            Assert.Equal("scene_01_clip_01_take_01.mp4", current.Mp4FileName);
            Assert.Equal("Stable current", current.VisualPrompt);
        }
        finally
        {
            DeleteDir(root);
        }
    }
}
