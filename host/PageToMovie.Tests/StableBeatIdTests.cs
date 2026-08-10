using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Fountain;
using Xunit;

namespace PageToMovie.Tests;

public class StableBeatIdTests
{
    [Fact]
    public void ForContent_is_deterministic_and_prefixed()
    {
        var a = StableBeatId.ForContent("INT. ROOM - DAY", "dialogue", "Character_Bob", "Hello there.");
        var b = StableBeatId.ForContent("INT. ROOM - DAY", "dialogue", "Character_Bob", "Hello there.");
        var c = StableBeatId.ForContent("INT. ROOM - DAY", "dialogue", "Character_Bob", "Hello there!");
        Assert.True(StableBeatId.IsStable(a));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("sb_", a);
        Assert.Equal(15, a.Length); // sb_ + 12 hex
    }

    [Fact]
    public void ForPart_links_split_monologue()
    {
        var root = StableBeatId.ForContent("S1", "dialogue", "A", "long line");
        Assert.Equal(root, StableBeatId.ForPart(root, 0, 1));
        Assert.Equal(root + "#p1of3", StableBeatId.ForPart(root, 0, 3));
        Assert.Equal(root + "#p2of3", StableBeatId.ForPart(root, 1, 3));
        Assert.Equal(root, StableBeatId.Root(root + "#p2of3"));
    }

    [Fact]
    public void BuildStage1_assigns_stable_ids_stable_across_reimport()
    {
        const string fountain = """
            Title: Test

            INT. KITCHEN - DAY

            Steam rises from the kettle.

            BOB
            We should leave before dawn.
            """;

        var r1 = FountainParser.Parse(fountain);
        var r2 = FountainParser.Parse(fountain);
        var d1 = FountainStage1Importer.BuildStage1(r1);
        var d2 = FountainStage1Importer.BuildStage1(r2);

        var scenes1 = (List<object?>)d1["scenes"]!;
        var scenes2 = (List<object?>)d2["scenes"]!;
        Assert.Single(scenes1);
        var beats1 = (List<object?>)((Dictionary<string, object?>)scenes1[0]!)["story_beats"]!;
        var beats2 = (List<object?>)((Dictionary<string, object?>)scenes2[0]!)["story_beats"]!;
        Assert.True(beats1.Count >= 2);
        Assert.Equal(beats1.Count, beats2.Count);

        for (var i = 0; i < beats1.Count; i++)
        {
            var id1 = ((Dictionary<string, object?>)beats1[i]!)["beat_id"]?.ToString();
            var id2 = ((Dictionary<string, object?>)beats2[i]!)["beat_id"]?.ToString();
            Assert.True(StableBeatId.IsStable(id1), $"beat {i} not stable: {id1}");
            Assert.Equal(id1, id2);
        }
    }

    [Fact]
    public void MergeSourceIds_accumulates_provenance()
    {
        var a = new Dictionary<string, object?> { ["beat_id"] = "sb_aaaaaaaaaaaa" };
        var b = new Dictionary<string, object?> { ["beat_id"] = "sb_bbbbbbbbbbbb" };
        StableBeatId.MergeSourceIds(a, b);
        var ids = StableBeatId.CollectIds(a);
        Assert.Equal(new[] { "sb_aaaaaaaaaaaa", "sb_bbbbbbbbbbbb" }, ids);
        Assert.Equal("sb_aaaaaaaaaaaa", a["beat_id"]?.ToString());
    }
}
