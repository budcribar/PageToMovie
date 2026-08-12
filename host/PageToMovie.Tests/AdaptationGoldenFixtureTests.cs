using System.Text.RegularExpressions;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Validation;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

/// <summary>
/// Golden / structural fixtures for Stage‑1 via <see cref="AdaptationService"/> only.
/// No live API — uses offline heuristic convert + pure analysis + cast package checks.
/// </summary>
public sealed class AdaptationGoldenFixtureTests
{
    private static string FixtureDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AdaptationGolden");
            if (Directory.Exists(dir)) return dir;
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "AdaptationGolden"));
        }
    }

    private static string Read(string fileName)
    {
        var path = Path.Combine(FixtureDir, fileName);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Mary_analyze_and_natural_runtime_are_short_book()
    {
        var book = Read("mary_had_a_little_lamb.txt");
        var svc = new AdaptationService();
        var analysis = svc.AnalyzeBook(book);
        var runtime = svc.EstimateNaturalRuntime(book);

        Assert.True(analysis.TextWords > 20, $"words={analysis.TextWords}");
        Assert.True(analysis.ReadyForStage1, string.Join("; ", analysis.Notes));
        // Short nursery rhyme — natural film length stays small (not 10+ min pad).
        Assert.InRange(runtime.NaturalMinutes, 1, 8);
        Assert.Equal(runtime.NaturalMinutes, runtime.TargetMinutes);
        Assert.Equal("natural", runtime.Mode);
    }

    [Fact]
    public void Buster_analyze_ready_for_stage1()
    {
        var book = Read("buster_the_noodlehead_dog.txt");
        var svc = new AdaptationService();
        var analysis = svc.AnalyzeBook(book);
        var runtime = svc.EstimateNaturalRuntime(book);

        Assert.True(analysis.TextWords > 50, $"words={analysis.TextWords}");
        Assert.True(analysis.ReadyForStage1, string.Join("; ", analysis.Notes));
        Assert.InRange(runtime.NaturalMinutes, 2, 30);
        Assert.Contains("PAGE", book, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mary_had_a_little_lamb.txt", "Mary Had a Little Lamb", "Traditional")]
    [InlineData("buster_the_noodlehead_dog.txt", "Buster The Noodlehead Dog", "Debra McGuinty")]
    public void Heuristic_convert_via_AdaptationService_is_structurally_good(
        string bookFile, string title, string author)
    {
        var book = Read(bookFile);
        var svc = new AdaptationService();
        var fountain = svc.FixDraftDate(svc.ConvertHeuristic(title, book, author));

        Assert.False(string.IsNullOrWhiteSpace(fountain));
        Assert.True(svc.LooksLikeGoodFountain(fountain), "LooksLikeGoodFountain failed");
        Assert.Matches(new Regex(@"(?im)^(INT|EXT|EST)", RegexOptions.Multiline, CommonRegex.Timeout), fountain);
        Assert.Contains("Title:", fountain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NARRATOR", fountain, StringComparison.OrdinalIgnoreCase);
        // No book page dump markers in operator-facing draft
        Assert.DoesNotContain("[[pages", fountain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mary_reference_fountain_passes_structural_and_cast_gates()
    {
        var fountain = Read("mary_reference.fountain");
        var book = Read("mary_had_a_little_lamb.txt");
        var svc = new AdaptationService();

        Assert.True(svc.LooksLikeGoodFountain(fountain));

        // Scene headings + dialogue shape (line scan — no Engine FountainParser)
        var headings = CommonRegex.Matches(fountain, @"(?im)^(INT|EXT)\.\s+\S+").Count;
        Assert.True(headings >= 2, $"headings={headings}");
        Assert.Contains("NARRATOR", fountain, StringComparison.Ordinal);
        Assert.Contains("CHILDREN", fountain, StringComparison.Ordinal);
        Assert.Contains("TEACHER", fountain, StringComparison.Ordinal);

        // No invented named children in the reference package
        Assert.DoesNotContain("ELI", fountain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLARA", fountain, StringComparison.OrdinalIgnoreCase);

        var cast = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Mary": {
                  "canonical_given_name": "Mary",
                  "description": "Eight-year-old girl in blue dress and white pinafore with hair ribbons.",
                  "visual_lock": "brown hair ribbons, blue dress, white pinafore",
                  "species_kind": "human"
                },
                "Character_Lamb": {
                  "canonical_given_name": "Lamb",
                  "description": "Small pure-white lamb with soft fleece.",
                  "visual_lock": "tiny snow-white fleece, dark eyes",
                  "species_kind": "animal"
                },
                "Character_Teacher": {
                  "canonical_given_name": "Teacher",
                  "description": "Adult woman in plain gray dress with hair in a bun.",
                  "visual_lock": "gray dress, hair in a bun, kind adult face",
                  "species_kind": "human"
                },
                "Character_Children": {
                  "canonical_given_name": "Children",
                  "description": "Group of school-age children in simple period play clothes.",
                  "visual_lock": "several young classmates, eager faces",
                  "species_kind": "human"
                },
                "Character_Narrator": {
                  "canonical_given_name": "Narrator",
                  "description": "Off-screen verse narrator only.",
                  "display_name_policy": "never_on_screen",
                  "species_kind": "human"
                }
              }
            }
            """;

        var report = svc.CrossCheckCast(fountain, cast, book);
        Assert.True(report.Ok, string.Join("; ", report.Failures));
        Assert.Empty(report.SpeakersMissingFromBook);
        Assert.Contains("Character_Children", report.MatchedKeys);
        Assert.DoesNotContain(report.MatchedKeys, k =>
            k.Contains("Eli", StringComparison.OrdinalIgnoreCase) ||
            k.Contains("Clara", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveTargetMinutes_override_clamps_and_sets_mode()
    {
        var book = Read("mary_had_a_little_lamb.txt");
        var svc = new AdaptationService();
        var natural = svc.EstimateNaturalRuntime(book);
        var reduced = svc.ResolveTargetMinutes(book, overrideMinutes: 2);

        Assert.Equal(2, reduced.TargetMinutes);
        Assert.Equal(natural.NaturalMinutes, reduced.NaturalMinutes);
        // 2 may equal natural for Mary — mode is natural or reduced/custom
        Assert.Contains(reduced.Mode, new[] { "natural", "reduced", "custom" });
    }

    [Fact]
    public void NormalizeBookText_is_stable_for_cache_keys()
    {
        var book = Read("buster_the_noodlehead_dog.txt");
        var svc = new AdaptationService();
        var a = svc.NormalizeBookText(book);
        var b = svc.NormalizeBookText(book);
        Assert.Equal(a, b);
        Assert.False(string.IsNullOrWhiteSpace(a));
    }
}
