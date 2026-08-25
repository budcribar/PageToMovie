using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The identity reseed drops the video-extend and generates fresh so reference images can be
/// attached — /videos/extensions cannot carry them. That is only worth doing for a face the
/// previous clip did not already contain. Mary19 S02C02 reseeded because the cast shrank from
/// four to two, and the lamb walked in through the door a second time.
/// </summary>
public class CastReseedSubsetTests
{
    private static readonly string[] SceneOneCast =
    {
        "Character_Mary", "Character_Teacher", "Character_The_Children", "Character_The_Lamb",
    };

    [Fact]
    public void Shrinking_to_a_subset_has_no_newcomers()
    {
        var newcomers = FilmJobService.CastNewcomers(
            new[] { "Character_The_Children", "Character_The_Lamb" }, SceneOneCast);
        Assert.Empty(newcomers);
    }

    [Fact]
    public void A_face_not_in_the_previous_clip_is_a_newcomer()
    {
        var newcomers = FilmJobService.CastNewcomers(
            new[] { "Character_The_Lamb", "Character_Constable" }, SceneOneCast);
        Assert.Equal(new[] { "Character_Constable" }, newcomers);
    }

    [Fact]
    public void Newcomers_are_matched_ignoring_case()
    {
        Assert.Empty(FilmJobService.CastNewcomers(
            new[] { "character_the_lamb" }, new[] { "Character_The_Lamb" }));
    }

    [Fact]
    public void A_first_clip_with_no_predecessor_cast_is_all_newcomers()
    {
        var newcomers = FilmJobService.CastNewcomers(
            new[] { "Character_Mary" }, System.Array.Empty<string>());
        Assert.Equal(new[] { "Character_Mary" }, newcomers);
    }

    [Fact]
    public void Empty_current_cast_never_forces_a_reseed()
    {
        Assert.Empty(FilmJobService.CastNewcomers(System.Array.Empty<string>(), SceneOneCast));
    }
}
