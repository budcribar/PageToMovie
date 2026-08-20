using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectAssetNamingTests
{
    [Fact]
    public void LocationRefFileNameCandidates_PrefixKey_CanonicalThenBare()
    {
        Assert.Equal(
            new[] { "loc_country_lane_ref.png", "country_lane_ref.png" },
            ProjectAssetNaming.LocationRefFileNameCandidates("Loc_Country_Lane"));
    }

    [Fact]
    public void LocationRefFileNameCandidates_BareKey_CanonicalThenPrefixed()
    {
        Assert.Equal(
            new[] { "kitchen_ref.png", "loc_kitchen_ref.png" },
            ProjectAssetNaming.LocationRefFileNameCandidates("kitchen"));
    }

    [Fact]
    public void CharacterRefFileCandidates_PrefixKey_CanonicalThenBare()
    {
        Assert.Equal(
            new[] { "character_mary_ref.png", "mary_ref.png" },
            ProjectAssetNaming.CharacterRefFileCandidates("Character_Mary"));
    }

    [Fact]
    public void CharacterRefFileCandidates_BareKey_CanonicalThenPrefixed()
    {
        Assert.Equal(
            new[] { "hero_ref.png", "character_hero_ref.png" },
            ProjectAssetNaming.CharacterRefFileCandidates("Hero"));
    }

    [Fact]
    public void RefFileName_Blank_UsesUnknownFallback()
    {
        Assert.Equal("unknown_location_ref.png", ProjectAssetNaming.LocationRefFileName(""));
        Assert.Equal("unknown_character_ref.png", ProjectAssetNaming.CharacterRefFileName(".."));
    }

    [Fact]
    public void RefFileName_AlreadySuffixed_Unchanged()
    {
        Assert.Equal("kitchen_ref.png", ProjectAssetNaming.LocationRefFileName("kitchen_ref.png"));
        Assert.Equal("mary_ref.png", ProjectAssetNaming.CharacterRefFileName("Mary_ref.png"));
    }
}
