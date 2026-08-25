using PageToMovie.Engine;
using PageToMovie.ScreenplayEditor.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A planned clip carries the beat id it came from; this is what turns that id back into the
/// screenplay paragraph, so deleting a clip can delete the line instead of leaving it for the next
/// replan to re-plan.
/// </summary>
public sealed class ScreenplayBeatLocatorTests
{
    private const string Fountain = """
        Title: Locator Check

        EXT. SCHOOLHOUSE - DAY

        THE LAMB stands in the yard just outside, lingering near the step.

        NARRATOR (V.O.)
        But still he lingered near, and waited patiently about till Mary did appear.

        MARY's hand rests on the snow-white fleece. THE CHILDREN crowd the doorway.

        THE CHILDREN
        What makes the lamb love Mary so?

        INT. SCHOOLROOM - DAY

        Dust hangs in the light above the ink desks.
        """;

    private static List<(int Scene, string Id, string Text)> Stage1Beats(string fountain)
    {
        var stage1 = ScreenplayService.BuildModelFromFountainText(fountain);
        var result = new List<(int, string, string)>();
        foreach (var s in (List<object?>)stage1["scenes"]!)
        {
            var scene = (Dictionary<string, object?>)s!;
            var n = Convert.ToInt32(scene["scene_number"]);
            foreach (var b in (List<object?>)scene["story_beats"]!)
            {
                var beat = (Dictionary<string, object?>)b!;
                var dialogue = beat.GetValueOrDefault("dialogue")?.ToString() ?? "";
                var text = dialogue.Length > 0 ? dialogue : beat.GetValueOrDefault("visual_event")?.ToString() ?? "";
                result.Add((n, beat["beat_id"]!.ToString()!, text));
            }
        }
        return result;
    }

    [Fact]
    public void Every_planned_beat_resolves_to_a_screenplay_paragraph()
    {
        var model = FountainFormatter.Parse(Fountain);
        var ids = Stage1Beats(Fountain).Select(b => b.Id).ToList();

        var found = ScreenplayBeatLocator.Locate(Fountain, model, ids, out var unresolved);

        Assert.Empty(unresolved);
        Assert.Equal(ids.Count, found.Count);
    }

    [Fact]
    public void An_action_beat_resolves_to_its_own_paragraph()
    {
        var model = FountainFormatter.Parse(Fountain);
        var beat = Stage1Beats(Fountain).First(b => b.Text.Contains("lingering near", StringComparison.OrdinalIgnoreCase));

        var found = Assert.Single(ScreenplayBeatLocator.Locate(Fountain, model, new[] { beat.Id }, out _));

        var paragraph = model.Scenes[found.SceneIndex].Beats[found.BeatIndex];
        Assert.Equal(BeatType.Action, paragraph.Type);
        Assert.Contains("lingering near", paragraph.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_spoken_beat_resolves_to_the_dialogue_paragraph_not_the_action_above_it()
    {
        var model = FountainFormatter.Parse(Fountain);
        var beat = Stage1Beats(Fountain).First(b => b.Text.Contains("love Mary so", StringComparison.OrdinalIgnoreCase));

        var found = Assert.Single(ScreenplayBeatLocator.Locate(Fountain, model, new[] { beat.Id }, out _));

        var paragraph = model.Scenes[found.SceneIndex].Beats[found.BeatIndex];
        Assert.Equal(BeatType.Dialogue, paragraph.Type);
        Assert.Contains("love Mary so", paragraph.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Beats_resolve_into_the_scene_they_belong_to()
    {
        var model = FountainFormatter.Parse(Fountain);
        var second = Stage1Beats(Fountain).First(b => b.Text.Contains("ink desks", StringComparison.OrdinalIgnoreCase));

        var found = Assert.Single(ScreenplayBeatLocator.Locate(Fountain, model, new[] { second.Id }, out _));

        Assert.Equal(2, found.SceneNumber);
        Assert.Equal(2, model.Scenes[found.SceneIndex].SceneNumber);
    }

    /// <summary>An id from a hand-edited clip matches nothing, and is reported rather than guessed.</summary>
    [Fact]
    public void An_id_that_matches_nothing_is_reported_unresolved()
    {
        var model = FountainFormatter.Parse(Fountain);

        var found = ScreenplayBeatLocator.Locate(Fountain, model, new[] { "sb_deadbeefdead" }, out var unresolved);

        Assert.Empty(found);
        Assert.Equal(new[] { "sb_deadbeefdead" }, unresolved);
    }

    /// <summary>
    /// A long speech is split across clips as <c>{root}#pNofM</c>, but the screenplay holds one
    /// line — so every part must land on that single paragraph, which is what lets a "delete this
    /// clip" prompt say the whole group goes together.
    /// </summary>
    [Fact]
    public void Split_monologue_parts_all_resolve_to_the_one_line()
    {
        var model = FountainFormatter.Parse(Fountain);
        var beat = Stage1Beats(Fountain).First(b => b.Text.Contains("lingered near", StringComparison.OrdinalIgnoreCase));
        var root = PageToMovie.Core.Utils.StableBeatId.Root(beat.Id);

        var found = ScreenplayBeatLocator.Locate(
            Fountain, model, new[] { $"{root}#p1of3", $"{root}#p2of3", $"{root}#p3of3" }, out var unresolved);

        Assert.Empty(unresolved);
        Assert.Single(found);
    }
}
