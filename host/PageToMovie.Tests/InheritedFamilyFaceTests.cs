using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Annette is one person at three ages, and only her fifties seed has a photograph. Left to invent
/// a face each, her child, young-adult and adult seeds agree on nothing except a name — three
/// strangers in one cast. So the family face is decided once, at the base seed, and the variants
/// borrow it: identity from the picture, age from their own words. Which makes the age the one
/// thing that must beat the photo, and therefore the one thing that cannot be left in the text
/// notes at the bottom of the prompt behind a paragraph of IGNORE rules.
/// </summary>
public class InheritedFamilyFaceTests
{
    private static JsonElement Seed(
        string ageBand,
        string description,
        string? provenance = null,
        string? variantOf = "Character_Woman")
    {
        var seed = new Dictionary<string, object?>
        {
            ["canonical_given_name"] = "Annette",
            ["age_band"] = ageBand,
            ["description"] = description,
            ["visual_lock"] = "",
        };
        if (variantOf is not null) seed["variant_of"] = variantOf;
        if (provenance is not null) seed[LookProvenanceTokens.SeedKey] = provenance;
        return JsonDocument.Parse(JsonSerializer.Serialize(seed)).RootElement.Clone();
    }

    [Fact]
    public void A_borrowed_face_is_named_as_the_same_person_at_another_age()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Girl",
            Seed("child_8_12", "Girl of about ten, on a pony in a summer field."),
            hasImageHints: true,
            identityRefIsInherited: true);

        Assert.Contains("SAME PERSON at a DIFFERENT AGE", prompt, StringComparison.Ordinal);
        // The usual instruction would hand a ten-year-old the wardrobe and years of a woman in
        // her fifties, because that is what the attached photograph shows.
        Assert.DoesNotContain("Match face, hair, and default clothing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stated_age_outranks_the_photo_and_is_stated_before_the_ignore_rules()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Girl",
            Seed("child_8_12", "Girl of about ten, on a pony in a summer field."),
            hasImageHints: true,
            identityRefIsInherited: true);

        var age = prompt.IndexOf("AGE (hard", StringComparison.Ordinal);
        var ignore = prompt.IndexOf("IGNORE in the text notes", StringComparison.Ordinal);
        Assert.True(age >= 0, prompt);
        Assert.True(ignore >= 0, prompt);
        Assert.True(age < ignore, $"a constraint that has to beat the attached photo cannot trail it:\n{prompt}");
        Assert.Contains("child 8 12", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_own_photo_retires_the_invented_words_that_were_standing_in_for_it()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Woman",
            Seed("adult", "Woman in her fifties, dark brown-black hair, dark brown eyes.",
                 provenance: LookProvenanceTokens.Invented, variantOf: null),
            hasImageHints: true,
            identityRefIsInherited: false);

        Assert.DoesNotContain("dark brown-black", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIORITY 3", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_borrowed_face_keeps_the_words_that_carry_the_age()
    {
        // The suppression above must not reach here: with a photo of the wrong life stage, the
        // prose is the only thing saying which age to draw.
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Girl",
            Seed("child_8_12", "Girl of about ten, gap-toothed grin.",
                 provenance: LookProvenanceTokens.Invented),
            hasImageHints: true,
            identityRefIsInherited: true);

        Assert.Contains("gap-toothed", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_look_the_source_backs_still_rides_alongside_the_characters_own_photo()
    {
        var (prompt, _) = CharacterDesignService.BuildDesignPrompt(
            "Character_Old_Man",
            Seed("adult", "Elderly man with a pale filmy left eye.",
                 provenance: LookProvenanceTokens.Sourced, variantOf: null),
            hasImageHints: true,
            identityRefIsInherited: false);

        Assert.Contains("filmy", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRIORITY 3", prompt, StringComparison.Ordinal);
    }
}
