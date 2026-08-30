using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public sealed class PlayMediaIndexTests
{
    [Fact]
    public void Scene_detail_hit_then_summary_change_misses_only_that_scene()
    {
        var index = new PlayMediaIndex();
        var s1 = new SceneSummary { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2 };
        var s2 = new SceneSummary { SceneNumber = 2, ClipCount = 1, ClipsOnDisk = 1 };
        var d1 = Detail(1, (1, "scene_01_clip_01_take_01.mp4", 1000), (2, "scene_01_clip_02_take_01.mp4", 2000));
        var d2 = Detail(2, (1, "scene_02_clip_01_take_01.mp4", 3000));

        index.RememberSceneDetail("p", d1, s1);
        index.RememberSceneDetail("p", d2, s2);

        Assert.True(index.TryGetSceneDetail("p", 1, PlayMediaIndex.FingerprintSummary(s1), out var hit1));
        Assert.Same(d1, hit1);
        Assert.True(index.TryGetSceneDetail("p", 2, PlayMediaIndex.FingerprintSummary(s2), out _));

        var s1Changed = new SceneSummary { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 1, HasStaleClips = true };
        index.SyncSceneList("p", [s1Changed, s2]);

        Assert.False(index.TryGetSceneDetail("p", 1, PlayMediaIndex.FingerprintSummary(s1Changed), out _));
        Assert.True(index.TryGetSceneDetail("p", 2, PlayMediaIndex.FingerprintSummary(s2), out var still2));
        Assert.Same(d2, still2);
    }

    [Fact]
    public void Clip_url_hit_requires_same_take_fingerprint()
    {
        var index = new PlayMediaIndex();
        var take1 = PlayMediaIndex.FingerprintClip(
            new ClipSummary { ClipNumber = 1, FileName = "scene_01_clip_01_take_01.mp4", SizeBytes = 10 },
            currentTakeRel: "assets/video/scene_01_clip_01_take_01.mp4");
        var take2 = PlayMediaIndex.FingerprintClip(
            new ClipSummary { ClipNumber = 1, FileName = "scene_01_clip_01_take_02.mp4", SizeBytes = 11 },
            currentTakeRel: "assets/video/scene_01_clip_01_take_02.mp4");

        index.RememberClipUrl("p", 1, 1, take1, "blob:take1");

        Assert.True(index.TryGetClipUrl("p", 1, 1, take1, out var url));
        Assert.Equal("blob:take1", url);
        Assert.False(index.TryGetClipUrl("p", 1, 1, take2, out _));
        Assert.NotEqual(take1, take2);
    }

    [Fact]
    public void Invalidate_clip_drops_that_clip_and_its_scene_segment_only()
    {
        var index = new PlayMediaIndex();
        var take1 = "take-1";
        var takeOther = "take-other";
        index.RememberClipUrl("p", 1, 1, take1, "blob:s1c1");
        index.RememberClipUrl("p", 1, 2, take1, "blob:s1c2");
        index.RememberClipUrl("p", 2, 1, takeOther, "blob:s2c1");
        index.RememberSceneSegment("p", 1, "seg-1", new ClientWipSegment
        {
            SceneNumber = 1,
            Url = "blob:scene1",
            RelativeSrc = "assets/video/scene_01.mp4",
        });
        index.RememberSceneSegment("p", 2, "seg-2", new ClientWipSegment
        {
            SceneNumber = 2,
            Url = "blob:scene2",
            RelativeSrc = "assets/video/scene_02.mp4",
        });

        index.InvalidateClip("p", 1, 1);

        Assert.False(index.TryGetClipUrl("p", 1, 1, take1, out _));
        Assert.True(index.TryGetClipUrl("p", 1, 2, take1, out _));
        Assert.True(index.TryGetClipUrl("p", 2, 1, takeOther, out _));
        Assert.False(index.TryGetSceneSegment("p", 1, "seg-1", out _));
        Assert.True(index.TryGetSceneSegment("p", 2, "seg-2", out _));
    }

    [Fact]
    public void Remember_detail_without_summary_keeps_warm_summary_fingerprint()
    {
        var index = new PlayMediaIndex();
        var summary = new SceneSummary { SceneNumber = 1, ClipCount = 1, ClipsOnDisk = 1, Status = "complete" };
        var detail = Detail(1, (1, "scene_01_clip_01_take_01.mp4", 1000));
        index.RememberSceneDetail("p", detail, summary);

        index.RememberSceneDetail("p", detail, summary: null);

        Assert.True(index.TryGetSceneDetail("p", 1, PlayMediaIndex.FingerprintSummary(summary), out var hit));
        Assert.Same(detail, hit);
    }

    [Fact]
    public void Scene_group_hits_until_the_scene_list_fingerprint_changes()
    {
        var index = new PlayMediaIndex();
        var scenes = new[]
        {
            new SceneSummary { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2 },
            new SceneSummary { SceneNumber = 2, ClipCount = 1, ClipsOnDisk = 1 },
        };
        index.RememberSceneGroup("p", scenes, [1, 2]);

        Assert.True(index.TryGetSceneGroup("p", scenes, out var playable));
        Assert.Equal(new[] { 1, 2 }, playable);

        var grown = scenes.Concat([new SceneSummary { SceneNumber = 3, ClipCount = 1, ClipsOnDisk = 1 }]).ToArray();
        Assert.False(index.TryGetSceneGroup("p", grown, out _));
    }

    [Fact]
    public void Review_razor_still_binds_subtitle_from_ReviewPage_key()
    {
        var page = File.ReadAllText(ReviewPagePath("Review.razor"));
        Assert.Contains("L[\"ReviewPage.Subtitle\"]", page, StringComparison.Ordinal);
        Assert.DoesNotContain("north star is automatic generation", page, StringComparison.Ordinal);
    }

    private static SceneDetail Detail(int scene, params (int Clip, string File, long Size)[] clips) =>
        new()
        {
            SceneNumber = scene,
            ClipCount = clips.Length,
            ClipsOnDisk = clips.Length,
            Clips = clips.Select(c => new ClipSummary
            {
                ClipNumber = c.Clip,
                FileName = c.File,
                SizeBytes = c.Size,
                OnDisk = true,
            }).ToList(),
        };

    private static string ReviewPagePath(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return candidate;
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
