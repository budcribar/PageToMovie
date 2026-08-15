using PageToMovie.Web.Components;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The Film Length control seeds a fresh project's target from the user's last choice on any
/// project. That must only shorten: a 180-minute target remembered from a novel silently turned
/// a 2-minute nursery rhyme into a 180-minute, $700+ estimate.
/// </summary>
public class FilmLengthCardPrefSeedTests
{
    [Fact]
    public void Pref_longer_than_natural_is_ignored()
    {
        Assert.False(FilmLengthCard.ShouldSeedTargetFromPref(180, naturalMinutes: 2, out _));
        Assert.False(FilmLengthCard.ShouldSeedTargetFromPref(3, naturalMinutes: 2, out _));
    }

    [Fact]
    public void Pref_at_or_below_natural_is_applied()
    {
        Assert.True(FilmLengthCard.ShouldSeedTargetFromPref(90, naturalMinutes: 140, out var m));
        Assert.Equal(90, m);
        Assert.True(FilmLengthCard.ShouldSeedTargetFromPref(2, naturalMinutes: 2, out m));
        Assert.Equal(2, m);
    }

    [Fact]
    public void Missing_or_out_of_range_pref_is_ignored()
    {
        Assert.False(FilmLengthCard.ShouldSeedTargetFromPref(null, 60, out _));
        Assert.False(FilmLengthCard.ShouldSeedTargetFromPref(0, 60, out _));
        Assert.False(FilmLengthCard.ShouldSeedTargetFromPref(181, 60, out _));
    }
}
