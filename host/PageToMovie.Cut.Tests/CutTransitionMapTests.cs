using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutTransitionMapTests
{
    [Theory]
    [InlineData("CUT TO:", CutJoinKind.Cut)]
    [InlineData("SMASH CUT TO:", CutJoinKind.Cut)]
    [InlineData("MATCH CUT TO:", CutJoinKind.Cut)]
    [InlineData("JUMP CUT TO:", CutJoinKind.Cut)]
    [InlineData("DISSOLVE TO:", CutJoinKind.Dissolve)]
    [InlineData("FADE IN:", CutJoinKind.FadeIn)]
    [InlineData("FADE OUT.", CutJoinKind.Dip)]
    [InlineData("FADE TO BLACK.", CutJoinKind.Dip)]
    [InlineData("BLACKOUT", CutJoinKind.Dip)]
    [InlineData("FADE TO WHITE:", CutJoinKind.FadeWhite)]
    [InlineData("CUT TO BLACK.", CutJoinKind.CutToBlack)]
    [InlineData("WIPE TO:", CutJoinKind.Cut)]
    [InlineData("", CutJoinKind.Unset)]
    public void Maps_fountain_lines(string line, CutJoinKind expected) =>
        Assert.Equal(expected, CutTransitionMap.FromFountain(line));

    [Fact]
    public void Same_scene_default_is_cut() =>
        Assert.Equal(CutJoinKind.Cut, CutTransitionMap.Resolve(null, sceneChanged: false, null));

    [Fact]
    public void Scene_change_default_is_dissolve() =>
        Assert.Equal(CutJoinKind.Dissolve, CutTransitionMap.Resolve(null, sceneChanged: true, null));

    [Fact]
    public void Fountain_wins_over_scene_default() =>
        Assert.Equal(CutJoinKind.Cut, CutTransitionMap.Resolve("CUT TO:", sceneChanged: true, null));

    [Fact]
    public void Ui_override_wins_including_fade_white_and_cut_to_black()
    {
        Assert.Equal(CutJoinKind.Dip, CutTransitionMap.Resolve("DISSOLVE TO:", true, CutJoinKind.Dip));
        Assert.Equal(CutJoinKind.Dissolve, CutTransitionMap.Resolve("CUT TO:", true, CutJoinKind.Dissolve));
        Assert.Equal(CutJoinKind.FadeWhite, CutTransitionMap.Resolve("CUT TO:", true, CutJoinKind.FadeWhite));
        Assert.Equal(CutJoinKind.CutToBlack, CutTransitionMap.Resolve("DISSOLVE TO:", true, CutJoinKind.CutToBlack));
        Assert.Equal(CutJoinKind.FadeIn, CutTransitionMap.Resolve("FADE IN:", false, CutJoinKind.Unset));
    }

    [Fact]
    public void Reads_sidecar_json_keys()
    {
        Assert.Equal("DISSOLVE TO:", CutTransitionMap.ReadSidecarTransition("""{"transition":"DISSOLVE TO:"}"""));
        Assert.Equal("cut", CutTransitionMap.ReadSidecarTransition("""{"transition_type":"cut"}"""));
        Assert.Equal("FADE OUT.", CutTransitionMap.ReadSidecarTransition("""{"fountainTransition":"FADE OUT."}"""));
        Assert.Null(CutTransitionMap.ReadSidecarTransition("{"));
    }

    [Fact]
    public void Reads_sidecar_card_and_fountain_note()
    {
        Assert.Equal("Chapter 1", CutTransitionMap.ReadSidecarCard("""{"card":"Chapter 1"}"""));
        Assert.Equal("Chapter 1", CutTransitionMap.ReadSidecarCard("""{"card":"[[CARD: Chapter 1]]"}"""));
        Assert.Equal("Hi", CutTransitionMap.ReadSidecarCard("""{"cardText":"Hi"}"""));
        Assert.Equal("Chapter 1", CutTransitionMap.ReadSidecarCard("""{"titleCard":"[[CARD: Chapter 1]]"}"""));
        Assert.Equal("Chapter 1", CutTransitionMap.ReadSidecarCard("[[CARD: Chapter 1]]"));
        Assert.True(CutTransitionMap.TryReadCardNote("[[CARD: Chapter 1]]", out var note));
        Assert.Equal("Chapter 1", note);
        Assert.False(CutTransitionMap.TryReadCardNote("[[NOTE: hi]]", out _));
        Assert.Null(CutTransitionMap.ReadSidecarCard("{"));
    }
}
