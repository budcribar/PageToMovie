using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class FountainFormatterEdgeCasesTests
{
    [Fact]
    public void TestIntExtAndIEEnvironmentParsing()
    {
        string fountain = @"I/E. BASEMENT - DAY

Action beat.

INT/EXT. PATIO - NIGHT

Patio action.
";
        var model = FountainFormatter.Parse(fountain);
        Assert.Equal(2, model.Scenes.Count);

        Assert.Equal("INT./EXT.", model.Scenes[0].Environment);
        Assert.Equal("BASEMENT", model.Scenes[0].Location);
        Assert.Equal("DAY", model.Scenes[0].TimeOfDay);

        Assert.Equal("INT./EXT.", model.Scenes[1].Environment);
        Assert.Equal("PATIO", model.Scenes[1].Location);
        Assert.Equal("NIGHT", model.Scenes[1].TimeOfDay);
    }

    [Fact]
    public void TestMultilineMetadataToFountain()
    {
        var model = new ScreenplayModel();
        model.Metadata.Title = "Line 1\nLine 2";
        model.Metadata.Author = "Author A\nAuthor B";

        string output = model.ToFountain();
        Assert.Contains("Title:", output);
        Assert.Contains("Line 1", output);
        Assert.Contains("Line 2", output);

        var reParsed = FountainFormatter.Parse(output);
        Assert.Contains("Line 1", reParsed.Metadata.Title);
    }

    [Fact]
    public void TestSceneHeadingWithoutDash()
    {
        string fountain = @"INT. KITCHEN

Action here.
";
        var model = FountainFormatter.Parse(fountain);
        Assert.Single(model.Scenes);
        Assert.Equal("KITCHEN", model.Scenes[0].Location);
        Assert.Equal("DAY", model.Scenes[0].TimeOfDay);
    }

    [Fact]
    public void TestNumberedSceneHeading()
    {
        string fountain = @"INT. ATTIC - NIGHT #42#

Action in attic.
";
        var model = FountainFormatter.Parse(fountain);
        Assert.Single(model.Scenes);
        Assert.Equal(42, model.Scenes[0].SceneNumber);
        Assert.Equal("ATTIC", model.Scenes[0].Location);
    }

    [Fact]
    public void TestStandaloneDialogueWithoutSpeaker()
    {
        string fountain = @"(whispering)
Spoken line without preceding speaker name.
";
        var model = FountainFormatter.Parse(fountain);
        Assert.Single(model.Scenes);
        Assert.NotEmpty(model.Scenes[0].Beats);
    }

    [Fact]
    public void TestToFountainBeatFormattingEdgeCases()
    {
        var model = new ScreenplayModel();
        var scene = new ScreenplayScene
        {
            SceneNumber = 1,
            Environment = "INT.",
            Location = "LAB",
            TimeOfDay = "DAY"
        };

        // Dialogue with extension with parens and parenthetical without parens
        scene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            Speaker = "DOCTOR",
            Extension = "(O.S.)",
            Parenthetical = "nervously",
            SpokenText = "Is anyone there?"
        });

        // Transition with '>'
        scene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Transition,
            TransitionText = "> FADE OUT."
        });

        // Transition without '>'
        scene.Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Transition,
            TransitionText = "CUT TO:"
        });

        model.Scenes.Add(scene);

        string fountain = model.ToFountain();
        Assert.Contains("DOCTOR (O.S.)", fountain);
        Assert.Contains("(nervously)", fountain);
        Assert.Contains("> FADE OUT.", fountain);
        Assert.Contains("> CUT TO:", fountain);
    }
}
