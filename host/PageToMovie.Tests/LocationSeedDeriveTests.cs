using PageToMovie.Engine;
using PageToMovie.Fountain;
using Xunit;

namespace PageToMovie.Tests;

public class LocationSeedDeriveTests
{
    [Fact]
    public void BuildStage1_enriches_location_description_from_action()
    {
        const string fountain = """
            Title: Loc Test

            INT. KIRK STREET APARTMENT - NIGHT

            A blue television glow washes the living room. Empty pizza boxes stack by the couch.

            JOE
            You home?
            """;

        var doc = FountainStage1Importer.BuildStage1(FountainParser.Parse(fountain));
        var gpv = (Dictionary<string, object?>)doc["global_production_variables"]!;
        var locs = (Dictionary<string, object?>)gpv["location_seed_tokens"]!;
        Assert.NotEmpty(locs);

        var seed = locs.Values.OfType<Dictionary<string, object?>>().First();
        var display = seed["display_name"]?.ToString() ?? "";
        Assert.Contains("KIRK", display, StringComparison.OrdinalIgnoreCase);
        var desc = seed["description"]?.ToString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(desc));
        Assert.Contains("television", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStage1_location_ids_stable_across_reimport()
    {
        const string fountain = """
            Title: Stable Loc

            EXT. DOCK - DAY

            Gulls wheel over the water.

            EXT. DOCK - NIGHT

            Fog rolls in.
            """;

        var a = FountainStage1Importer.BuildStage1(FountainParser.Parse(fountain));
        var b = FountainStage1Importer.BuildStage1(FountainParser.Parse(fountain));
        var la = (Dictionary<string, object?>)((Dictionary<string, object?>)a["global_production_variables"]!)["location_seed_tokens"]!;
        var lb = (Dictionary<string, object?>)((Dictionary<string, object?>)b["global_production_variables"]!)["location_seed_tokens"]!;
        Assert.Equal(la.Keys.OrderBy(k => k), lb.Keys.OrderBy(k => k));
        Assert.Single(la); // same place day/night → one Loc_*
    }
}
