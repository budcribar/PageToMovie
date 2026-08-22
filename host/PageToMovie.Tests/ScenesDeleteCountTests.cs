using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Web.Components.Pages;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// After a scene is deleted, Film a/b counts (scene-index summary, per-row clips,
/// <c>data-scene-count</c> / <c>data-clip-count</c> / <c>data-clips-on-disk</c> /
/// <c>data-scenes-complete</c>) must match the remaining shot plan — no stale total.
/// </summary>
public class ScenesDeleteCountTests
{
    [Fact]
    public void ApplyDeletedSceneLocally_refreshes_bound_scene_and_clip_counts()
    {
        var page = new Scenes();
        var list = page.List;
        list._scenes = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2, ClipsComplete = true },
            new() { SceneNumber = 2, ClipCount = 4, ClipsOnDisk = 3, ClipsComplete = false },
            new() { SceneNumber = 3, ClipCount = 1, ClipsOnDisk = 1, ClipsComplete = true },
            new() { SceneNumber = 4, ClipCount = 3, ClipsOnDisk = 0, ClipsComplete = false },
        };
        list._selectedScene = 2;
        list._detail = new SceneDetail { SceneNumber = 2, ClipCount = 4, ClipsOnDisk = 3 };
        list._selected.Add(2);

        var before = list.MovieReadiness;
        Assert.Equal(4, before.Scenes);
        Assert.Equal(10, before.ClipsPlanned);
        Assert.Equal(6, before.ClipsOnDisk);
        Assert.Equal(2, before.ScenesComplete);

        list.ApplyDeletedSceneLocally(2);

        Assert.Equal(3, list._scenes.Count);
        Assert.DoesNotContain(list._scenes, s => s.SceneNumber == 2);
        Assert.DoesNotContain(2, list._selected);
        Assert.NotEqual(2, list._selectedScene);
        Assert.True(list._detail is null || list._detail.SceneNumber != 2);

        // Same sums Scenes.razor binds on data-scene-count / data-clip-count /
        // data-clips-on-disk / data-scenes-complete (and the index 3/4-style labels).
        Assert.Equal(3, list._scenes.Count);
        Assert.Equal(6, list._scenes.Sum(s => s.ClipCount));
        Assert.Equal(3, list._scenes.Sum(s => s.ClipsOnDisk));
        Assert.Equal(2, list._scenes.Count(s => s.ClipsComplete));

        var after = list.MovieReadiness;
        Assert.Equal(3, after.Scenes);
        Assert.Equal(6, after.ClipsPlanned);
        Assert.Equal(3, after.ClipsOnDisk);
        Assert.Equal(2, after.ScenesComplete);
        Assert.Equal(3, after.ClipsMissing);
    }

    [Fact]
    public void ReconcileSelectedSceneWithList_drops_open_scene_that_is_gone()
    {
        var page = new Scenes();
        var list = page.List;
        list._scenes = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 1 },
            new() { SceneNumber = 3, ClipCount = 2, ClipsOnDisk = 2, ClipsComplete = true },
        };
        list._selectedScene = 2;
        list._detail = new SceneDetail { SceneNumber = 2, ClipCount = 4, ClipsOnDisk = 3 };

        list.ReconcileSelectedSceneWithList();

        Assert.Equal(3, list._selectedScene);
        Assert.True(list._detail is null || list._detail.SceneNumber != 2);
        Assert.Equal(4, list._scenes.Sum(s => s.ClipCount));
        Assert.Equal(3, list._scenes.Sum(s => s.ClipsOnDisk));
    }

    [Fact]
    public async Task DeleteScene_list_counts_match_remaining_scenes_even_with_read_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_delscene_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(Path.Combine(proj, "assets", "video"));
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.json"}""");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.json"), """
            {
              "scenes": [
                { "scene_number": 1, "setting": "Kitchen", "veo_clips": [
                  { "clip_number": 1 }, { "clip_number": 2 } ] },
                { "scene_number": 2, "setting": "Yard", "veo_clips": [
                  { "clip_number": 1 }, { "clip_number": 2 }, { "clip_number": 3 }, { "clip_number": 4 } ] },
                { "scene_number": 3, "setting": "Porch", "veo_clips": [
                  { "clip_number": 1 } ] },
                { "scene_number": 4, "setting": "Street", "veo_clips": [
                  { "clip_number": 1 }, { "clip_number": 2 }, { "clip_number": 3 } ] }
              ]
            }
            """);
        var video = Path.Combine(proj, "assets", "video");
        WriteDummyMp4(video, "scene_01_clip_01.mp4");
        WriteDummyMp4(video, "scene_01_clip_02.mp4");
        WriteDummyMp4(video, "scene_02_clip_01.mp4");
        WriteDummyMp4(video, "scene_02_clip_02.mp4");
        WriteDummyMp4(video, "scene_02_clip_03.mp4");
        WriteDummyMp4(video, "scene_03_clip_01.mp4");

        var opts = Options.Create(new PageToMovieOptions
        {
            WorkspaceRoot = root,
            EnableReadCaches = true,
        });
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var store = new ProjectStore(opts, sceneListCache: cache);

        try
        {
            var before = await store.ListScenesAsync("Demo", probeDurations: false);
            Assert.Equal(4, before.Count);
            Assert.Equal(10, before.Sum(s => s.ClipCount));
            Assert.Equal(6, before.Sum(s => s.ClipsOnDisk));
            Assert.Equal(2, before.Count(s => s.ClipsComplete));

            Assert.True(store.DeleteScene("Demo", 2));

            var after = await store.ListScenesAsync("Demo", probeDurations: false);
            Assert.Equal(3, after.Count);
            Assert.DoesNotContain(after, s => s.SceneNumber == 2);
            Assert.Equal(6, after.Sum(s => s.ClipCount));
            Assert.Equal(3, after.Sum(s => s.ClipsOnDisk));
            Assert.Equal(2, after.Count(s => s.ClipsComplete));
            Assert.Equal(new[] { 1, 3, 4 }, after.Select(s => s.SceneNumber).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    private static void WriteDummyMp4(string dir, string name) =>
        File.WriteAllBytes(Path.Combine(dir, name), new byte[2048]);
}
