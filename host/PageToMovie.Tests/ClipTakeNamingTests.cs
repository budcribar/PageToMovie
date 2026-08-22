using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class ClipTakeNamingTests
{
    [Theory]
    [InlineData("scene_03_clip_07_take_01.clip.json", 1)]
    [InlineData("scene_03_clip_07_take_04.mp4", 4)]
    [InlineData("scene_03_clip_07_take_01_20260820_172934.clip.json", 1)]
    [InlineData("scene_03_clip_07.mp4", 0)]
    [InlineData("scene_01_clip_01_100.mp4", 0)]
    public void ParseTakeNumber_reads_filename_including_timestamped_leftovers(string fileName, int expected)
    {
        Assert.Equal(expected, ClipTakeNaming.ParseTakeNumber(fileName));
    }

    [Fact]
    public void ResolveTakeNumber_prefers_filename_over_sidecar_field()
    {
        Assert.Equal(4, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02_take_04.clip.json", sidecarTake: 1));
        Assert.Equal(3, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02.mp4", sidecarTake: 3));
        Assert.Equal(0, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02.mp4", sidecarTake: 0));
    }

    [Fact]
    public void Timestamped_leftover_is_not_a_stable_take_name()
    {
        Assert.True(ClipTakeNaming.IsStableTakeName("scene_03_clip_07_take_01.clip.json"));
        Assert.False(ClipTakeNaming.IsStableTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.True(ClipTakeNaming.IsTimestampedTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.True(ClipTakeNaming.IsCanonicalClipName("scene_03_clip_07.mp4"));
        Assert.False(ClipTakeNaming.IsCanonicalClipName("scene_03_clip_07_take_01.mp4"));
    }

    [Fact]
    public void AssignUniqueTakeNumbers_splits_two_take_01_files()
    {
        var a = 1;
        var b = 1;
        ClipTakeNaming.AssignUniqueTakeNumbers(new (int, Action<int>)[]
        {
            (1, n => a = n),
            (1, n => b = n),
        });
        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void JobMediaSaveKey_uses_job_and_take_not_canonical_path()
    {
        var first = ClipTakeNaming.JobMediaSaveKey("P", "job-1", "assets/video/scene_01_clip_01.mp4", 1);
        var second = ClipTakeNaming.JobMediaSaveKey("P", "job-2", "assets/video/scene_01_clip_01.mp4", 2);
        Assert.NotEqual(first, second);
        Assert.Contains("take-01", first);
        Assert.Contains("take-02", second);
    }

    [Fact]
    public void ShouldKeepLocalSidecar_when_local_is_larger()
    {
        Assert.True(ClipTakeNaming.ShouldKeepLocalSidecar(5170, 4933));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(4933, 5170));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(4933, 4933));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(0, 4933));
    }

    [Fact]
    public void Take_paths_have_no_timestamp()
    {
        Assert.Equal("scene_03_clip_07_take_05.mp4", ClipTakeNaming.TakeMp4FileName(3, 7, 5));
        Assert.Equal("scene_03_clip_07_take_05.clip.json", ClipTakeNaming.TakeSidecarFileName(3, 7, 5));
        Assert.Equal("assets/video/scene_03_clip_07_take_05.mp4", ClipTakeNaming.TakeRelativePath(3, 7, 5));
        Assert.Equal("assets/video/scene_03_clip_07.mp4", ClipTakeNaming.CanonicalRelativePath(3, 7));
        Assert.DoesNotContain("2026", ClipTakeNaming.TakeMp4FileName(3, 7, 5));
    }
}
