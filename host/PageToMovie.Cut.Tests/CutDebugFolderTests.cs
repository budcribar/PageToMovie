using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public sealed class CutDebugFolderTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5299/?debugMaryTest=1")]
    [InlineData("http://127.0.0.1:5299/?x=1&debugMaryTest=true")]
    public void ExplicitDebugFlagResolvesManifest(string url)
    {
        Assert.True(CutDebugFolder.TryManifestUrl(url, out var manifest));
        Assert.Equal("http://127.0.0.1:5299/debug-marytest.json", manifest);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5299/")]
    [InlineData("http://127.0.0.1:5299/?debugMaryTest=0")]
    [InlineData("not a URL")]
    public void MissingOrDisabledFlagKeepsNormalFolderFlow(string url)
    {
        Assert.False(CutDebugFolder.TryManifestUrl(url, out var manifest));
        Assert.Empty(manifest);
    }
}
