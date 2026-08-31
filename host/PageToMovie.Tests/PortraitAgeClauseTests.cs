using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Annette appears as a child, in her twenties, and in middle age. The portrait prompt recognised
/// only child and teen bands, so her early-twenties seed fell through to "HUMAN adult" — the same
/// words her forties-to-sixties seed got, and the two came back as the same person.
/// </summary>
public class PortraitAgeClauseTests
{
    [Fact]
    public void A_young_adult_is_not_told_it_is_an_adult()
    {
        var clause = CharacterDesignService.BuildAgeClause("young_adult_20s", "Character_Young_Woman");

        Assert.Contains("YOUNG ADULT", clause, StringComparison.Ordinal);
        Assert.Contains("(20s)", clause, StringComparison.Ordinal);
        Assert.Contains("younger than the middle-aged version", clause, StringComparison.OrdinalIgnoreCase);
        // The stage the adult seed gets, and the one this seed used to be given instead.
        Assert.NotEqual(
            CharacterDesignService.BuildAgeClause("adult", "Character_Woman"), clause);
        Assert.DoesNotContain("mature adult face", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_child_carries_the_years_its_seed_names()
    {
        var clause = CharacterDesignService.BuildAgeClause("child_8_12", "Character_Girl");

        Assert.Contains("CHILD", clause, StringComparison.Ordinal);
        Assert.Contains("(8-12)", clause, StringComparison.Ordinal);
        Assert.Contains("not adult", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_middle_aged_seed_says_so_without_claiming_the_other_stages()
    {
        var clause = CharacterDesignService.BuildAgeClause("adult", "Character_Woman");

        Assert.Contains("ADULT human", clause, StringComparison.Ordinal);
        Assert.Contains("not a twenty-year-old", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("teen_13_17", "TEEN")]
    [InlineData("elderly_70s", "ELDERLY")]
    [InlineData("senior", "ELDERLY")]
    public void Other_bands_reach_the_model_as_themselves(string band, string expected)
    {
        Assert.Contains(expected, CharacterDesignService.BuildAgeClause(band, "Character_X"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_band_this_code_has_never_seen_says_nothing_rather_than_guessing()
    {
        Assert.Equal("", CharacterDesignService.BuildAgeClause("ageless_spirit", "Character_X"));
    }
}
