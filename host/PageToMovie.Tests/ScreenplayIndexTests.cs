using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ScreenplayIndexTests
{
    [Fact]
    public void Parses_and_rolls_up_valid_index()
    {
        var json = SampleIndex(cards: 3, chaptersInBook: 0);
        Assert.True(ScreenplayIndexParser.TryParse(json, out var index, out var err), err);
        var gate = ScreenplayIndexParser.Evaluate(index);
        Assert.True(gate.Ok, string.Join(",", gate.Failures));
        var rollup = ScreenplayIndexParser.Rollup(index);
        Assert.Equal(3, rollup.SceneCards);
        Assert.Equal(2, rollup.Locations);
        Assert.Equal(2, rollup.SpeakingCast);
        Assert.Equal(1, rollup.Sequences);
    }

    [Fact]
    public void Rejects_missing_beat_and_duplicate_ids()
    {
        var bad = """
            {"schema_version":"screenplay.index.v1","movie_title":"T","source_book_title":"B",
             "acts":[{"id":"a1","title":"A","sequences":[{"id":"a1","title":"S","scenes":[
               {"id":"c1","order":1,"heading":"INT. HALL - DAY","location_key":"Loc_Hall",
                "speaking_cast":["HERO"],"beat":"","book_anchor_start":"Once","book_anchor_end":"end"}
             ]}]}]}
            """;
        Assert.True(ScreenplayIndexParser.TryParse(bad, out var index, out _));
        var gate = ScreenplayIndexParser.Evaluate(index);
        Assert.False(gate.Ok);
        Assert.Contains(gate.Failures, f => f.StartsWith("missing_beat", StringComparison.Ordinal));
        Assert.Contains(gate.Failures, f => f.StartsWith("duplicate_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Warns_on_collapse_but_does_not_cap_scene_count()
    {
        var one = SampleIndex(cards: 1, chaptersInBook: 0);
        Assert.True(ScreenplayIndexParser.TryParse(one, out var idx, out _));
        var gate = ScreenplayIndexParser.Evaluate(idx, bookText: TwentyChapters());
        Assert.True(gate.Ok);
        Assert.Contains(gate.Warnings, w => w.Contains("possible_collapse", StringComparison.Ordinal));

        var many = SampleIndex(cards: 175, chaptersInBook: 0);
        Assert.True(ScreenplayIndexParser.TryParse(many, out var big, out _));
        var bigGate = ScreenplayIndexParser.Evaluate(big, TwentyChapters());
        Assert.True(bigGate.Ok);
        Assert.Equal(175, ScreenplayIndexParser.Rollup(big).SceneCards);
        Assert.DoesNotContain(bigGate.Failures, f => f.Contains("max", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Short_books_skip_index()
    {
        Assert.False(AdaptationService.ShouldBuildIndex("Once upon a time there was a lamb.", "grok-4.6"));
        var novel = string.Join('\n', Enumerable.Repeat("word ", 70_000));
        Assert.True(AdaptationService.ShouldBuildIndex(novel, "grok-4.6"));
    }

    private static string TwentyChapters()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 20; i++)
            sb.AppendLine($"CHAPTER {i}").AppendLine("Some book text here.");
        return sb.ToString();
    }

    private static string SampleIndex(int cards, int chaptersInBook)
    {
        _ = chaptersInBook;
        var scenes = new List<string>();
        for (var i = 1; i <= cards; i++)
        {
            var loc = i % 2 == 0 ? "Loc_Sea" : "Loc_Hall";
            var who = i % 2 == 0 ? "ODYSSEUS" : "PENELOPE";
            scenes.Add(
                $"{{\"id\":\"sc.{i}\",\"order\":{i},\"heading\":\"INT. PLACE {i} - DAY\"," +
                $"\"location_key\":\"{loc}\",\"speaking_cast\":[\"{who}\"]," +
                $"\"beat\":\"Beat {i} happens.\",\"book_anchor_start\":\"Start {i}\"," +
                $"\"book_anchor_end\":\"End {i}\",\"approx_minutes\":1.5}}");
        }

        return "{\"schema_version\":\"screenplay.index.v1\",\"movie_title\":\"O\",\"source_book_title\":\"O\"," +
               "\"acts\":[{\"id\":\"act.1\",\"title\":\"A\",\"sequences\":[{\"id\":\"seq.1\",\"title\":\"S\"," +
               "\"scenes\":[" + string.Join(",", scenes) + "]}]}]}";
    }
}
