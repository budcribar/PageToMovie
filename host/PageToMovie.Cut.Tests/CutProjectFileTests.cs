using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutProjectFileTests
{
    [Fact]
    public void Round_trips_trims_deletes_joins_cards_and_music()
    {
        var clip = NewClip(1, 1);
        clip.ApplyInOut(0.5, 8);
        clip.SetDuration(10);
        Assert.True(CutRangeDelete.TryAdd(clip.RangeDeletes, 2, 3, clip.MarkIn, clip.MarkOut, out _));
        clip.JoinOverride = CutJoinKind.FadeWhite;
        clip.FountainTransition = "DISSOLVE TO:";
        clip.Card.Enabled = true;
        clip.Card.Text = "Chapter 1";
        clip.Card.Seconds = 2;

        var track = new CutMusic { FileName = "score.mp3" };
        track.SetStart(18);
        track.ApplyInOut(1.5, 9);
        var json = CutProjectFile.Serialize([clip], "score.mp3", music: track);
        Assert.Contains("score.mp3", json, StringComparison.Ordinal);
        Assert.Contains("musicStart", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"take\"", json, StringComparison.OrdinalIgnoreCase);

        var reload = NewClip(1, 1);
        reload.SetDuration(10);
        Assert.True(CutProjectFile.TryApply([reload], json, out var music, out _, out _, out var loadedTrack));
        Assert.Equal("score.mp3", music);
        Assert.Equal(18, loadedTrack.StartSec);
        Assert.Equal(1.5, loadedTrack.MarkIn);
        Assert.Equal(9, loadedTrack.MarkOut);
        Assert.Equal(0.5, reload.MarkIn);
        Assert.Equal(8, reload.MarkOut);
        var del = Assert.Single(reload.RangeDeletes);
        Assert.Equal(2, del.Start);
        Assert.Equal(3, del.End);
        Assert.Equal(CutJoinKind.FadeWhite, reload.JoinOverride);
        Assert.Equal("DISSOLVE TO:", reload.FountainTransition);
        Assert.True(reload.Card.Enabled);
        Assert.Equal("Chapter 1", reload.Card.Text);
    }

    [Fact]
    public void Round_trips_fresh_movie_fingerprint()
    {
        var clip = NewClip(1, 1);
        clip.SetDuration(4);
        var fp = CutPlayMerge.Fingerprint([clip], [], "score.mp3");
        var json = CutProjectFile.Serialize([clip], "score.mp3", movieFingerprint: fp);
        Assert.Contains(fp, json, StringComparison.Ordinal);

        var reload = NewClip(1, 1);
        reload.SetDuration(4);
        Assert.True(CutProjectFile.TryApply([reload], json, out _, out _, out var loaded));
        Assert.Equal(fp, loaded);
        Assert.True(CutPlayMerge.IsFreshMerge(loaded, [reload], [], "score.mp3"));
    }

    [Fact]
    public void Round_trips_text_clips_on_the_text_row()
    {
        var clip = NewClip(1, 1);
        clip.SetDuration(10);
        var titles = new List<CutTextClip>
        {
            new() { Id = "title-1", Text = "Opening", StartSec = 1.25, Seconds = 3 },
        };

        var json = CutProjectFile.Serialize([clip], null, titles);
        Assert.Contains("Opening", json, StringComparison.Ordinal);
        Assert.Contains("textClips", json, StringComparison.OrdinalIgnoreCase);

        var reload = NewClip(1, 1);
        reload.SetDuration(10);
        Assert.True(CutProjectFile.TryApply([reload], json, out _, out var loaded));
        var one = Assert.Single(loaded);
        Assert.Equal("title-1", one.Id);
        Assert.Equal("Opening", one.Text);
        Assert.Equal(1.25, one.StartSec);
        Assert.Equal(3, one.HoldSeconds);
        Assert.True(one.Style.IsDefault);
    }

    [Fact]
    public void Round_trips_text_and_card_style_options()
    {
        var clip = NewClip(1, 1);
        clip.SetDuration(10);
        clip.Card.Enabled = true;
        clip.Card.Text = "Chapter 1";
        clip.Card.Style.Position = CutTextPosition.Top;
        clip.Card.Style.Size = CutTextSize.L;
        clip.Card.Style.Color = CutTextColor.Yellow;
        clip.Card.Style.Background = CutTextBackground.DarkBar;
        clip.Card.Style.Fade = CutTextFade.Short;
        var titles = new List<CutTextClip>
        {
            new() { Id = "title-1", Text = "Opening", StartSec = 1, Seconds = 2 },
        };
        titles[0].Style.Position = CutTextPosition.LowerThird;
        titles[0].Style.Size = CutTextSize.S;
        titles[0].Style.Color = CutTextColor.Black;

        var json = CutProjectFile.Serialize([clip], null, titles);
        Assert.Contains("lowerThird", json, StringComparison.Ordinal);
        Assert.Contains("yellow", json, StringComparison.Ordinal);

        var reload = NewClip(1, 1);
        reload.SetDuration(10);
        Assert.True(CutProjectFile.TryApply([reload], json, out _, out var loaded));
        Assert.Equal(CutTextPosition.Top, reload.Card.Style.Position);
        Assert.Equal(CutTextSize.L, reload.Card.Style.Size);
        Assert.Equal(CutTextColor.Yellow, reload.Card.Style.Color);
        Assert.Equal(CutTextBackground.DarkBar, reload.Card.Style.Background);
        Assert.Equal(CutTextFade.Short, reload.Card.Style.Fade);
        var one = Assert.Single(loaded);
        Assert.Equal(CutTextPosition.LowerThird, one.Style.Position);
        Assert.Equal(CutTextSize.S, one.Style.Size);
        Assert.Equal(CutTextColor.Black, one.Style.Color);
        Assert.True(one.Style.Background == CutTextBackground.None);
        Assert.True(one.Style.Fade == CutTextFade.None);
    }

    [Fact]
    public void Saved_marks_override_hop_seed()
    {
        var clip = NewClip(1, 2);
        clip.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        clip.SetDuration(10);
        clip.ApplyInOut(6, 9);
        var json = CutProjectFile.Serialize([clip], null);

        var reload = NewClip(1, 2);
        reload.SelectedTake!.SetHop(new CutHop(5, 5, 10, 5));
        reload.SetDuration(10);
        Assert.Equal(5, reload.MarkIn);
        Assert.True(CutProjectFile.TryApply([reload], json, out _));
        Assert.Equal(6, reload.MarkIn);
        Assert.Equal(9, reload.MarkOut);
    }

    [Fact]
    public void Round_trips_scissors_windows_of_the_same_take()
    {
        var clip = NewClip(1, 1);
        clip.SetDuration(10);
        var clips = new List<CutClip> { clip };
        Assert.True(CutSplit.TryAt(clips, 4, out _));

        var json = CutProjectFile.Serialize(clips, null);
        var reload = new List<CutClip> { NewClip(1, 1) };
        reload[0].SetDuration(10);
        Assert.True(CutProjectFile.TryApply(reload, json, out _));
        Assert.Equal(2, reload.Count);
        Assert.Equal(0, reload[0].MarkIn);
        Assert.Equal(4, reload[0].MarkOut);
        Assert.Equal(4, reload[1].MarkIn);
        Assert.Equal(10, reload[1].MarkOut);
        Assert.Equal(reload[0].FileName, reload[1].FileName);
    }

    [Fact]
    public void FromFiles_reads_sidecar_transition()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_01_clip_01_take_01.mp4", "assets/video/scene_01_clip_01_take_01.mp4", 2000),
            new("scene_01_clip_01.current.json", "assets/video/scene_01_clip_01.current.json", 40,
                """{"take":1}"""),
            new("scene_01_clip_01.clip.json", "assets/video/scene_01_clip_01.clip.json", 80,
                """{"transition":"DISSOLVE TO:"}"""),
        ]);
        var clip = Assert.Single(clips);
        Assert.Equal("DISSOLVE TO:", clip.FountainTransition);
        Assert.Equal(CutJoinKind.Dissolve, clip.JoinToNext(NewClip(2, 1)));
    }

    [Fact]
    public void FromFiles_reads_sidecar_card_note()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_02_clip_01_take_01.mp4", "assets/video/scene_02_clip_01_take_01.mp4", 2000),
            new("scene_02_clip_01.current.json", "assets/video/scene_02_clip_01.current.json", 40,
                """{"take":1}"""),
            new("scene_02_clip_01.clip.json", "assets/video/scene_02_clip_01.clip.json", 80,
                """{"transition":"DISSOLVE TO:","card":"[[CARD: Chapter 1]]"}"""),
        ]);
        var clip = Assert.Single(clips);
        Assert.True(clip.Card.Enabled);
        Assert.Equal("Chapter 1", clip.Card.Text);
        var block = Assert.Single(CutTextTrack.Build(clips, [], pxPerSec: 10));
        Assert.Equal("Chapter 1", block.Text);
        Assert.Equal(0, block.StartSec);
    }

    private static CutClip NewClip(int scene, int clip)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        return c;
    }
}
