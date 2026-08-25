using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.ScreenplayEditor.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Whether editing the screenplay keeps existing clips attached to it. A clip is bound to its beat
/// by a content hash of (scene heading, kind, speaker, text, repeat index), so an edit that changes
/// any of those for an existing beat detaches every clip planned from it. These pin down which
/// edits are safe and which one is not, because the answer is not uniform and the unsafe case is
/// not obvious.
/// </summary>
public sealed class ScreenplayRoundTripTests
{
    private const string Original = """
        Title: Round Trip

        EXT. SCHOOLHOUSE - DAY

        THE LAMB waits by the step.

        THE CHILDREN
        What makes the lamb love Mary so?

        INT. SCHOOLROOM - DAY

        Dust hangs above the ink desks.
        """;

    /// <summary>Scene number → beat id → the beat's text, as Stage 1 sees it.</summary>
    private static Dictionary<string, string> BeatIds(string fountain)
    {
        var stage1 = ScreenplayService.BuildModelFromFountainText(fountain);
        var map = new Dictionary<string, string>();
        foreach (var s in (List<object?>)stage1["scenes"]!)
        {
            var scene = (Dictionary<string, object?>)s!;
            foreach (var b in (List<object?>)scene["story_beats"]!)
            {
                var beat = (Dictionary<string, object?>)b!;
                var dialogue = beat.GetValueOrDefault("dialogue")?.ToString() ?? "";
                map[StableBeatId.Root(beat["beat_id"]!.ToString())] =
                    dialogue.Length > 0 ? dialogue : beat.GetValueOrDefault("visual_event")?.ToString() ?? "";
            }
        }
        return map;
    }

    private static string Rewrite(string fountain, Action<ScreenplayModel> edit)
    {
        var model = FountainFormatter.Parse(fountain);
        edit(model);
        return model.ToFountain();
    }

    /// <summary>Opening and saving the editor without touching anything must change nothing.</summary>
    [Fact]
    public void An_untouched_round_trip_keeps_every_beat_id()
    {
        var after = Rewrite(Original, _ => { });

        Assert.Equal(BeatIds(Original).Keys.OrderBy(k => k), BeatIds(after).Keys.OrderBy(k => k));
    }

    [Fact]
    public void Adding_a_scene_keeps_the_existing_scenes_beat_ids()
    {
        var after = Rewrite(Original, m => m.Scenes.Add(new ScreenplayScene
        {
            SceneNumber = 99,
            SceneTitle = "INT. CLOAKROOM - DAY",
            Beats = { new ScreenplayBeat { Type = BeatType.Action, Text = "Coats drip onto the boards." } },
        }));

        var before = BeatIds(Original);
        var now = BeatIds(after);
        Assert.All(before.Keys, id => Assert.Contains(id, now.Keys));
    }

    [Fact]
    public void Adding_dialogue_keeps_the_existing_beat_ids()
    {
        var after = Rewrite(Original, m => m.Scenes[0].Beats.Add(new ScreenplayBeat
        {
            Type = BeatType.Dialogue,
            Speaker = "TEACHER",
            Text = "Oh, Mary loves the lamb, you know.",
        }));

        var before = BeatIds(Original);
        var now = BeatIds(after);
        Assert.All(before.Keys, id => Assert.Contains(id, now.Keys));
    }

    /// <summary>
    /// The one edit that detaches clips. Stage 1 accumulates consecutive action paragraphs and
    /// flushes them as a single beat, so a new action line written next to an existing one becomes
    /// part of THAT beat rather than a beat of its own — the beat's text changes, so its content
    /// hash changes, and every clip planned from it stops resolving.
    /// </summary>
    /// <remarks>
    /// Nothing is silently wrong when this happens: the clip keeps working, and the write-back
    /// reports the id as unresolved rather than guessing, so the UI offers only a shot-plan delete.
    /// Rebuilding that scene's shot list re-attaches everything. Putting the new line after a
    /// dialogue beat, rather than beside another action line, avoids it entirely.
    /// </remarks>
    [Fact]
    public void Adding_an_action_line_beside_another_one_detaches_that_beat()
    {
        var after = Rewrite(Original, m => m.Scenes[0].Beats.Insert(1, new ScreenplayBeat
        {
            Type = BeatType.Action,
            Text = "A bell rings somewhere inside.",
        }));

        var before = BeatIds(Original);
        var now = BeatIds(after);
        var lambBeat = before.First(kv => kv.Value.Contains("waits by the step", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(lambBeat.Key, now.Keys);
        // It is not lost — it is merged into one beat carrying both lines.
        Assert.Contains(now.Values, v =>
            v.Contains("waits by the step", StringComparison.OrdinalIgnoreCase)
            && v.Contains("A bell rings", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A detached id is reported, never matched to the wrong paragraph — which is what makes the
    /// failure mode safe rather than destructive.
    /// </summary>
    [Fact]
    public void A_detached_beat_id_is_reported_unresolved_rather_than_mismatched()
    {
        var after = Rewrite(Original, m => m.Scenes[0].Beats.Insert(1, new ScreenplayBeat
        {
            Type = BeatType.Action,
            Text = "A bell rings somewhere inside.",
        }));
        var stale = BeatIds(Original)
            .First(kv => kv.Value.Contains("waits by the step", StringComparison.OrdinalIgnoreCase)).Key;

        var found = ScreenplayBeatLocator.Locate(
            after, FountainFormatter.Parse(after), new[] { stale }, out var unresolved);

        Assert.Empty(found);
        Assert.Equal(new[] { stale }, unresolved);
    }
}
