using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutClipNamingTests
{
    [Theory]
    [InlineData("scene_03_clip_07_take_04.mp4", 3, 7)]
    [InlineData("SCENE_03_CLIP_07_TAKE_04.MP4", 3, 7)]
    [InlineData("assets/video/scene_03_clip_07_take_01.mp4", 3, 7)]
    [InlineData("scene_03_clip_07.current.json", 3, 7)]
    public void Parses_scene_and_clip(string name, int scene, int clip)
    {
        Assert.True(CutClipNaming.TryParseSceneClip(name, out var s, out var c));
        Assert.Equal(scene, s);
        Assert.Equal(clip, c);
    }

    [Fact]
    public void Bare_alias_mp4_is_legacy_and_not_usable()
    {
        Assert.True(CutClipNaming.IsLegacyAliasMp4("scene_03_clip_07.mp4"));
        Assert.False(CutClipNaming.IsUsableClipMp4("scene_03_clip_07.mp4"));
        Assert.False(CutClipNaming.IsStableTakeName("scene_03_clip_07.mp4"));
        Assert.Equal(0, CutClipNaming.ParseTakeNumber("scene_03_clip_07.mp4"));
    }

    [Fact]
    public void Stable_take_is_usable()
    {
        Assert.True(CutClipNaming.IsStableTakeName("scene_03_clip_07_take_04.mp4"));
        Assert.True(CutClipNaming.IsUsableClipMp4("scene_03_clip_07_take_04.mp4"));
        Assert.False(CutClipNaming.IsLegacyAliasMp4("scene_03_clip_07_take_04.mp4"));
        Assert.Equal(4, CutClipNaming.ParseTakeNumber("scene_03_clip_07_take_04.mp4"));
    }

    [Theory]
    [InlineData("scene_03_clip_07.mp4")]
    [InlineData("scene_03_clip_07_take_04_20260101120000.mp4")]
    [InlineData("scene_03_clip_07_take_01_20260820_172934.mp4")]
    [InlineData("scene_03_clip_07_20260101120000.mp4")]
    [InlineData("scene_03.mp4")]
    [InlineData("scene_03_clip_07.mp4.client.json")]
    public void Rejects_alias_timestamp_leftovers_and_non_clips(string name)
    {
        Assert.False(CutClipNaming.IsUsableClipMp4(name));
    }

    [Fact]
    public void Timestamped_take_is_not_stable()
    {
        Assert.True(CutClipNaming.IsTimestampedTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.False(CutClipNaming.IsStableTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.True(CutClipNaming.IsCurrentPointerName("scene_03_clip_07.current.json"));
    }

    [Fact]
    public void FromFiles_ignores_alias_and_seeds_current_json_only()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_01_clip_01_take_01.mp4", "assets/video/scene_01_clip_01_take_01.mp4", 2000),
            new("scene_01_clip_01_take_02.mp4", "assets/video/scene_01_clip_01_take_02.mp4", 2000),
            new("scene_01_clip_01.mp4", "assets/video/scene_01_clip_01.mp4", 9999),
            new("scene_01_clip_01_take_02_20260101120000.mp4", "history/scene_01_clip_01_take_02_20260101120000.mp4", 2000),
            new("scene_01_clip_01.current.json", "assets/video/scene_01_clip_01.current.json", 40,
                """{"scene":1,"clip":1,"take":2}"""),
            new("scene_02_clip_01.mp4", "assets/video/scene_02_clip_01.mp4", 2000),
        ]);

        var first = Assert.Single(clips);
        Assert.Equal((1, 1), (first.Scene, first.Clip));
        Assert.Equal(new[] { 1, 2 }, first.Takes.Select(t => t.Take));
        Assert.Equal(2, first.ActiveTakeNumber);
        Assert.Equal(2, first.SelectedTakeNumber);
        Assert.Equal("scene_01_clip_01_take_02.mp4", first.FileName);
        Assert.DoesNotContain(first.Takes, t => t.FileName == "scene_01_clip_01.mp4");

        first.SelectTake(1);
        Assert.Equal(1, first.SelectedTakeNumber);
        Assert.Equal(1, first.ActiveTakeNumber);
        Assert.Equal("scene_01_clip_01_take_01.mp4", first.FileName);
        Assert.Equal("assets/video/scene_01_clip_01.current.json", first.PointerRelativePath);
    }

    [Fact]
    public void FromFiles_without_current_json_has_no_current_take()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_03_clip_07_take_01.mp4", "assets/video/scene_03_clip_07_take_01.mp4", 2000),
            new("scene_03_clip_07_take_04.mp4", "assets/video/scene_03_clip_07_take_04.mp4", 2000),
        ]);

        var clip = Assert.Single(clips);
        Assert.Equal(0, clip.ActiveTakeNumber);
        Assert.Equal(0, clip.SelectedTakeNumber);
        Assert.Null(clip.SelectedTake);
        Assert.True(clip.Missing);
    }

    [Fact]
    public void Pointer_to_missing_take_does_not_fall_back()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_01_clip_01_take_01.mp4", "assets/video/scene_01_clip_01_take_01.mp4", 2000),
            new("scene_01_clip_01.current.json", "assets/video/scene_01_clip_01.current.json", 20,
                """{"take":3}"""),
        ]);

        var clip = Assert.Single(clips);
        Assert.Equal(3, clip.ActiveTakeNumber);
        Assert.Equal(3, clip.SelectedTakeNumber);
        Assert.Null(clip.SelectedTake);
        Assert.Contains("take 3 is missing", clip.MissingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsePointerTake_reads_take_field()
    {
        Assert.Equal(4, CutClipList.ParsePointerTake("""{"scene":3,"clip":7,"take":4}"""));
        Assert.Equal(0, CutClipList.ParsePointerTake("""{"scene":3}"""));
        Assert.Equal(0, CutClipList.ParsePointerTake("not-json"));
    }

    [Fact]
    public void CurrentPointerJson_has_take_only_no_alias_path()
    {
        var json = CutClipNaming.CurrentPointerJson(3, 7, 4);
        Assert.Equal(4, CutClipList.ParsePointerTake(json));
        Assert.DoesNotContain(".mp4", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("scene_03_clip_07.current.json", CutClipNaming.CurrentTakePointerFileName(3, 7));
    }
}
