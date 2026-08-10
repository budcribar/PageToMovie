using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CastLocationNormalizeTests
{
    [Fact]
    public void NormalizeCastDoc_keeps_location_seed_tokens()
    {
        var parsed = new Dictionary<string, object?>
        {
            ["schema_version"] = "cast_seeds.v1",
            ["movie_title"] = "Test",
            ["character_seed_tokens"] = new Dictionary<string, object?>
            {
                ["Character_Bob"] = new Dictionary<string, object?>
                {
                    ["canonical_given_name"] = "Bob",
                    ["species_kind"] = "human",
                    ["display_name_policy"] = "ok_anytime",
                    ["description"] = "Adult man with brown hair and a blue work jacket",
                    ["visual_lock"] = "Same brown-haired man in blue work jacket",
                    ["voice_label"] = "Bob",
                    ["voice_profile"] = "Adult male",
                },
            },
            ["location_seed_tokens"] = new Dictionary<string, object?>
            {
                ["Loc_Kitchen"] = new Dictionary<string, object?>
                {
                    ["display_name"] = "KITCHEN",
                    ["location_type"] = "INT",
                    ["description"] = "Narrow 1970s galley kitchen: linoleum floor, avocado fridge, formica counters, single fluorescent fixture.",
                    ["visual_lock"] = "Always the same avocado fridge and formica counters; no modern appliances.",
                },
            },
        };

        var doc = CastFromScreenplayService.NormalizeCastDoc(parsed, "proj");
        Assert.True(doc.TryGetValue("location_seed_tokens", out var locObj));
        var locs = Assert.IsType<Dictionary<string, object?>>(locObj);
        Assert.True(locs.ContainsKey("Loc_Kitchen"));
        var kitchen = Assert.IsType<Dictionary<string, object?>>(locs["Loc_Kitchen"]);
        Assert.Contains("avocado", kitchen["description"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("formica", kitchen["visual_lock"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLocationHintsFromFountain_lists_heading_places()
    {
        const string fountain = """
            Title: Hints

            INT. KITCHEN - DAY

            Steam rises.

            EXT. DOCK - NIGHT

            Fog rolls in.
            """;
        var hints = CastFromScreenplayService.BuildLocationHintsFromFountain(fountain);
        Assert.Contains("Loc_", hints);
        Assert.Contains("KITCHEN", hints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOCK", hints, StringComparison.OrdinalIgnoreCase);
    }
}
