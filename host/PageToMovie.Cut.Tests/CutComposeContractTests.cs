using PageToMovie.Cut.Cut;
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
}
