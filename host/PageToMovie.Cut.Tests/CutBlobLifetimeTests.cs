using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutBlobLifetimeTests
{
    private const string Source = "blob:http://127.0.0.1:5299/take-source";
    private const string HopSlice = "blob:http://127.0.0.1:5299/hop-slice";
    private const string Prefix = "blob:http://127.0.0.1:5299/jit-prefix";
    private const string StaleMovie = "blob:http://127.0.0.1:5299/old-movie";

    [Fact]
    public void Concat_is_not_given_a_revoked_url()
    {
        var ownedTemps = new[] { HopSlice, StaleMovie };
        var inUse = new[] { Source, HopSlice };
        var pinned = new[] { Prefix };

        var concatInputs = new[] { Source, HopSlice };
        Assert.DoesNotContain(
            concatInputs,
            url => CutBlobLifetime.CanRevoke(url, ownedTemps, inUse, pinned));

        var revocable = CutBlobLifetime.Revocable(ownedTemps, inUse, pinned);
        Assert.Equal(new[] { StaleMovie }, revocable);
        Assert.DoesNotContain(StaleMovie, concatInputs);
    }

    [Fact]
    public void Source_take_url_is_never_revocable_as_a_temp()
    {
        Assert.False(CutBlobLifetime.CanRevoke(
            Source,
            ownedTemps: [HopSlice],
            inUse: [],
            pinned: []));
    }

    [Fact]
    public void In_use_hop_slice_stays_until_ffmpeg_finishes()
    {
        Assert.False(CutBlobLifetime.CanRevoke(
            HopSlice,
            ownedTemps: [HopSlice],
            inUse: [HopSlice],
            pinned: []));
        Assert.True(CutBlobLifetime.CanRevoke(
            HopSlice,
            ownedTemps: [HopSlice],
            inUse: [],
            pinned: []));
    }

    [Fact]
    public void Live_prefix_and_movie_stay_pinned()
    {
        Assert.False(CutBlobLifetime.CanRevoke(
            Prefix,
            ownedTemps: [Prefix, StaleMovie],
            inUse: [],
            pinned: [Prefix]));
        Assert.True(CutBlobLifetime.CanRevoke(
            StaleMovie,
            ownedTemps: [Prefix, StaleMovie],
            inUse: [],
            pinned: [Prefix]));
    }
}
