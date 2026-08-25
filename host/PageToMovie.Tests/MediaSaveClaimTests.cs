using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Two browser windows signed in as one user receive the same JobUpdated and each ran their own
/// per-window save de-duplication, so both wrote the same file into the same folder. The writer
/// truncates on open, so the second write destroyed the first, and the folder's take resolver
/// skips files under 1 KB and falls back to the newest surviving take — three distinct takes came
/// back as one video (Mary19 S02C02 takes 6-8).
/// </summary>
public class MediaSaveClaimTests
{
    private const string Path1 = "assets/video/scene_02_clip_02_take_08.mp4";
    private const string Scope = "budcribar/Mary19/c:/videos/pagetomovie";

    [Fact]
    public void Only_the_first_window_may_write_a_given_file()
    {
        var claims = new MediaSaveClaims();
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
        Assert.False(claims.TryClaim(Scope, Path1, "windowB"));
    }

    [Fact]
    public void The_next_window_may_write_once_the_holder_releases()
    {
        var claims = new MediaSaveClaims();
        claims.TryClaim(Scope, Path1, "windowA");
        claims.Release(Scope, Path1, "windowA");
        Assert.True(claims.TryClaim(Scope, Path1, "windowB"));
    }

    /// <summary>A window retrying its own save must not be locked out by its own earlier attempt.</summary>
    [Fact]
    public void Re_claiming_from_the_same_window_renews()
    {
        var claims = new MediaSaveClaims();
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
    }

    /// <summary>A straggler finishing after its lease lapsed must not free the new holder's claim.</summary>
    [Fact]
    public void A_stale_holder_cannot_release_someone_elses_claim()
    {
        var claims = new MediaSaveClaims();
        claims.TryClaim(Scope, Path1, "windowA");
        claims.Release(Scope, Path1, "windowA");
        claims.TryClaim(Scope, Path1, "windowB");

        claims.Release(Scope, Path1, "windowA");
        Assert.False(claims.TryClaim(Scope, Path1, "windowC"));
    }

    /// <summary>A window that closes mid-save must not make the file unwritable forever.</summary>
    [Fact]
    public void An_abandoned_claim_expires()
    {
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var claims = new MediaSaveClaims(() => now);
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
        Assert.False(claims.TryClaim(Scope, Path1, "windowB"));

        now = now.Add(MediaSaveClaims.LeaseDuration).AddSeconds(1);
        Assert.True(claims.TryClaim(Scope, Path1, "windowB"));
        Assert.Equal(1, claims.ActiveCount);
    }

    [Fact]
    public void Different_takes_never_contend()
    {
        var claims = new MediaSaveClaims();
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
        Assert.True(claims.TryClaim(
            Scope, "assets/video/scene_02_clip_02_take_07.mp4", "windowB"));
    }

    /// <summary>
    /// Two windows on DIFFERENT folders must both write — blocking the second would leave that
    /// folder silently missing takes, which is worse than the collision this class prevents.
    /// </summary>
    [Fact]
    public void Different_folders_do_not_contend()
    {
        var claims = new MediaSaveClaims();
        Assert.True(claims.TryClaim("budcribar/Mary19/c:/videos", Path1, "windowA"));
        Assert.True(claims.TryClaim("budcribar/Mary19/d:/backup", Path1, "windowB"));
    }

    /// <summary>Separators and casing vary between the two windows' reported paths; the same file
    /// must still be recognised as the same file.</summary>
    [Fact]
    public void Path_shape_does_not_create_a_second_claim()
    {
        var claims = new MediaSaveClaims();
        Assert.True(claims.TryClaim(Scope, Path1, "windowA"));
        Assert.False(claims.TryClaim(Scope, @"/Assets\Video\Scene_02_Clip_02_Take_08.MP4", "windowB"));
    }

    [Fact]
    public void Missing_identifiers_are_never_granted()
    {
        var claims = new MediaSaveClaims();
        Assert.False(claims.TryClaim(Scope, Path1, ""));
        Assert.False(claims.TryClaim(Scope, "", "windowA"));
        Assert.False(claims.TryClaim("", Path1, "windowA"));
    }
}
