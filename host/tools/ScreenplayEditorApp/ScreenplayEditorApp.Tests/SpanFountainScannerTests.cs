using System;
using System.IO;
using PageToMovie.Fountain;
using ScreenplayEditorApp.Models;
using Xunit;

namespace ScreenplayEditorApp.Tests;

public class SpanFountainScannerTests
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

        throw new FileNotFoundException($"Fixture file '{relativeOrFileName}' could not be located.");
    }

    [Theory]
    // 1. Spec Fixtures:
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

    // 2. Open-Source Screenplays:
    [InlineData("FountainOpenSource/Big_Fish_nyousefi.fountain")]
    [InlineData("FountainOpenSource/Brick_And_Steel_nyousefi.fountain")]
    [InlineData("FountainOpenSource/DualDialogue_nyousefi.fountain")]
    [InlineData("FountainOpenSource/ForcedElements_nyousefi.fountain")]
    [InlineData("FountainOpenSource/Indenting_nyousefi.fountain")]
    [InlineData("FountainOpenSource/MultilineAction_nyousefi.fountain")]
    [InlineData("FountainOpenSource/Notes_nyousefi.fountain")]
    [InlineData("FountainOpenSource/PageBreaks_nyousefi.fountain")]
    [InlineData("FountainOpenSource/SceneHeaders_nyousefi.fountain")]
    [InlineData("FountainOpenSource/SceneNumbers_nyousefi.fountain")]
    [InlineData("FountainOpenSource/SectionsComplex_nyousefi.fountain")]
    [InlineData("FountainOpenSource/Simple_nyousefi.fountain")]
    [InlineData("FountainOpenSource/Synopses_nyousefi.fountain")]
    [InlineData("FountainOpenSource/TitlePage_screenplaytools.fountain")]
    [InlineData("FountainOpenSource/Transitions_nyousefi.fountain")]
    [InlineData("FountainOpenSource/UTF8_screenplaytools.fountain")]

    // 3. Classic Book Adaptations:
    [InlineData("BookToFountainPackage/fountain_adaptations/01_Alices_Adventures_in_Wonderland.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/02_A_Christmas_Carol.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/03_Dracula.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/04_Frankenstein.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/05_The_Jungle_Book.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/06_The_Gift_of_the_Magi.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/07_The_Tell-Tale_Heart.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/08_The_Yellow_Wallpaper.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/09_The_Raven.fountain")]
    [InlineData("BookToFountainPackage/fountain_adaptations/10_The_Monkeys_Paw.fountain")]
    public void TestSpanFountainScannerAcrossCorpus(string fixtureFileName)
    {
        string fixturePath = GetFixturePath(fixtureFileName);
        string text = File.ReadAllText(fixturePath);

        // 1. Scan with SpanFountainScanner
        int spanElementCount = SpanFountainScanner.ScanElementCount(text.AsSpan());
        Assert.True(spanElementCount > 0, $"SpanFountainScanner should find >0 elements in {fixtureFileName}");

        // 2. Parse with FountainParser
        var parseResult = FountainParser.Parse(text);
        Assert.NotNull(parseResult);
        Assert.True(parseResult.Elements.Count > 0 || parseResult.TitlePage.Count > 0);
    }
}
