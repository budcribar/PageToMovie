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

    [Fact]
    public void Menu_delete_clears_the_one_track()
    {
        var music = new CutMusic();
        music.SetFile("ocean-sunrise.mp3");
        music.SetDuration(20);
        music.Move(8);
        music.TrimIn(1);
        music.TrimOut(12);
        music.DisplayName = "Ocean Sunrise";

        music.SetVolumePercent(30);
        music.SetFadeIn(1);
        music.SetFadeOut(2);
        CutMusicEdit.Delete(music);
        Assert.False(music.HasFile);
        Assert.Null(music.FileName);
        Assert.Null(music.DisplayName);
        Assert.Equal(0, music.StartSec);
        Assert.Equal(0, music.MarkIn);
        Assert.Equal(0, music.MarkOut);
        Assert.Equal(100, music.VolumePercent);
        Assert.Equal(0, music.FadeInSec);
        Assert.Equal(0, music.FadeOutSec);
        Assert.Equal(1, music.PlaybackRate);
        Assert.False(music.NoiseSuppression);
    }

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(1.25, 1.25)]
    [InlineData(4, 2)]
    public void Playback_rate_is_clamped_and_drives_timeline_length(double input, double expected)
    {
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetDuration(12);
        music.ApplyInOut(0, 12);
        music.SetPlaybackRate(input);

        Assert.Equal(expected, music.PlaybackRate, 5);
        Assert.Equal(12 / expected, music.OutputDurationSec, 5);
    }

    [Fact]
    public void Menu_rename_keeps_the_real_file_underneath()
    {
        var music = new CutMusic();
        music.SetFile("folder/ocean-sunrise.mp3");
        Assert.Equal("ocean-sunrise.mp3", music.FileName);
        Assert.Equal("ocean-sunrise.mp3", CutMusicEdit.Label(music));
        Assert.Equal("ocean-sunrise.mp3", CutMusicEdit.Label(music, "Compose.AudioFileName"));

        CutMusicEdit.Rename(music, "  Ocean Sunrise  ");
        Assert.Equal("ocean-sunrise.mp3", music.FileName);
        Assert.Equal("Ocean Sunrise", music.DisplayName);
        Assert.Equal("Ocean Sunrise", music.Label);

        CutMusicEdit.Rename(music, "ocean-sunrise.mp3");
        Assert.Null(music.DisplayName);
        Assert.Equal("ocean-sunrise.mp3", music.Label);
    }

    [Fact]
    public void Menu_edit_duration_uses_existing_in_out()
    {
        var music = new CutMusic();
        music.SetFile("score.mp3");
        music.SetDuration(40);
        music.TrimIn(4);
        music.TrimOut(16);
        Assert.Equal(12, music.SlicedDurationSec);

        CutMusicEdit.SetHold(music, 9);
        Assert.Equal(4, music.MarkIn);
        Assert.Equal(13, music.MarkOut);
        Assert.Equal(9, music.SlicedDurationSec);
        Assert.Equal(0, music.StartSec);
    }

    [Fact]
    public void Menu_edit_duration_is_timeline_time_when_speed_changes()
    {
        var music = new CutMusic();
        music.SetFile("score.mp3");
        music.SetDuration(40);
        music.TrimIn(4);
        music.SetPlaybackRate(2);

        CutMusicEdit.SetHold(music, 6);

        Assert.Equal(4, music.MarkIn);
        Assert.Equal(16, music.MarkOut);
        Assert.Equal(6, music.OutputDurationSec, 5);
    }

    [Fact]
    public void Copy_paste_moves_the_same_slice_to_the_playhead()
    {
        var music = new CutMusic();
        music.SetFile("score.mp3");
        music.SetDuration(30);
        music.Move(4);
        music.TrimIn(2);
        music.TrimOut(10);

        var placed = CutMusicEdit.Copy(music);
        CutMusicEdit.Paste(music, placed, playheadSec: 18);
        Assert.Equal(18, music.StartSec);
        Assert.Equal(2, music.MarkIn);
        Assert.Equal(10, music.MarkOut);
        Assert.Equal("score.mp3", music.FileName);
        Assert.False(CutMusicEdit.CanSplit(music, 20));
    }

    [Fact]
    public void Rename_round_trips_through_cut_project_json()
    {
        var clip = new CutClip { Scene = 1, Clip = 1 };
        clip.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = "scene_01_clip_01_take_01.mp4",
            RelativePath = "assets/video/scene_01_clip_01_take_01.mp4",
        });
        clip.ActiveTakeNumber = 1;
        clip.SeedSelection();
        clip.SetDuration(8);

        var music = new CutMusic { FileName = "ocean-sunrise.mp3" };
        music.SetStart(6);
        music.ApplyInOut(1, 9);
        CutMusicEdit.Rename(music, "Ocean Sunrise");

        var json = CutProjectFile.Serialize([clip], "ocean-sunrise.mp3", music: music);
        Assert.Contains("ocean-sunrise.mp3", json, StringComparison.Ordinal);
        Assert.Contains("Ocean Sunrise", json, StringComparison.Ordinal);
        Assert.Contains("musicStart", json, StringComparison.OrdinalIgnoreCase);

        var reload = new CutClip { Scene = 1, Clip = 1 };
        reload.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = "scene_01_clip_01_take_01.mp4",
            RelativePath = "assets/video/scene_01_clip_01_take_01.mp4",
        });
        reload.ActiveTakeNumber = 1;
        reload.SeedSelection();
        reload.SetDuration(8);
        Assert.True(CutProjectFile.TryApply([reload], json, out var file, out _, out _, out var loaded));
        Assert.Equal("ocean-sunrise.mp3", file);
        Assert.Equal("ocean-sunrise.mp3", loaded.FileName);
        Assert.Equal("Ocean Sunrise", loaded.DisplayName);
        Assert.Equal(6, loaded.StartSec);
        Assert.Equal(1, loaded.MarkIn);
        Assert.Equal(9, loaded.MarkOut);
    }
}
