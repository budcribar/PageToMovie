using System;
using System.IO;
using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class FountainFormatterTests
{
    private static string GetFixturePath(string relativeOrFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "PageToMovie.Tests", "Fixtures", relativeOrFileName);
            if (File.Exists(candidate))
                return candidate;

            var hostCandidate = Path.Combine(dir.FullName, "host", "PageToMovie.Tests", "Fixtures", relativeOrFileName);
            if (File.Exists(hostCandidate))
                return hostCandidate;

            var fountainCandidate = Path.Combine(dir.FullName, "PageToMovie.Tests", "Fixtures", "Fountain", relativeOrFileName);
            if (File.Exists(fountainCandidate))
                return fountainCandidate;

            var hostFountainCandidate = Path.Combine(dir.FullName, "host", "PageToMovie.Tests", "Fixtures", "Fountain", relativeOrFileName);
            if (File.Exists(hostFountainCandidate))
                return hostFountainCandidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Fixture file '{relativeOrFileName}' could not be located from '{AppContext.BaseDirectory}'.");
    }

    [Fact]
    public void TestTitlePageMetadataParsing()
    {
        string fountain = @"Title: The Great Adventure
Credit: Written by
Author: Jane Doe
Source: Based on original story
Draft date: 2026-08-09
Contact: jane@example.com
Notes: First Revision

INT. KITCHEN - DAY
";

        ScreenplayModel model = FountainFormatter.Parse(fountain);

        Assert.NotNull(model.Metadata);
        Assert.Equal("The Great Adventure", model.Metadata.Title);
        Assert.Equal("Written by", model.Metadata.Credit);
        Assert.Equal("Jane Doe", model.Metadata.Author);
        Assert.Equal("Based on original story", model.Metadata.Source);
        Assert.Equal("2026-08-09", model.Metadata.DraftDate);
        Assert.Equal("jane@example.com", model.Metadata.Contact);
        Assert.Equal("First Revision", model.Metadata.Notes);
    }

    [Fact]
    public void TestSceneHeadingParsing()
    {
        string fountain = @"INT. KITCHEN - DAY

Action in kitchen.

EXT. PARK - NIGHT

Action in park.
";

        ScreenplayModel model = FountainFormatter.Parse(fountain);

        Assert.Equal(2, model.Scenes.Count);

        var scene1 = model.Scenes[0];
        Assert.Equal("INT.", scene1.Environment);
        Assert.Equal("KITCHEN", scene1.Location);
        Assert.Equal("DAY", scene1.TimeOfDay);
        Assert.Equal("INT. KITCHEN - DAY", scene1.HeaderText);

        var scene2 = model.Scenes[1];
        Assert.Equal("EXT.", scene2.Environment);
        Assert.Equal("PARK", scene2.Location);
        Assert.Equal("NIGHT", scene2.TimeOfDay);
        Assert.Equal("EXT. PARK - NIGHT", scene2.HeaderText);
    }

    [Fact]
    public void TestActionBlockAndCharacterDialogueBeatParsing()
    {
        string fountain = @"INT. LIVING ROOM - DAY

John enters the room quietly and looks around.

JOHN
(whispering)
Did you hear that sound?

MARY (O.S.)
(calmly)
It's just the wind outside.
";

        ScreenplayModel model = FountainFormatter.Parse(fountain);

        Assert.Single(model.Scenes);
        var scene = model.Scenes[0];

        Assert.Equal(3, scene.Beats.Count);

        // Beat 1: Action
        var beat1 = scene.Beats[0];
        Assert.Equal(BeatType.Action, beat1.BeatType);
        Assert.Equal("John enters the room quietly and looks around.", beat1.ActionText);

        // Beat 2: Dialogue (John)
        var beat2 = scene.Beats[1];
        Assert.Equal(BeatType.Dialogue, beat2.BeatType);
        Assert.Equal("JOHN", beat2.Speaker);
        Assert.Equal("whispering", beat2.Parenthetical);
        Assert.Equal("Did you hear that sound?", beat2.SpokenText);

        // Beat 3: Dialogue (Mary with Extension)
        var beat3 = scene.Beats[2];
        Assert.Equal(BeatType.Dialogue, beat3.BeatType);
        Assert.Equal("MARY", beat3.Speaker);
        Assert.Contains("O.S.", beat3.Extension);
        Assert.Equal("calmly", beat3.Parenthetical);
        Assert.Equal("It's just the wind outside.", beat3.SpokenText);
    }

    [Theory]
    [InlineData("01_basic_scene_elements.fountain")]
    [InlineData("02_title_page.fountain")]
    [InlineData("03_parentheticals_and_beats.fountain")]
    [InlineData("04_dual_dialogue.fountain")]
    [InlineData("05_transitions.fountain")]
    [InlineData("06_centered_text.fountain")]
    [InlineData("07_emphasis_bold_italic_underline.fountain")]
    [InlineData("08_lyrics.fountain")]
    [InlineData("09_sections_and_synopses.fountain")]
    [InlineData("10_notes_and_boneyard.fountain")]
    [InlineData("11_page_breaks.fountain")]
    [InlineData("12_character_extensions.fountain")]
    [InlineData("14_numbered_scene_headings.fountain")]
    [InlineData("15_line_breaks_and_whitespace.fountain")]
    [InlineData("16_unicode_and_special_chars.fountain")]
    [InlineData("17_combined_feature_sample.fountain")]
    [InlineData("18_montage_sequence.fountain")]
    [InlineData("19_minimal_dialogue_only.fountain")]
    [InlineData("20_edge_cases.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/01_Alices_Adventures_in_Wonderland.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/02_A_Christmas_Carol.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/07_The_Tell-Tale_Heart.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/10_The_Monkeys_Paw.fountain")]
    public void TestRoundTripFidelity(string fixtureFileName)
    {
        string fixturePath = GetFixturePath(fixtureFileName);
        string originalFountain = File.ReadAllText(fixturePath);

        // Parse into ScreenplayModel
        ScreenplayModel model1 = FountainFormatter.Parse(originalFountain);

        // Convert back to Fountain via ToFountain()
        string exportedFountain = model1.ToFountain();
        Assert.NotNull(exportedFountain);
        Assert.NotEmpty(exportedFountain);

        // Parse back into second ScreenplayModel
        ScreenplayModel model2 = FountainFormatter.Parse(exportedFountain);

        // Verify content preservation
        Assert.Equal(model1.Metadata.Title, model2.Metadata.Title);
        Assert.Equal(model1.Scenes.Count, model2.Scenes.Count);
    }
}
