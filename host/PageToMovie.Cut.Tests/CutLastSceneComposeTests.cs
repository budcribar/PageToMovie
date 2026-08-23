using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutLastSceneComposeTests
{
    [Fact]
    public void MarkOut_zero_picks_up_sidecar_duration_and_persists()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_04_clip_01_take_05.mp4", "assets/video/scene_04_clip_01_take_05.mp4", 44_780),
            new("scene_04_clip_01.clip.json", "assets/video/scene_04_clip_01.clip.json", 80,
                """{"duration_seconds":6}"""),
        ]);
        var clip = Assert.Single(clips);
        Assert.True(clip.HoldsPicture);
        Assert.Equal(0, clip.ActiveTakeNumber);
        Assert.Equal(6, clip.DurationSec);
        Assert.Equal(0, clip.MarkIn);
        Assert.Equal(6, clip.MarkOut);
        Assert.True(clip.MarksRepaired);

        var saved = """{"version":1,"clips":[{"scene":4,"clip":1,"markIn":0,"markOut":0}]}""";
        Assert.True(CutProjectFile.TryApply([clip], saved, out _));
        Assert.Equal(6, clip.MarkOut);
        Assert.False(clip.EnsureInOutFromDuration());

        var json = CutProjectFile.Serialize([clip], null);
        Assert.Contains("\"markOut\": 6", json, StringComparison.Ordinal);

        var reload = CutClipList.FromFiles(
        [
            new("scene_04_clip_01_take_05.mp4", "assets/video/scene_04_clip_01_take_05.mp4", 44_780),
            new("scene_04_clip_01.clip.json", "assets/video/scene_04_clip_01.clip.json", 80,
                """{"duration_seconds":6}"""),
        ]).ToList();
        Assert.True(CutProjectFile.TryApply(reload, json, out _));
        Assert.Equal(6, reload[0].MarkOut);
        Assert.Equal(6, reload[0].DurationSec);
    }

    [Fact]
    public void Missing_last_scene_does_not_inherit_previous_picture()
    {
        var teacher = PlayableClip(3, 1, 40, "blob:teacher");
        var credits = new CutClip { Scene = 4, Clip = 1 };
        credits.SetDuration(6);
        Assert.True(credits.EnsureInOutFromDuration());
        Assert.True(credits.HoldsPicture);
        Assert.True(credits.Missing);

        var titles = new List<CutTextClip>
        {
            new() { Id = "end-1", Text = "Title", StartSec = 40, Seconds = 2 },
        };
        var payload = CutComposeService.BuildExportPayload([teacher, credits], titles);

        Assert.Equal(2, payload.Count);
        Assert.False(payload[0].Hold);
        Assert.Equal("blob:teacher", payload[0].Url);
        Assert.True(payload[1].Hold);
        Assert.Null(payload[1].Url);
        Assert.NotEqual(payload[0].Url, payload[1].Url);
        Assert.Equal(6, payload[1].Duration);
        Assert.Equal(6, payload[1].MarkOut);
        var overlay = Assert.Single(payload[1].Texts);
        Assert.Equal("Title", overlay.Text);
        Assert.Equal(0, overlay.Start, 5);
        Assert.Empty(payload[0].Texts);

        var layout = CutTimelineLayout.Build([teacher, credits], 10);
        Assert.Equal(46, layout.TotalSec, 5);
        Assert.Equal(40, layout.Lanes[1].StartSec, 5);
        Assert.Equal(6, layout.Lanes[1].WidthSec, 5);
        Assert.NotEqual(teacher.FileName, credits.FileName);
    }

    [Fact]
    public void End_titles_overlay_the_credits_scene_not_the_teacher()
    {
        var s01 = PlayableClip(1, 1, 50, "blob:s01");
        var s03 = PlayableClip(3, 1, 52, "blob:teacher");
        var s04 = new CutClip { Scene = 4, Clip = 1 };
        s04.SetDuration(6);
        s04.EnsureInOutFromDuration();
        var titles = new List<CutTextClip>
        {
            new() { Id = "t1", Text = "Title", StartSec = 101.9, Seconds = 2 },
            new() { Id = "t2", Text = "Mary had a little lamb", StartSec = 102.4, Seconds = 2 },
        };

        var clips = new[] { s01, s03, s04 };
        Assert.Equal(108, CutJitPlay.TotalSec(clips), 5);
        var overlays = CutTextTrack.OverlaysForCompose(clips, titles);
        Assert.Equal(2, overlays.Count);
        Assert.All(overlays, o => Assert.Equal(2, o.ClipIndex));
        Assert.True(overlays[0].LocalStart < 1);
        var payload = CutComposeService.BuildExportPayload(clips, titles);
        Assert.Empty(payload[1].Texts);
        Assert.Equal(2, payload[2].Texts.Count);
        Assert.True(payload[2].Hold);
        Assert.Null(payload[2].Url);
        Assert.Equal("blob:teacher", payload[1].Url);
    }

    [Fact]
    public void Same_slot_playable_take_without_current_json_is_bound()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_03_clip_01_take_01.mp4", "assets/video/scene_03_clip_01_take_01.mp4", 200_000),
            new("scene_03_clip_01.current.json", "assets/video/scene_03_clip_01.current.json", 40,
                """{"take":1}"""),
            new("scene_04_clip_01_take_02.mp4", "assets/video/scene_04_clip_01_take_02.mp4", 180_000),
            new("scene_04_clip_01_take_05.mp4", "assets/video/scene_04_clip_01_take_05.mp4", 44_780),
        ]).ToList();

        Assert.Equal(2, clips.Count);
        Assert.Equal(1, clips[0].ActiveTakeNumber);
        Assert.Equal(2, clips[1].ActiveTakeNumber);
        Assert.Equal("scene_04_clip_01_take_02.mp4", clips[1].FileName);
        Assert.False(clips[1].HoldsPicture);
        Assert.DoesNotContain(clips[1].Takes, t => t.FileName.Contains("scene_03", StringComparison.Ordinal));
        Assert.DoesNotContain(clips[1].Takes, t => t.Take == 5);
    }

    [Fact]
    public void Stub_last_take_without_current_json_is_not_bound()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_03_clip_01_take_01.mp4", "assets/video/scene_03_clip_01_take_01.mp4", 200_000),
            new("scene_03_clip_01.current.json", "assets/video/scene_03_clip_01.current.json", 40,
                """{"take":1}"""),
            new("scene_04_clip_01_take_04.mp4", "assets/video/scene_04_clip_01_take_04.mp4", 44_780),
            new("scene_04_clip_01_take_05.mp4", "assets/video/scene_04_clip_01_take_05.mp4", 44_780),
            new("scene_04_clip_01.clip.json", "assets/video/scene_04_clip_01.clip.json", 80,
                """{"duration_seconds":6,"script_text":"","visual_prompt":""}"""),
        ]).ToList();

        Assert.Equal(2, clips.Count);
        Assert.Equal(0, clips[1].ActiveTakeNumber);
        Assert.Empty(clips[1].Takes);
        Assert.True(clips[1].HoldsPicture);
        Assert.Equal(6, clips[1].MarkOut);
        var teacher = PlayableClip(3, 1, 40, "blob:teacher");
        teacher.Takes[0].PreviewUrl = "blob:teacher";
        var payload = CutComposeService.BuildExportPayload([teacher, clips[1]]);
        Assert.Equal("blob:teacher", payload[0].Url);
        Assert.True(payload[1].Hold);
        Assert.Null(payload[1].Url);
    }

    [Fact]
    public void Compose_keeps_every_film_order_slot()
    {
        var ready = PlayableClip(1, 1, 4, "blob:s01");
        var missing = new CutClip { Scene = 4, Clip = 1 };
        missing.SetDuration(6);
        var all = CutTransport.ComposeClips([ready, missing]);
        Assert.Equal(2, all.Count);
        Assert.Single(CutTransport.PlayableClips([ready, missing]));
        Assert.True(CutTransport.CanPlay([ready, missing]));
    }

    private static CutClip PlayableClip(int scene, int clip, double duration, string preview)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            SizeBytes = 200_000,
            PreviewUrl = preview,
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        c.SetDuration(duration);
        return c;
    }
}
