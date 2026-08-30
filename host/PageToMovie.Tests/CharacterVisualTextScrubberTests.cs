using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CharacterVisualTextScrubberTests
{
    [Theory]
    [InlineData("matching the dog's CG look")]
    [InlineData("matching the fox's picture-book style")]
    [InlineData("same CG look as the hero animal")]
    [InlineData("matching HeroAnimal's CG look")]
    public void SoftenCrossSpecies_default_is_neutral_medium_not_human_adult(string input)
    {
        var outText = CharacterVisualTextScrubber.SoftenCrossSpeciesStyleLanguage(input);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumPhrase,
            outText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human adult", outText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not an animal", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoftenCrossSpecies_human_disambiguation_opt_in()
    {
        var outText = CharacterVisualTextScrubber.SoftenCrossSpeciesStyleLanguage(
            "matching the dog's CG look",
            disambiguateAsHuman: true);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumHumanDisambiguationPhrase,
            outText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("human", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not an animal", outText, StringComparison.OrdinalIgnoreCase);
        // Prefer "human" over the old "human adult" forced age band
        Assert.DoesNotContain("human adult", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScrubVisualProse_animal_seed_does_not_force_human()
    {
        var scrubbed = CharacterVisualTextScrubber.ScrubVisualProse(
            "A small orange cat matching the dog's CG look; soft fur.",
            disambiguateCrossSpeciesAsHuman: false);
        Assert.DoesNotContain("human adult", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cat", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumPhrase,
            scrubbed,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScrubVisualProse_human_opt_in_keeps_anti_animal_bleed()
    {
        var scrubbed = CharacterVisualTextScrubber.ScrubVisualProse(
            "A middle-aged woman matching the dog's CG look.",
            disambiguateCrossSpeciesAsHuman: true);
        Assert.Contains("not an animal", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human adult", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripGarments_drops_a_pinafore_clause_and_keeps_face()
    {
        var scrubbed = CharacterVisualTextScrubber.StripGarmentsFromIdentityProse(
            "School-age girl with brown braids, a pale pinafore, and a rose ribbon.");
        Assert.Contains("School-age girl", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brown braids", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinafore", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ribbon", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripGarments_drops_wear_frame_welded_into_identity()
    {
        var scrubbed = CharacterVisualTextScrubber.StripGarmentsFromIdentityProse(
            "School-age girl in a pale pinafore with brown braids");
        Assert.Contains("School-age girl", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brown braids", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinafore", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripGarments_drops_nightshirt_clause_from_visual_lock()
    {
        var scrubbed = CharacterVisualTextScrubber.StripGarmentsFromIdentityProse(
            "Always elderly, white-haired, frail; signature constant is the single pale blue eye " +
            "with dull filmy veil that must not drift to clear blue; wears a plain white period nightshirt.");
        Assert.Contains("pale blue eye", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filmy veil", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nightshirt", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wears", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripGarments_keeps_animal_fur_coat()
    {
        var scrubbed = CharacterVisualTextScrubber.StripGarmentsFromIdentityProse(
            "A small terrier with a white coat and brown patches.");
        Assert.Contains("terrier", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("white coat", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsGarmentOnlyClause_pinafore_yes_eye_veil_no()
    {
        Assert.True(CharacterVisualTextScrubber.IsGarmentOnlyClause("a pale pinafore"));
        Assert.True(CharacterVisualTextScrubber.IsGarmentOnlyClause("wears a plain white period nightshirt"));
        Assert.False(CharacterVisualTextScrubber.IsGarmentOnlyClause(
            "one pale blue eye with a dull filmy veil"));
        Assert.False(CharacterVisualTextScrubber.IsGarmentOnlyClause("school-age girl with brown braids"));
    }


    [Fact]
    public void A_species_the_word_list_never_heard_of_keeps_its_clause()
    {
        // Decided by what survives the garments, not by recognising the animal. Under an
        // identity-word list, "goat" and "duckling" were not identity, so the clause went and
        // took the character with it.
        Assert.Equal(
            "A goat, always chewing",
            CharacterVisualTextScrubber.StripGarmentsFromIdentityProse("A goat in a straw hat, always chewing"));

        Assert.Equal(
            "A duckling",
            CharacterVisualTextScrubber.StripGarmentsFromIdentityProse("A duckling in a blue bonnet"));

        Assert.False(CharacterVisualTextScrubber.IsGarmentOnlyClause("A goat in a straw hat"));
    }

    [Fact]
    public void A_second_garment_hung_off_the_first_goes_with_it()
    {
        Assert.Equal(
            "Tall gaunt man, deep-set pale blue eyes",
            CharacterVisualTextScrubber.StripGarmentsFromIdentityProse(
                "Tall gaunt man, deep-set pale blue eyes, wearing a red wool coat over a grey waistcoat"));
    }
}
