using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutComposeContractTests
{
    [Fact]
    public void Preview_cache_reuses_url_until_cleared()
    {
        Assert.False(CutComposeContract.CanReusePreview(null));
        Assert.False(CutComposeContract.CanReusePreview(""));
        Assert.False(CutComposeContract.CanReusePreview("   "));
        Assert.True(CutComposeContract.CanReusePreview("blob:cut-preview"));
    }

    [Fact]
    public void Native_clip_audio_stays_on_hard_cut_and_xfade()
    {
        Assert.True(CutComposeContract.KeepNativeClipAudio);
        Assert.False(CutComposeContract.PadCardSilence);
        Assert.Equal(CutComposeAudioJoin.KeepThroughConcat, CutComposeContract.AudioJoin(CutJoinKind.Cut));
        Assert.Equal(CutComposeAudioJoin.KeepThroughConcat, CutComposeContract.AudioJoin(CutJoinKind.CutToBlack));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.Dissolve));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.Dip));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.FadeWhite));
    }

    [Fact]
    public void Cut_to_black_is_a_black_hold_not_a_scene_card()
    {
        Assert.Equal(0.4, CutComposeContract.CutToBlackHoldSeconds);
        Assert.True(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.CutToBlack));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Dip));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.FadeWhite));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Cut));
        Assert.Equal(0.4, CutComposeContract.HoldSeconds(CutJoinKind.CutToBlack));
        Assert.Equal(0, CutComposeContract.HoldSeconds(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinIsSceneCard(CutJoinKind.CutToBlack));
        Assert.False(CutComposeContract.JoinIsSceneCard(CutJoinKind.Dissolve));
    }

    [Fact]
    public void Scene_joins_wire_fade_white_then_dissolve_into_compose()
    {
        var s01 = NewClip(1, 1, 5.04);
        s01.JoinOverride = CutJoinKind.FadeWhite;
        var s02 = NewClip(2, 1, 20);
        s02.JoinOverride = CutJoinKind.Dissolve;
        var s03 = NewClip(3, 1, 40);
        var payload = CutComposeService.BuildExportPayload([s01, s02, s03]);

        Assert.Equal("fadewhite", payload[0].JoinOut);
        Assert.Equal("dissolve", payload[1].JoinOut);
        Assert.Equal("cut", payload[2].JoinOut);
        Assert.True(CutComposeContract.JoinIsXfade(CutJoinKind.FadeWhite));
        Assert.True(CutComposeContract.JoinIsXfade(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinIsXfade(CutJoinKind.Cut));
        Assert.False(CutComposeContract.JoinIsXfade(CutJoinKind.CutToBlack));
        Assert.Equal(0.5, CutComposeContract.XfadeSecondsFor(5.04), 5);
        Assert.Equal(0.2, CutComposeContract.XfadeSecondsFor(0.4), 5);
        Assert.Equal(CutComposeContract.XfadeSeconds, CutComposeContract.XfadeSecondsFor(8), 5);
    }

    private static CutClip NewClip(int scene, int clip, double duration)
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
        c.SetDuration(duration);
        return c;
    }
}
