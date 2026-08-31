using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The age-variant chips name a life stage. Annette appears as a child, a twenty-year-old and an
/// adult; the chips read "Annette" and "Adult" for all of them, because the label was reading the
/// four-value voice-casting enum instead of the seed's own age_band.
/// </summary>
public class CharactersAgeVariantLabelTests
{
    private static CharacterSummary Seed(string key, string? token, VoiceAgeBand? band = null) =>
        new() { Key = key, DisplayName = "Annette", AgeBandToken = token, AgeBand = band };

    [Fact]
    public void Each_life_stage_names_itself()
    {
        Assert.Equal("Child (8-12)", Characters.CharactersListState.AgeVariantLabel(
            Seed("Character_Girl", "child_8_12")));
        Assert.Equal("Young adult (20s)", Characters.CharactersListState.AgeVariantLabel(
            Seed("Character_Young_Woman", "young_adult_20s")));
        Assert.Equal("Adult", Characters.CharactersListState.AgeVariantLabel(
            Seed("Character_Woman", "adult", VoiceAgeBand.Adult)));
    }

    [Fact]
    public void A_seed_with_no_age_band_falls_back_to_the_voice_band_then_the_name()
    {
        Assert.Equal("Child", Characters.CharactersListState.AgeVariantLabel(
            Seed("Character_Girl", null, VoiceAgeBand.Child)));
        Assert.Equal("Annette", Characters.CharactersListState.AgeVariantLabel(
            Seed("Character_Girl", null)));
    }
}
