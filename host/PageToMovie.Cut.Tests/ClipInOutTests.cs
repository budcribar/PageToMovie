using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class ClipInOutTests
{
    [Fact]
    public void Clamps_in_below_zero_and_out_past_duration()
    {
        var (inn, outt) = ClipInOut.Clamp(-2, 99, 5);
        Assert.Equal(0, inn);
        Assert.Equal(5, outt);
    }

    [Fact]
    public void Clamps_out_before_in()
    {
        var (inn, outt) = ClipInOut.Clamp(3, 1, 5);
        Assert.Equal(3, inn);
        Assert.True(outt >= inn);
        Assert.Equal(3 + ClipInOut.MinSpanSeconds, outt, 5);
    }

    [Fact]
    public void Empty_duration_is_zero_range()
    {
        Assert.Equal((0, 0), ClipInOut.Clamp(1, 2, 0));
        Assert.Equal((0, 0), ClipInOut.Clamp(1, 2, double.NaN));
        Assert.Equal((0, 0), ClipInOut.Clamp(1, 2, -4));
    }

    [Fact]
    public void Near_end_keeps_minimum_span()
    {
        var (inn, outt) = ClipInOut.Clamp(4.99, 4.99, 5);
        Assert.Equal(5, outt);
        Assert.Equal(5 - ClipInOut.MinSpanSeconds, inn, 5);
    }

    [Fact]
    public void CutClip_applies_clamp_when_duration_arrives()
    {
        var clip = new CutClip { Scene = 3, Clip = 7 };
        clip.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = "scene_03_clip_07_take_01.mp4",
            RelativePath = "assets/video/scene_03_clip_07_take_01.mp4",
        });
        clip.ActiveTakeNumber = 1;
        clip.SeedSelection();
        clip.ApplyInOut(-1, 40);
        clip.SetDuration(8);
        Assert.Equal(0, clip.MarkIn);
        Assert.Equal(8, clip.MarkOut);
        clip.ApplyInOut(2.5, 6);
        Assert.Equal(2.5, clip.MarkIn);
        Assert.Equal(6, clip.MarkOut);
        Assert.True(clip.NeedsTrim);
    }
}
