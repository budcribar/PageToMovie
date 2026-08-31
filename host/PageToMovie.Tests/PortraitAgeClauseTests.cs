using System.Text.Json;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Annette is written at three ages, and the seed says so in prose the extraction wrote for her:
/// "Human woman in her early twenties … fuller youthful cheeks than the later adult, no silver in
/// the hair". With no reference image that sentence is the character, so it leads the prompt.
/// It used to sit last, behind a paragraph of IGNORE rules, while a category synthesised from
/// age_band led instead — and that category knew only "child" and "teen", so her twenties seed
/// was handed the same word as her forties-to-sixties seed.
/// </summary>
public class PortraitAgeClauseTests
{
    private const string YoungWomanDesc =
        "Human woman in her early twenties, lean student build, sun-warmed tan skin, smoother unlined face.";
    private const string YoungWomanLock =
        "Early-twenties face: fuller youthful cheeks than the later adult, no silver in the hair.";

    private static JsonElement Seed(string ageBand, string description, string visualLock) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            canonical_given_name = "Annette",
            age_band = ageBand,
            description,
            visual_lock = visualLock,
            variant_of = "Character_Woman",
        })).RootElement.Clone();

    [Fact]
    public void Without_a_reference_image_the_seeds_own_words_lead()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Young_Woman",
            Seed("young_adult_20s", YoungWomanDesc, YoungWomanLock),
            hasImageHints: false);

        var look = prompt.IndexOf("early twenties", StringComparison.OrdinalIgnoreCase);
        var ignore = prompt.IndexOf("IGNORE in the text notes", StringComparison.Ordinal);
        Assert.True(look >= 0, prompt);
        Assert.True(ignore >= 0, prompt);
        Assert.True(look < ignore, $"the description must lead, not trail the IGNORE block:\n{prompt}");
    }

    [Fact]
    public void No_age_category_is_synthesised_over_a_description_that_states_the_age()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Young_Woman",
            Seed("young_adult_20s", YoungWomanDesc, YoungWomanLock),
            hasImageHints: false);

        // The old clause asserted a stage of its own here; the sentence says it better.
        Assert.DoesNotContain("SPECIES/AGE:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("AGE: young adult", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_seed_with_no_description_still_gets_its_age_band_verbatim()
    {
        Assert.Equal("AGE: child 8 12. ", CharacterDesignService.BuildAgeFallbackClause("child_8_12", ""));
        Assert.Equal("AGE: ageless spirit. ", CharacterDesignService.BuildAgeFallbackClause("ageless_spirit", null));
    }

    [Fact]
    public void A_description_that_states_the_age_needs_no_fallback()
    {
        Assert.Equal("", CharacterDesignService.BuildAgeFallbackClause("young_adult_20s", YoungWomanDesc));
    }
}
