using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutMusicTests
{
    [Fact]
    public void Place_and_trim_head_tail_anywhere_on_the_timeline()
    {
        var music = new CutMusic();
        music.SetFile("score.mp3");
        music.SetDuration(40);
        Assert.Equal(0, music.StartSec);
        Assert.Equal(0, music.MarkIn);
        Assert.Equal(40, music.MarkOut);
        Assert.Equal(40, music.SlicedDurationSec);

        music.Move(90);
        music.TrimIn(4);
        music.TrimOut(16);
        Assert.Equal(90, music.StartSec);
        Assert.Equal(4, music.MarkIn);
        Assert.Equal(16, music.MarkOut);
        Assert.Equal(12, music.SlicedDurationSec);
    }

    [Fact]
    public void Saved_in_out_survives_before_duration_is_known()
    {
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetStart(12.5);
        music.ApplyInOut(2, 20);
        Assert.Equal(12.5, music.StartSec);
        Assert.Equal(2, music.MarkIn);
        Assert.Equal(20, music.MarkOut);

        music.SetDuration(30);
        music.ApplyInOut(2, 20);
        Assert.Equal(2, music.MarkIn);
        Assert.Equal(20, music.MarkOut);
    }
}
