using PageToMovie.Fountain;
using Xunit;

namespace PageToMovie.Tests;

public class FountainSceneJoinTests
{
    private const string TwoScenes = """
        Title: Join Test

        FADE IN:

        EXT. YARD - DAY

        A dog runs.

        INT. KITCHEN - NIGHT

        The kettle sings.

        > FADE OUT.

        THE END
        """;

    [Fact]
    public void Dissolve_writes_before_next_heading_and_cut_removes_it()
    {
        var withDissolve = FountainSceneJoin.WriteIncoming(TwoScenes, 2, FountainSceneJoinKind.Dissolve, null);
        var parsed = FountainParser.Parse(withDissolve);
        var beforeKitchen = TransitionImmediatelyBefore(parsed, "KITCHEN");
        Assert.Equal("DISSOLVE TO:", beforeKitchen, ignoreCase: true);

        var incoming = FountainSceneJoin.ReadIncoming(withDissolve);
        Assert.Contains(incoming, j => j.IncomingHeadingIndex == 2 && j.Kind == FountainSceneJoinKind.Dissolve);

        var cut = FountainSceneJoin.WriteIncoming(withDissolve, 2, FountainSceneJoinKind.Cut, null);
        var afterCut = FountainParser.Parse(cut);
        Assert.Null(TransitionImmediatelyBefore(afterCut, "KITCHEN"));
        Assert.DoesNotContain("DISSOLVE TO:", cut, StringComparison.OrdinalIgnoreCase);

        var cutIncoming = FountainSceneJoin.ReadIncoming(cut);
        Assert.Contains(cutIncoming, j => j.IncomingHeadingIndex == 2 && j.Kind == FountainSceneJoinKind.Cut);
        Assert.Contains(afterCut.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                                e.Text.Contains("FADE OUT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dip_writes_forced_fade_out_and_card_note()
    {
        var text = FountainSceneJoin.WriteIncoming(TwoScenes, 2, FountainSceneJoinKind.Dip, "Chapter 1");
        Assert.Contains("> FADE OUT.", text, StringComparison.Ordinal);
        Assert.Contains("[[CARD: Chapter 1]]", text, StringComparison.Ordinal);

        var parsed = FountainParser.Parse(text);
        Assert.Equal("FADE OUT.", TransitionImmediatelyBefore(parsed, "KITCHEN"), ignoreCase: true);
        var incoming = FountainSceneJoin.ReadIncoming(text).Single(j => j.IncomingHeadingIndex == 2);
        Assert.Equal(FountainSceneJoinKind.Dip, incoming.Kind);
        Assert.Equal("Chapter 1", incoming.CardText);
    }

    [Theory]
    [InlineData("CUT TO:", FountainSceneJoinKind.Cut)]
    [InlineData("SMASH CUT TO:", FountainSceneJoinKind.Cut)]
    [InlineData("MATCH CUT TO:", FountainSceneJoinKind.Cut)]
    [InlineData("JUMP CUT TO:", FountainSceneJoinKind.Cut)]
    [InlineData("WIPE TO:", FountainSceneJoinKind.Cut)]
    [InlineData("DISSOLVE TO:", FountainSceneJoinKind.Dissolve)]
    [InlineData("FADE OUT.", FountainSceneJoinKind.Dip)]
    [InlineData("FADE TO BLACK.", FountainSceneJoinKind.Dip)]
    [InlineData("BLACKOUT", FountainSceneJoinKind.Dip)]
    [InlineData("FADE TO WHITE:", FountainSceneJoinKind.FadeWhite)]
    [InlineData("CUT TO BLACK.", FountainSceneJoinKind.CutToBlack)]
    [InlineData("", FountainSceneJoinKind.Cut)]
    public void FromFountain_matches_cut_playable_joins(string line, FountainSceneJoinKind expected) =>
        Assert.Equal(expected, FountainSceneJoin.FromFountain(line));

    [Fact]
    public void ForceTransitionMarkers_forces_bare_fade_out()
    {
        var src = """
            EXT. YARD - DAY

            Dog runs.

            FADE OUT.

            INT. KITCHEN - NIGHT

            Kettle.
            """;
        var forced = FountainSceneJoin.ForceTransitionMarkers(src);
        Assert.Contains("> FADE OUT.", forced, StringComparison.Ordinal);
        var parsed = FountainParser.Parse(forced);
        Assert.Contains(parsed.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                             e.Text.Contains("FADE OUT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Write_does_not_touch_opening_fade_in_or_closing_fade_out()
    {
        var text = FountainSceneJoin.WriteIncoming(TwoScenes, 2, FountainSceneJoinKind.Dissolve, null);
        Assert.Contains("FADE IN:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THE END", text, StringComparison.Ordinal);
        var parsed = FountainParser.Parse(text);
        Assert.Contains(parsed.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                             e.Text.Contains("FADE IN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                             e.Text.Contains("FADE OUT", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TransitionImmediatelyBefore(FountainParser.ParseResult parsed, string headingNeedle)
    {
        string? lastTransition = null;
        foreach (var el in parsed.Elements)
        {
            if (el.Type == FountainParser.ElementType.Transition)
                lastTransition = el.Text;
            else if (el.Type == FountainParser.ElementType.SceneHeading)
            {
                if (el.Text.Contains(headingNeedle, StringComparison.OrdinalIgnoreCase))
                    return lastTransition;
                lastTransition = null;
            }
            else
                lastTransition = null;
        }

        return null;
    }
}
