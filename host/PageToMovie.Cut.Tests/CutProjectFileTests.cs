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

        var json = CutProjectFile.Serialize([clip], "score.mp3");
        Assert.Contains("score.mp3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"take\"", json, StringComparison.OrdinalIgnoreCase);

        var reload = NewClip(1, 1);
        reload.SetDuration(10);
        Assert.True(CutProjectFile.TryApply([reload], json, out var music));
        Assert.Equal("score.mp3", music);
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
