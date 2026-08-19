using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public sealed class Stage2PlannerAutomationTests
{
    [Fact]
    public void CoalesceSilentPreludeBeats_MergesSilentBeat1IntoBeat2()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE OLD MAN turns in a shaft of gray light.",
                ["dialogue"] = "",
                ["speaker"] = ""
            },
            new()
            {
                ["beat_id"] = "b2",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE NARRATOR's face goes cold.",
                ["dialogue"] = "He had the eye of a vulture.",
                ["speaker"] = "Character_The_Narrator"
            }
        };

        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        Assert.Single(coalesced);
        Assert.Equal("b2", coalesced[0]["beat_id"]);
        Assert.Equal("He had the eye of a vulture.", coalesced[0]["dialogue"]);
        Assert.Contains("THE OLD MAN turns in a shaft of gray light.", coalesced[0]["visual_event"]?.ToString());
        Assert.Contains("THE NARRATOR's face goes cold.", coalesced[0]["visual_event"]?.ToString());
    }

    [Fact]
    public void CoalesceSilentPreludeBeats_LeavesSceneUnchangedIfBeat1HasDialogue()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE OLD MAN turns in a shaft of gray light.",
                ["dialogue"] = "Who's there?",
                ["speaker"] = "Character_The_Old_Man"
            },
            new()
            {
                ["beat_id"] = "b2",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE NARRATOR stands motionless.",
                ["dialogue"] = "I kept quite still.",
                ["speaker"] = "Character_The_Narrator"
            }
        };

        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        Assert.Equal(2, coalesced.Count);
        Assert.Equal("b1", coalesced[0]["beat_id"]);
    }

    private sealed class MockChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "";

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task ShotPlanRefiner_UpdatesVisualPromptsAndContinuationSources()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "refinements": [
                {
                  "clip_number": 1,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. Wide establishing shot. Character_The_Narrator in doorway.",
                  "veo_continuation_source": "none"
                },
                {
                  "clip_number": 2,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. ECU on Character_The_Old_Man pale blue eye.",
                  "veo_continuation_source": "none"
                },
                {
                  "clip_number": 3,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. Medium shot on Character_The_Narrator shuddering.",
                  "veo_continuation_source": "extend_previous"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyShotPlanRefineWithChat = true });
        var refiner = new ShotPlanRefiningClassifier(mockChat, opts, NullLogger<ShotPlanRefiningClassifier>.Instance);

        var clips = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["clip_number"] = 1,
                ["duration_seconds"] = 5,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "none"
            },
            new Dictionary<string, object?>
            {
                ["clip_number"] = 2,
                ["duration_seconds"] = 8,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "extend_previous"
            },
            new Dictionary<string, object?>
            {
                ["clip_number"] = 3,
                ["duration_seconds"] = 6,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "extend_previous"
            }
        };

        var plannedScene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. BEDCHAMBER - DAY",
            ["characters_on_screen"] = new List<object?> { "Character_The_Narrator", "Character_The_Old_Man" },
            ["veo_clips"] = clips
        };

        var applied = await refiner.RefinePlannedSceneAsync(plannedScene);

        Assert.True(applied);
        var updatedClips = ((List<object?>)plannedScene["veo_clips"]!).OfType<Dictionary<string, object?>>().ToList();
        Assert.Contains("Wide establishing shot", updatedClips[0]["visual_prompt"]?.ToString());
        Assert.Contains("ECU on Character_The_Old_Man", updatedClips[1]["visual_prompt"]?.ToString());
        Assert.Equal("none", updatedClips[1]["veo_continuation_source"]);
        Assert.Equal("extend_previous", updatedClips[2]["veo_continuation_source"]);
    }

    private static string ResolveTellTaleHeartFountainPath()
    {
        // Portable fixture lookup (independent of machine/drive/username): mirrors the
        // Fixtures\CastExtractGold pattern used elsewhere in this test project — check the
        // build output's copied Fixtures folder first, then fall back to the source tree
        // location relative to the test assembly when running from an IDE that hasn't copied it.
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "TellTaleHeartV7", "screenplay.fountain"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TellTaleHeartV7", "screenplay.fountain")
        };
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
        }
        return paths[0];
    }

    [Fact]
    public void CoalesceSilentPreludeBeats_TellTaleHeartScene2_CoalescesClip1IntoFrame1VO()
    {
        var fountainPath = ResolveTellTaleHeartFountainPath();
        var text = System.IO.File.ReadAllText(fountainPath);
        var model = ScreenplayService.BuildModelFromFountainText(text);

        var scenes = Stage2PlannerService.GetScenes(model);
        var scene2 = scenes.FirstOrDefault(s => Stage2PlannerService.ToInt(s.GetValueOrDefault("scene_number")) == 2);
        Assert.NotNull(scene2);

        var beats = Stage2PlannerService.GetList(scene2!, "story_beats").OfType<Dictionary<string, object?>>().ToList();
        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        // Before coalescing: Beat 1 was silent action, Beats 2-6 were VO dialogue (6 beats total).
        // After coalescing + orphan word merging ("it"): Yields 4 beats total with VO dialogue on frame 1.
        Assert.Equal(4, coalesced.Count);
        Assert.Contains("He had the eye of a vulture", coalesced[0]["dialogue"]?.ToString());
        Assert.Contains("THE OLD MAN turns in a shaft of gray light", coalesced[0]["visual_event"]?.ToString());
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_MergesShortDifferentSpeakerBeats()
    {
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Nick",
            ["dialogue"] = "You coming or not?",
            ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Sionna",
            ["dialogue"] = "Give me a second.",
            ["location_id"] = "Loc_Porch",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);

        Assert.Single(coalesced);
        Assert.Equal("Character_Nick", coalesced[0]["speaker"]);
        Assert.Equal("You coming or not?", coalesced[0]["dialogue"]);
        Assert.Equal("Character_Sionna", coalesced[0]["secondary_speaker"]);
        Assert.Equal("Give me a second.", coalesced[0]["secondary_dialogue"]);
    }

    // ── Talking characters per scene (2 must work; 3 is untested / future) ──────────────────────

    private static Dictionary<string, object?> DialogueBeat(string id, string speaker, string line, string loc = "Loc_Hall") =>
        new() { ["beat_id"] = id, ["speaker"] = speaker, ["dialogue"] = line, ["location_id"] = loc };

    /// <summary>Every (speaker, line) actually carried by the coalesced clips — primary AND the
    /// two-hander secondary — so a dropped line is detectable.</summary>
    private static List<(string speaker, string line)> AllSpokenLines(IEnumerable<Dictionary<string, object?>> clips)
    {
        var outp = new List<(string, string)>();
        foreach (var c in clips)
        {
            string? S(string k) => c.TryGetValue(k, out var v) ? v?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(S("dialogue"))) outp.Add((S("speaker") ?? "", S("dialogue")!));
            if (!string.IsNullOrWhiteSpace(S("secondary_dialogue"))) outp.Add((S("secondary_speaker") ?? "", S("secondary_dialogue")!));
        }
        return outp;
    }

    private static int SpeakersOnClip(Dictionary<string, object?> c)
    {
        var n = 0;
        if (c.TryGetValue("dialogue", out var d) && !string.IsNullOrWhiteSpace(d?.ToString())) n++;
        if (c.TryGetValue("secondary_dialogue", out var s) && !string.IsNullOrWhiteSpace(s?.ToString())) n++;
        return n;
    }

    [Fact]
    public void TwoTalkersPerScene_MergeIntoOneTwoHander_NoLineLost()
    {
        // Two talking characters in one scene MUST work: A + B coalesce into a single two-hander
        // clip (camera pans A→B), both lines preserved, exactly two speakers on the clip.
        var beats = new List<Dictionary<string, object?>>
        {
            DialogueBeat("b1", "Character_Alice", "We convene at last."),
            DialogueBeat("b2", "Character_Boris", "The north stands ready."),
        };

        var clips = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 12);

        Assert.Single(clips);
        Assert.Equal(2, SpeakersOnClip(clips[0]));
        var lines = AllSpokenLines(clips);
        Assert.Contains(("Character_Alice", "We convene at last."), lines);
        Assert.Contains(("Character_Boris", "The north stands ready."), lines);
    }

    [Fact]
    public void ThreeTalkersPerScene_KeepsEveryLine_AndNoClipExceedsTwoSpeakers()
    {
        // FUTURE / untested: three talking characters in one scene. Graceful handling means every
        // line survives and no clip carries more than two speakers (there is no "three-hander").
        var beats = new List<Dictionary<string, object?>>
        {
            DialogueBeat("b1", "Character_Alice", "We convene at last."),
            DialogueBeat("b2", "Character_Boris", "The north stands ready."),
            DialogueBeat("b3", "Character_Cora", "And the ledger balances."),
        };

        var clips = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 12);

        var lines = AllSpokenLines(clips);
        Assert.Contains(("Character_Alice", "We convene at last."), lines);
        Assert.Contains(("Character_Boris", "The north stands ready."), lines);
        Assert.Contains(("Character_Cora", "And the ledger balances."), lines);   // the 3rd speaker must not vanish
        Assert.All(clips, c => Assert.True(SpeakersOnClip(c) <= 2,
            $"a clip carries more than two speakers: {string.Join(" | ", AllSpokenLines(new[] { c }).Select(l => l.speaker))}"));
    }

    [Fact]
    public void ThreeTalkersPerScene_RapidExchange_KeepsEveryLine()
    {
        // A rapid A→B→C→A→B→C exchange in one scene. Untested territory: verify no line is dropped
        // however the pairs fall out.
        var speakers = new[] { "Character_Alice", "Character_Boris", "Character_Cora",
                               "Character_Alice", "Character_Boris", "Character_Cora" };
        var beats = speakers.Select((sp, i) => DialogueBeat($"b{i}", sp, $"Line {i} from {sp}.")).ToList();

        var clips = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 12);

        var lines = AllSpokenLines(clips);
        for (var i = 0; i < speakers.Length; i++)
            Assert.Contains((speakers[i], $"Line {i} from {speakers[i]}."), lines);
        Assert.All(clips, c => Assert.True(SpeakersOnClip(c) <= 2, "a clip carries more than two speakers"));
    }

    // ── Per-model speakers-per-clip policy (catalog-driven, forward-thinking) ────────────────────

    [Fact]
    public void ResolveMaxSpeakersPerClip_Grok_isOneSpeakerPerClip()
    {
        // Catalog is the source of truth. Grok's best results today are one speaker per clip.
        SupportedModelCatalog.ReloadCatalog();
        Assert.Equal(1, Stage2PlannerService.ResolveMaxSpeakersPerClip("grok-imagine-video"));
    }

    [Fact]
    public void ResolveMaxSpeakersPerClip_unknownModel_defaultsToOne()
    {
        // Safe default: one speaker per clip is always renderable, so an unset/unknown model is 1.
        Assert.Equal(1, Stage2PlannerService.ResolveMaxSpeakersPerClip("no-such-model-xyz"));
    }

    [Fact]
    public void ApplyCrossSpeakerCoalescing_oneSpeakerPolicy_keepsSingleSpeakerClips()
    {
        // maxSpeakersPerClip == 1 (Grok today): A and B stay separate one-speaker clips
        // (shot-reverse-shot), never merged into a two-hander.
        var beats = new List<Dictionary<string, object?>>
        {
            DialogueBeat("b1", "Character_Alice", "We convene at last."),
            DialogueBeat("b2", "Character_Boris", "The north stands ready."),
        };

        var clips = Stage2PlannerService.ApplyCrossSpeakerCoalescing(beats, maxSpeakersPerClip: 1, maxSeconds: 12);

        Assert.Equal(2, clips.Count);
        Assert.All(clips, c => Assert.Equal(1, SpeakersOnClip(c)));
        Assert.DoesNotContain(clips, c => c.ContainsKey("secondary_speaker"));
    }

    [Fact]
    public void ApplyCrossSpeakerCoalescing_twoSpeakerPolicy_mergesIntoTwoHander()
    {
        // maxSpeakersPerClip >= 2 (a future model): A + B coalesce into one two-hander clip.
        var beats = new List<Dictionary<string, object?>>
        {
            DialogueBeat("b1", "Character_Alice", "We convene at last."),
            DialogueBeat("b2", "Character_Boris", "The north stands ready."),
        };

        var clips = Stage2PlannerService.ApplyCrossSpeakerCoalescing(beats, maxSpeakersPerClip: 2, maxSeconds: 12);

        Assert.Single(clips);
        Assert.Equal("Character_Boris", clips[0]["secondary_speaker"]);
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_SizesMergedClipForBothLines()
    {
        // Regression: the merge set secondary_dialogue but left duration_seconds at the primary's
        // size, so the second speaker's line got cut. It must now cover BOTH lines.
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Children",
            ["dialogue"] = "Why does the lamb love Mary so?",
            ["location_id"] = "Loc_Class",
            ["duration_seconds"] = 3,
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Teacher",
            ["dialogue"] = "Oh, Mary loves the lamb, you know.",
            ["location_id"] = "Loc_Class",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);

        Assert.Single(coalesced);
        Assert.Equal("Character_Teacher", coalesced[0]["secondary_speaker"]);
        Assert.Equal("Oh, Mary loves the lamb, you know.", coalesced[0]["secondary_dialogue"]);

        var mergedDuration = Convert.ToInt32(coalesced[0]["duration_seconds"]);
        var primaryOnly = ClipDurationEstimator.Estimate(
            "Why does the lamb love Mary so?", "", "dialogue", "spoken_on_camera");
        Assert.True(mergedDuration > primaryOnly,
            $"merged duration {mergedDuration}s must cover both lines, not just the primary {primaryOnly}s");
        Assert.InRange(mergedDuration, ClipDurationEstimator.MinSeconds, 10);
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_LeavesSameSpeakerBeatsUnmerged()
    {
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Nick",
            ["dialogue"] = "You coming or not?",
            ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Nick",
            ["dialogue"] = "Well?",
            ["location_id"] = "Loc_Porch",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);

        Assert.Equal(2, coalesced.Count);
        Assert.False(coalesced[0].ContainsKey("secondary_speaker"));
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_DoesNotMergeWhenEitherLineExceedsPerLineCap()
    {
        var longLine = string.Join(" ", Enumerable.Repeat("word", 20)); // well over half of a 10s model max
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Nick",
            ["dialogue"] = longLine,
            ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Sionna",
            ["dialogue"] = "Give me a second.",
            ["location_id"] = "Loc_Porch",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);

        Assert.Equal(2, coalesced.Count);
        Assert.False(coalesced[0].ContainsKey("secondary_speaker"));
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_DoesNotMergeAcrossDifferentLocations()
    {
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Nick",
            ["dialogue"] = "You coming or not?",
            ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Sionna",
            ["dialogue"] = "Give me a second.",
            ["location_id"] = "Loc_Kitchen",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);

        Assert.Equal(2, coalesced.Count);
        Assert.False(coalesced[0].ContainsKey("secondary_speaker"));
    }

    [Fact]
    public void CoalesceCrossSpeakerDialogueBeats_RespectsTighterExtensionCapWhenGroupExtends()
    {
        // Each line (4 words, ~2.44s) fits under the fresh per-line cap (10/2=5) but not the
        // tighter extension per-line cap (4/2=2) — a clip that will extend from the previous one
        // (e.g. Grok's tighter MaxExtensionSeconds) must not merge past what it can actually fit.
        var line = string.Join(" ", Enumerable.Repeat("word", 4));
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1", ["speaker"] = "Character_Nick", ["dialogue"] = line, ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2", ["speaker"] = "Character_Sionna", ["dialogue"] = line, ["location_id"] = "Loc_Porch",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var extended = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(
            beats, maxSeconds: 10, extensionMaxSeconds: 4, extendsFromPrevious: new[] { true, false });
        Assert.Equal(2, extended.Count);
        Assert.False(extended[0].ContainsKey("secondary_speaker"));

        var fresh = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(
            beats, maxSeconds: 10, extensionMaxSeconds: 4, extendsFromPrevious: new[] { false, false });
        Assert.Single(fresh);
        Assert.Equal("Character_Sionna", fresh[0]["secondary_speaker"]);
    }

    [Fact]
    public void CoalesceShortMonologueBeats_RespectsTighterExtensionCapWhenGroupExtends()
    {
        // Combined dialogue (17 words, ~7.4s) fits under a fresh max of 10 but not a tighter
        // extension max of 6.
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1", ["speaker"] = "Character_Nick",
            ["dialogue"] = string.Join(" ", Enumerable.Repeat("word", 8)),
            ["location_id"] = "Loc_Porch", ["delivery"] = "spoken_on_camera",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2", ["speaker"] = "Character_Nick",
            ["dialogue"] = string.Join(" ", Enumerable.Repeat("word", 9)),
            ["location_id"] = "Loc_Porch", ["delivery"] = "spoken_on_camera",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var extended = Stage2PlannerService.CoalesceShortMonologueBeats(
            beats, maxSeconds: 10, extensionMaxSeconds: 6, extendsFromPrevious: new[] { true, false });
        Assert.Equal(2, extended.Count);

        var fresh = Stage2PlannerService.CoalesceShortMonologueBeats(
            beats, maxSeconds: 10, extensionMaxSeconds: 6, extendsFromPrevious: new[] { false, false });
        Assert.Single(fresh);
    }

    [Fact]
    public void Coalesce_WithoutExtendInfo_BehavesExactlyAsBeforeUsingFreshMaxOnly()
    {
        var line = string.Join(" ", Enumerable.Repeat("word", 4));
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1", ["speaker"] = "Character_Nick", ["dialogue"] = line, ["location_id"] = "Loc_Porch",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2", ["speaker"] = "Character_Sionna", ["dialogue"] = line, ["location_id"] = "Loc_Porch",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats(beats, maxSeconds: 10);
        Assert.Single(coalesced);
    }
}

/// <summary>Mary19 S02: Stage 1 emits [silent action beat] + [narration beat carrying the SAME visual] pairs
/// mid-scene (C02/C03 children laugh; C04/C05 teacher steers the lamb out). The prelude rule only covered
/// beat 1, so the rest became back-to-back clips replaying one action.</summary>
public class Stage2DuplicateActionVoTests
{
    private static Dictionary<string, object?> Beat(string id, string visual, string dialogue = "", string speaker = "", string loc = "Loc_Schoolroom") => new()
    {
        ["beat_id"] = id, ["location_id"] = loc, ["visual_event"] = visual, ["dialogue"] = dialogue, ["speaker"] = speaker,
    };

    [Fact]
    public void Silent_action_then_narration_over_the_same_action_becomes_one_beat_anywhere_in_the_scene()
    {
        const string steer = "TEACHER closes the book, takes THE LAMB gently by a handful of wool, and steers him toward the open door.";
        var beats = new List<Dictionary<string, object?>>
        {
            Beat("b1", "THE CHILDREN twist in their seats and point.", "He followed her to school one day.", "Character_Narrator"),
            Beat("b2", "THE CHILDREN laugh and clap at the lamb."),
            Beat("b3", "THE CHILDREN laugh and clap at the lamb.", "It made the children laugh and play.", "Character_Narrator"),
            Beat("b4", steer),
            Beat("b5", steer, "And so the teacher turned him out.", "Character_Narrator"),
            Beat("b6", "The white shape of THE LAMB crosses the threshold into daylight."),
        };

        var merged = PageToMovie.Engine.Stage2PlannerService.CoalesceDuplicateActionVoBeats(beats);

        Assert.Equal(new[] { "b1", "b3", "b5", "b6" }, merged.Select(b => b["beat_id"]!.ToString()).ToArray());
        Assert.Equal("And so the teacher turned him out.", merged[2]["dialogue"]);
        Assert.Contains("steers him toward the open door", merged[2]["visual_event"]!.ToString());
    }

    [Fact]
    public void Silent_action_followed_by_a_DIFFERENT_action_with_dialogue_is_not_merged()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            Beat("b1", "Mary opens the gate."),
            Beat("b2", "The lamb trots after her down the lane.", "And everywhere that Mary went…", "Character_Narrator"),
        };
        var merged = PageToMovie.Engine.Stage2PlannerService.CoalesceDuplicateActionVoBeats(beats);
        Assert.Equal(2, merged.Count);
    }
}

public class Stage2DuplicateActionVoKeepsItsOwnClipTests
{
    private static Dictionary<string, object?> Beat(string id, string visual, string dialogue = "", string speaker = "") => new()
    {
        ["beat_id"] = id, ["location_id"] = "Loc_Country_Lane", ["visual_event"] = visual, ["dialogue"] = dialogue, ["speaker"] = speaker,
    };

    /// <summary>Mary19 S01 after the duplicate-action merge: [VO1, silent, VO2(same action), silent] → the merged
    /// action+VO2 beat sat next to VO1 and the monologue coalescer absorbed it — one clip, the second verse
    /// concatenated and then lost. The merged beat must keep its own clip.</summary>
    [Fact]
    public void Merged_action_plus_line_is_not_absorbed_into_the_previous_narration()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            Beat("b1", "A painted country lane; MARY walks with THE LAMB.", "Mary had a little lamb, Its fleece was white as snow.", "Character_Narrator"),
            Beat("b2", "MARY turns along the lane, one hand swinging free."),
            Beat("b3", "MARY turns along the lane, one hand swinging free.", "And everywhere that Mary went, The lamb was sure to go.", "Character_Narrator"),
            Beat("b4", "Mary passes a painted rail fence and a wash of meadow flowers."),
        };
        var afterDup = PageToMovie.Engine.Stage2PlannerService.CoalesceDuplicateActionVoBeats(beats);
        Assert.Equal(3, afterDup.Count);
        var afterMono = PageToMovie.Engine.Stage2PlannerService.CoalesceShortMonologueBeats(afterDup, maxSeconds: 15);
        Assert.Equal(3, afterMono.Count);
        Assert.Equal("Mary had a little lamb, Its fleece was white as snow.", afterMono[0]["dialogue"]);
        Assert.Equal("And everywhere that Mary went, The lamb was sure to go.", afterMono[1]["dialogue"]);
    }
}
