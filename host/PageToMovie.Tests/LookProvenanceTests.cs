using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A book that describes nobody still has to yield portraits, so the pipeline invents a look —
/// that part is unavoidable. What it must not do is file the invention where facts go. Annette's
/// source is twelve one-line life vignettes with no appearance detail at all; extraction gave her
/// "dark brown-black hair, dark brown eyes, sun-warmed tan skin" and put it in visual_lock, the
/// must-never-drift contract carried into every shot of the film. Her actual photograph shows
/// auburn hair and blue eyes. Nothing in the pipeline could tell the difference between the two
/// kinds of claim, because both were written into the same field with the same authority.
/// </summary>
public class LookProvenanceTests
{
    private static Dictionary<string, object?> Cast(params (string Key, object? Seed)[] seeds)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, seed) in seeds)
            dict[key] = seed;
        return new Dictionary<string, object?>
        {
            ["movie_title"] = "Annette",
            ["character_seed_tokens"] = dict,
        };
    }

    private static Dictionary<string, object?> Normalized(Dictionary<string, object?> parsed) =>
        (Dictionary<string, object?>)CastFromScreenplayService
            .NormalizeCastDoc(parsed, "budcribar/Annette")["character_seed_tokens"]!;

    [Fact]
    public void An_invented_look_never_reaches_visual_lock()
    {
        var seeds = Normalized(Cast(("Character_Woman", new Dictionary<string, object?>
        {
            ["canonical_given_name"] = "Annette",
            ["description"] = "Woman in her fifties, dark brown-black hair, dark brown eyes, sun-warmed tan skin.",
            ["visual_lock"] = "Dark brown-black hair, dark brown eyes, sun-warmed tan skin every scene.",
            ["look_provenance"] = "invented",
            ["display_name_policy"] = "ok_anytime",
        })));

        var seed = (Dictionary<string, object?>)seeds["Character_Woman"]!;
        Assert.Equal("", seed["visual_lock"]);
        Assert.Equal("invented", seed[LookProvenanceTokens.SeedKey]);
        // The words are not thrown away — they are what the first portrait draws from.
        Assert.Contains("fifties", seed["description"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sourced")]
    [InlineData("inferred")]
    public void A_look_the_source_backs_keeps_its_visual_lock(string provenance)
    {
        var seeds = Normalized(Cast(("Character_Old_Man", new Dictionary<string, object?>
        {
            ["canonical_given_name"] = "Old Man",
            ["description"] = "Elderly man, pale filmy left eye.",
            ["visual_lock"] = "The pale filmy left eye must not drift to a clear one.",
            ["look_provenance"] = provenance,
            ["display_name_policy"] = "ok_anytime",
        })));

        var seed = (Dictionary<string, object?>)seeds["Character_Old_Man"]!;
        Assert.Contains("filmy", seed["visual_lock"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(provenance, seed[LookProvenanceTokens.SeedKey]);
    }

    [Fact]
    public void A_seed_extracted_before_provenance_existed_is_left_alone()
    {
        var seeds = Normalized(Cast(("Character_Buster", new Dictionary<string, object?>
        {
            ["canonical_given_name"] = "Buster",
            ["description"] = "Small black-and-white dog, floppy ears.",
            ["visual_lock"] = "Black-and-white coat, floppy ears every scene.",
            ["display_name_policy"] = "ok_anytime",
        })));

        // No marker is not evidence of invention. Voiding an old project's locks on a guess
        // would be a worse failure than the one this field exists to fix.
        var seed = (Dictionary<string, object?>)seeds["Character_Buster"]!;
        Assert.Contains("floppy ears", seed["visual_lock"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.False(seed.ContainsKey(LookProvenanceTokens.SeedKey));
    }

    [Fact]
    public void Book_text_harvested_for_a_blank_look_does_not_refill_an_invented_lock()
    {
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Annette"] = new Dictionary<string, object?>
            {
                ["canonical_given_name"] = "Annette",
                ["description"] = "Woman in her fifties, auburn hair.",
                ["visual_lock"] = "",
                [LookProvenanceTokens.SeedKey] = "invented",
            },
        };

        CastFromScreenplayService.EnrichStubLooksFromSources(
            seeds,
            bookText: "Annette rode every morning. Annette kept her boots by the door.",
            fountainText: null);

        // Paragraphs that merely mention the name are thin evidence — fine for filling a blank
        // description, not enough to promise a look will never drift for a character the source
        // never actually described.
        var seed = (Dictionary<string, object?>)seeds["Character_Annette"]!;
        Assert.Equal("", seed["visual_lock"]!.ToString());
    }
}
