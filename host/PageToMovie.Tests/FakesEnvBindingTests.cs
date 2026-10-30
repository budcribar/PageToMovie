using Microsoft.Extensions.Configuration;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// LoadSim CI exports ALL_CAPS fakes/capacity env vars (githubactions:S7684). .NET configuration
/// binding ignores case; direct <c>Environment.GetEnvironmentVariable</c> reads on Linux do not.
/// </summary>
[Collection("env-serial")]
public sealed class FakesEnvBindingTests
{
    private static readonly string[] DirectFakesNames =
    {
        "PageToMovie__UseFakes",
        "PAGETOMOVIE__USEFAKES",
        "PageToMovie_USE_FAKES",
        "PAGETOMOVIE_USE_FAKES",
    };

    private static readonly string[] ConfigNames =
    {
        "PAGETOMOVIE__USEFAKES",
        "PAGETOMOVIE__CAPACITY__MAXVIDEOINFLIGHT",
        "PAGETOMOVIE__CAPACITY__MAXVIDEOINFLIGHTPERUSER",
        "PAGETOMOVIE__FAKES__VIDEODELAYMS",
        "PageToMovie__UseFakes",
        "PageToMovie__Capacity__MaxVideoInFlight",
        "PageToMovie__Capacity__MaxVideoInFlightPerUser",
        "PageToMovie__Fakes__VideoDelayMs",
    };

    [Theory]
    [InlineData("PAGETOMOVIE__USEFAKES", "true")]
    [InlineData("PAGETOMOVIE_USE_FAKES", "true")]
    [InlineData("PageToMovie__UseFakes", "true")]
    [InlineData("PageToMovie_USE_FAKES", "1")]
    public void FakeCatalogEnabled_AcceptsAllCapsAndLegacyNames(string name, string value)
    {
        var previous = Snapshot(DirectFakesNames);
        try
        {
            Clear(DirectFakesNames);
            Assert.False(SupportedModelCatalog.FakeCatalogEnabled());

            Environment.SetEnvironmentVariable(name, value);
            Assert.True(SupportedModelCatalog.FakeCatalogEnabled());
        }
        finally
        {
            Restore(previous);
        }
    }

    [Fact]
    public void AllCapsConfigEnvVars_BindPageToMovieOptions()
    {
        var previous = Snapshot(ConfigNames);
        try
        {
            // Drop the mixed-case twins so dictionary load order cannot hide the ALL_CAPS keys.
            Clear(ConfigNames);
            Environment.SetEnvironmentVariable("PAGETOMOVIE__USEFAKES", "true");
            Environment.SetEnvironmentVariable("PAGETOMOVIE__CAPACITY__MAXVIDEOINFLIGHT", "8");
            Environment.SetEnvironmentVariable("PAGETOMOVIE__CAPACITY__MAXVIDEOINFLIGHTPERUSER", "2");
            Environment.SetEnvironmentVariable("PAGETOMOVIE__FAKES__VIDEODELAYMS", "50");

            var options = new PageToMovieOptions();
            new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(PageToMovieOptions.SectionName)
                .Bind(options);

            Assert.True(options.UseFakes);
            Assert.Equal(8, options.Capacity.MaxVideoInFlight);
            Assert.Equal(2, options.Capacity.MaxVideoInFlightPerUser);
            Assert.Equal(50, options.Fakes.VideoDelayMs);
        }
        finally
        {
            Restore(previous);
        }
    }

    private static Dictionary<string, string?> Snapshot(IEnumerable<string> names)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in names)
            previous[name] = Environment.GetEnvironmentVariable(name);
        return previous;
    }

    private static void Clear(IEnumerable<string> names)
    {
        foreach (var name in names)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void Restore(Dictionary<string, string?> previous)
    {
        foreach (var (key, value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }
}
