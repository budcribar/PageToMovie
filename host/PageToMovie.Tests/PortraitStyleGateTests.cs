using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class PortraitStyleGateTests
{
    [Theory]
    [InlineData("STYLE LOCK: Live-action gothic period drama; photoreal human faces", false)]
    [InlineData("STYLE LOCK: children's picture-book illustration, painted cartoon", true)]
    [InlineData("photoreal live-action period drama circa 1840s", false)]
    [InlineData("STYLE LOCK: photoreal live-action continuity portrait — naturalistic face", false)]
    [InlineData("STYLE LOCK: stylized animated children's picture-book look for ALL on-screen cast (animals and humans share the same medium) -- not photoreal, not live-action", true)]
    public void PrefersIllustrated_FromProjectStyle(string style, bool illustrated)
    {
        Assert.Equal(
            illustrated,
            CharacterDesignService.PrefersIllustratedPortraitStyle(style, hasImageHints: false, isAnimal: false));
    }

    [Fact]
    public void TryResolvePortraitStyleExpectation_NegativeClauseInIllustratedLock_ResolvesIllustration()
    {
        var style = "STYLE LOCK: stylized animated children's picture-book look for ALL on-screen cast (animals and humans share the same medium) -- not photoreal, not live-action";
        var ok = CharacterDesignService.TryResolvePortraitStyleExpectation(style, "illustrated_picture_book", out var expected);
        Assert.True(ok);
        Assert.Equal("illustration", expected);
    }

    [Fact]
    public void PrefersIllustrated_NoFileHeuristicsWithoutStyle()
    {
        // Medium must come from screenplay/cast style lock — not file type or book plates alone.
        Assert.False(CharacterDesignService.PrefersIllustratedPortraitStyle(null, hasImageHints: true, isAnimal: false));
        Assert.False(CharacterDesignService.PrefersIllustratedPortraitStyle("", hasImageHints: false, isAnimal: true));
        Assert.False(CharacterDesignService.PrefersIllustratedPortraitStyle(null, hasImageHints: false, isAnimal: false));
        Assert.False(CharacterDesignService.PrefersIllustratedPortraitStyle(
            null, hasImageHints: false, isAnimal: false, hasBookSource: true));
    }

    [Fact]
    public void ParseGate_PassPhotoreal()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":true,"medium":"photoreal","reason":"photo skin"}""");
        Assert.NotNull(g);
        Assert.True(g.Pass);
        Assert.Equal("photoreal", g.Medium);
    }

    [Fact]
    public void ParseGate_FailSketch()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":false,"medium":"sketch","reason":"pencil drawing"}""");
        Assert.NotNull(g);
        Assert.False(g.Pass);
        Assert.Equal("sketch", g.Medium);
    }

    [Fact]
    public void ParseGate_NormalizesAliases()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":true,"medium":"live-action","reason":"ok"}""");
        Assert.NotNull(g);
        Assert.Equal("photoreal", g.Medium);
    }

    [Fact]
    public void Materialize_BinStaging_SniffsPng()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ptm-gate-test-{Guid.NewGuid():N}.bin");
        try
        {
            var png = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0, 0, 0, 0, 0, 0, 0, 0
            };
            File.WriteAllBytes(tmp, png);
            var path = CharacterDesignService.MaterializeImagePathForVision(tmp, out var del);
            Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(del);
            Assert.True(File.Exists(path));
            if (del is not null) File.Delete(del);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* */ }
        }
    }

    [Fact]
    public void ParseGate_OtherMedium()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":false,"medium":"other","reason":"no image attached"}""");
        Assert.NotNull(g);
        Assert.False(g.Pass);
        Assert.Equal("other", g.Medium);
    }
}
