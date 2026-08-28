using System.Text.Json;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ClipDurationEstimatorTests
{
    [Fact]
    public void Short_dialogue_is_tighter_than_8s_default()
    {
        var d = ClipDurationEstimator.Estimate(
            dialogue: "Merry Christmas, Uncle!",
            visualOrAction: "Fred grins.",
            actionClass: "dialogue");
        Assert.InRange(d, ClipDurationEstimator.MinSeconds, 6);
    }

    [Fact]
    public void Long_dialogue_gets_more_time_but_stays_capped()
    {
        var line =
            "If I could work my will, every idiot who goes about with Merry Christmas on his lips " +
            "should be boiled with his own pudding and buried with a stake of holly through his heart.";
        var d = ClipDurationEstimator.Estimate(line, "Scrooge scowls.", "dialogue");
        Assert.True(d >= 6, $"expected longer clip, got {d}");
        Assert.True(d <= ClipDurationEstimator.MaxSeconds);
    }

    [Fact]
    public void Estimate_UsesLedgerCalibratedOverheadForDetectedCameraMovement()
    {
        // "crane shot" matches the crane/canopy camera regex (2.7s calibrated overhead) — the flat
        // 0.6s "short visual head" guess would badly underestimate a beat that actually describes this.
        var withCrane = ClipDurationEstimator.Estimate(
            dialogue: "I need you to listen to me.",
            visualOrAction: "A slow crane shot descends toward her face.",
            actionClass: "dialogue");

        var plain = ClipDurationEstimator.Estimate(
            dialogue: "I need you to listen to me.",
            visualOrAction: "She looks at him.",
            actionClass: "dialogue");

        Assert.True(withCrane > plain,
            $"expected crane-shot beat ({withCrane}s) to need more time than a plain beat ({plain}s)");
    }

    [Fact]
    public void Estimate_UsesLedgerCalibratedOverheadForDetectedPhysicalAction()
    {
        // "pulls out a switchblade" matches the knife-pull action regex (2.0s calibrated overhead).
        var withKnife = ClipDurationEstimator.Estimate(
            dialogue: "Back off.",
            visualOrAction: "He pulls out a switchblade.",
            actionClass: "dialogue");

        var plain = ClipDurationEstimator.Estimate(
            dialogue: "Back off.",
            visualOrAction: "He stands there.",
            actionClass: "dialogue");

        Assert.True(withKnife > plain,
            $"expected knife-pull beat ({withKnife}s) to need more time than a plain beat ({plain}s)");
    }

    [Fact]
    public void Estimate_NoRecognizableCameraOrActionCueKeepsUnchangedFlatFallback()
    {
        // A beat with no camera-move or physical-action keyword must behave exactly as before the
        // ledger integration — identical to the case with no visual/action text at all.
        var withGenericVisual = ClipDurationEstimator.Estimate(
            dialogue: "Hello there.",
            visualOrAction: "She looks at him.",
            actionClass: "dialogue");

        var withNoVisual = ClipDurationEstimator.Estimate(
            dialogue: "Hello there.",
            visualOrAction: "",
            actionClass: "dialogue");

        Assert.Equal(withNoVisual, withGenericVisual);
    }

    [Fact]
    public void Estimate_HonorsExplicitModelBounds_InsteadOfGlobalDefaults()
    {
        var line =
            "If I could work my will, every idiot who goes about with Merry Christmas on his lips " +
            "should be boiled with his own pudding and buried with a stake of holly through his heart.";

        // Default bounds: clamps up to the global MaxSeconds (10).
        var withDefaults = ClipDurationEstimator.Estimate(line, "Scrooge scowls.", "dialogue");
        Assert.Equal(ClipDurationEstimator.MaxSeconds, withDefaults);

        // A narrower model-specific max must win over the global default.
        var withNarrowModel = ClipDurationEstimator.Estimate(
            line, "Scrooge scowls.", "dialogue", maxSeconds: 6);
        Assert.Equal(6, withNarrowModel);
    }

    [Fact]
    public void ResolveBoundsForModel_UnknownModel_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ClipDurationEstimator.ResolveBoundsForModel("totally-unknown-model-id"));
        Assert.Contains("not in models_catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBoundsForModel_EmptyModel_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ClipDurationEstimator.ResolveBoundsForModel(null));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBoundsForModel_ReturnsCatalogValuesForKnownVideoModel()
    {
        // grok-imagine-video is configured in SupportedModelCatalog with explicit clip-duration
        // bounds — real docs.x.ai fresh text/image-to-video range is 1-15s.
        var (min, max, absMax) = ClipDurationEstimator.ResolveBoundsForModel("grok-imagine-video");
        Assert.Equal(1, min);
        Assert.Equal(15, max);
        Assert.Equal(15, absMax);
    }

    [Fact]
    public void ResolveBoundsForModel_ReturnsNarrowRealRangeForWan()
    {
        // fal-ai/wan-2.1's real usable range is a narrow ~5-6s band (81-100 frames @ 5-24fps),
        // not the generic 3-10s default.
        var (min, max, absMax) = ClipDurationEstimator.ResolveBoundsForModel("fal-ai/wan-2.1");
        Assert.Equal(5, min);
        Assert.Equal(6, max);
        Assert.Equal(6, absMax);
    }

    [Theory]
    [InlineData(1, 4)]  // below the lowest allowed value clamps up to it
    [InlineData(5, 4)]  // exactly between 4 and 6 — ties go to the lower value
    [InlineData(7, 6)]  // exactly between 6 and 8 — ties go to the lower value
    [InlineData(6, 6)]  // exact match passes through unchanged
    [InlineData(9, 8)]  // above the highest allowed value clamps down to it
    public void ResolveActualDurationForModel_SnapsToVeoDiscreteDurations(int requested, int expected)
    {
        // veo-3.1 documents exactly 4/6/8 seconds, not a continuous range — a plain min/max clamp
        // would let e.g. 7 through unchanged, which Veo does not accept.
        var actual = ClipDurationEstimator.ResolveActualDurationForModel("veo-3.1", requested);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(5, 4)]
    [InlineData(7, 6)]
    [InlineData(6, 6)]
    [InlineData(9, 8)]
    public void ResolveActualDurationForModel_SnapsToVeoLiteDiscreteDurations(int requested, int expected)
    {
        // Veo 3.1 Lite copies the family 4/6/8 discrete set; Lite docs do not publish a different set.
        var actual = ClipDurationEstimator.ResolveActualDurationForModel(
            "veo-3.1-lite-generate-preview", requested);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveActualDurationForModel_FallsBackToPlainClampWithoutDiscreteSet()
    {
        // grok-imagine-video has no AllowedDurationsSeconds — behaves like a plain min/max clamp
        // against its real 1-15s range.
        var actual = ClipDurationEstimator.ResolveActualDurationForModel("grok-imagine-video", 25);
        Assert.Equal(15, actual);
    }

    [Fact]
    public void ResolveActualDurationForModel_ExtensionModeAppliesTighterCapForGrok()
    {
        // Fresh generation allows up to 15s, but the "new portion" of a reference/continue call is
        // tighter (maxExtensionSeconds = 10) — isExtensionMode must apply that extra ceiling.
        var fresh = ClipDurationEstimator.ResolveActualDurationForModel("grok-imagine-video", 12);
        var extension = ClipDurationEstimator.ResolveActualDurationForModel(
            "grok-imagine-video", 12, isExtensionMode: true);
        Assert.Equal(12, fresh);
        Assert.Equal(10, extension);
    }

    [Fact]
    public void ResolveActualDurationForModel_ExtensionModeIsNoOpWhenRequestAlreadyUnderCap()
    {
        var actual = ClipDurationEstimator.ResolveActualDurationForModel(
            "grok-imagine-video", 6, isExtensionMode: true);
        Assert.Equal(6, actual);
    }

    [Fact]
    public void Action_only_is_not_padded_to_ten()
    {
        var d = ClipDurationEstimator.Estimate(
            dialogue: "",
            visualOrAction: "Buster runs across the grass.",
            actionClass: "action");
        Assert.InRange(d, ClipDurationEstimator.ActionOnlyMinSeconds, ClipDurationEstimator.SilentActionMaxSeconds);
    }

    [Fact]
    public void Hold_is_minimum_and_establishing_is_capped()
    {
        var hold = ClipDurationEstimator.Estimate(
            dialogue: "",
            visualOrAction: "He steadies his hands on his knees. A thin smile.",
            actionClass: "hold");
        Assert.Equal(ClipDurationEstimator.ActionOnlyMinSeconds, hold);

        var longEstablish =
            "A bare lamplit chamber with close walls and a plain wooden chair faces us. " +
            "The lean man of middle years sits with pale skin dark disordered hair and eyes too bright " +
            "leaning forward as if answering an unseen accuser in period dress with no modern detail " +
            "and many more words of room dressing that must not buy ten seconds of silence.";
        var est = ClipDurationEstimator.Estimate("", longEstablish, "establishing");
        Assert.InRange(est, ClipDurationEstimator.ActionOnlyMinSeconds, ClipDurationEstimator.EstablishingMaxSeconds);
    }

    [Fact]
    public void Allocate_does_not_inflate_dialogue_clips_to_fill_scene_budget()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["dialogue"] = "Hi.",
                ["visual_event"] = "Wave.",
                ["action_class"] = "dialogue",
            },
            new()
            {
                ["dialogue"] = "Bye.",
                ["visual_event"] = "Exit.",
                ["action_class"] = "dialogue",
            },
        };
        var durs = ClipDurationEstimator.AllocateForBeats(beats, sceneTargetSeconds: 40);
        Assert.All(durs, d => Assert.True(d <= 6, $"dialogue clip padded to {d}"));
    }

    [Fact]
    public void Allocate_does_not_stretch_silent_holds_to_scene_budget()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["dialogue"] = "",
                ["visual_event"] = "A bare room. Narrator sits in a chair.",
                ["action_class"] = "establishing",
            },
            new()
            {
                ["dialogue"] = "Hello there friend of mine.",
                ["visual_event"] = "Narrator speaks.",
                ["action_class"] = "dialogue",
                ["delivery"] = "spoken_on_camera",
            },
            new()
            {
                ["dialogue"] = "",
                ["visual_event"] = "He steadies his hands. A thin smile.",
                ["action_class"] = "hold",
            },
        };
        var durs = ClipDurationEstimator.AllocateForBeats(beats, sceneTargetSeconds: 80);
        Assert.True(durs[0] <= ClipDurationEstimator.EstablishingMaxSeconds, $"establish padded to {durs[0]}");
        Assert.Equal(ClipDurationEstimator.ActionOnlyMinSeconds, durs[2]); // hold never padded
    }

    [Theory]
    [InlineData("He steadies his hands on his knees. A thin smile.", false, "hold")]
    [InlineData("A bare, lamplit chamber. Close walls. A plain wooden chair faces us. Narrator sits.", true, "establishing")]
    [InlineData("They chase through the alley and crash into the stalls.", false, "big_action")]
    [InlineData("He crosses the room and opens the heavy oak door to the hall.", false, "action")]
    // Baseline product fallback: first silent → establishing (chat classify overrides at plan time)
    [InlineData("She finishes her cry. She attends to her cheeks with a powder rag and looks out dully.", true, "establishing")]
    [InlineData("They chase through the alley and crash into the stalls.", true, "big_action")]
    public void InferActionClass_tags_silent_beats(string text, bool first, string expected)
    {
        Assert.Equal(expected, FountainStage1Importer.InferActionClass(text, first));
    }

    [Fact]
    public void EstimateForClip_reads_audio_payload()
    {
        var clip = JsonDocument.Parse("""
            {
              "duration_seconds": 10,
              "visual_prompt": "Momma points to the door.",
              "audio_payload": {
                "speaker": "Character_Momma",
                "dialogue": "A doggy goes outside.",
                "delivery": "on_camera"
              }
            }
            """).RootElement;
        var d = ClipDurationEstimator.EstimateForClip(clip);
        Assert.True(d < 10, "should not keep inflated plan for short dialogue");
        Assert.InRange(d, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds);
    }

    [Fact]
    public void EstimateForClip_does_not_charge_scene_recap_action_to_spoken_hold()
    {
        // Production visual_prompt text is enriched with the whole scene recap. The old estimator
        // treated "heavy bed" below as a heavy-carry action performed during this quiet VO hold.
        var enriched = JsonDocument.Parse("""
            {
              "action_class": "hold",
              "visual_prompt": "Earlier, he pulled the heavy bed across the room. Camera directive: close on his listening face.",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "The heart beat on.",
                "delivery": "voiceover_internal"
              }
            }
            """).RootElement;
        var clean = JsonDocument.Parse("""
            {
              "action_class": "hold",
              "visual_prompt": "",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "The heart beat on.",
                "delivery": "voiceover_internal"
              }
            }
            """).RootElement;

        Assert.Equal(
            ClipDurationEstimator.EstimateForClip(clean),
            ClipDurationEstimator.EstimateForClip(enriched));
    }

    [Fact]
    public void EstimateForClip_keeps_action_overhead_for_action_bearing_spoken_beat()
    {
        var action = JsonDocument.Parse("""
            {
              "action_class": "action",
              "visual_prompt": "He pulls the heavy bed across the room, then speaks.",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "Move back.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var hold = JsonDocument.Parse("""
            {
              "action_class": "hold",
              "visual_prompt": "He pulls the heavy bed across the room, then speaks.",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "Move back.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        Assert.True(
            ClipDurationEstimator.EstimateForClip(action) > ClipDurationEstimator.EstimateForClip(hold));
    }

    [Fact]
    public void EstimateForClip_honors_planned_silent_big_action_beyond_default_5s()
    {
        // Stage 2 writes duration_seconds + action_class; gen must not clamp all silent to 5s.
        var clip = JsonDocument.Parse(
            """
            {
              "duration_seconds": 9,
              "action_class": "big_action",
              "visual_prompt": "The chase crashes through the hall and down the stairs."
            }
            """).RootElement;
        var d = ClipDurationEstimator.EstimateForClip(clip);
        Assert.Equal(9, d);
        Assert.True(
            d > ClipDurationEstimator.SilentActionMaxSeconds,
            "big_action planned length must exceed flat silent cap");
    }

    [Fact]
    public void EstimateForClip_silent_without_class_still_caps_at_5s()
    {
        var clip = JsonDocument.Parse(
            """
            {
              "duration_seconds": 9,
              "visual_prompt": "A quiet hold on the empty doorway."
            }
            """).RootElement;
        var d = ClipDurationEstimator.EstimateForClip(clip);
        Assert.Equal(ClipDurationEstimator.SilentActionMaxSeconds, d);
    }

    [Fact]
    public void EstimateForClip_does_not_under_run_speech_when_plan_is_short()
    {
        // Plan said 5s; line needs more with head+tail so first word is not clipped
        const string line = "True! Nervous - very, very dreadfully nervous I had been and am;";
        var est = ClipDurationEstimator.Estimate(line, "Narrator speaks.", "dialogue", "spoken_on_camera");
        Assert.True(est >= 6, $"expected >=6s for confession open, got {est}");

        var clip = JsonDocument.Parse(
            """
            {
              "duration_seconds": 5,
              "visual_prompt": "Narrator speaks.",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "True! Nervous - very, very dreadfully nervous I had been and am;",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var d = ClipDurationEstimator.EstimateForClip(clip);
        Assert.Equal(est, d);
        Assert.True(d > 5, "must not lock to under-planned 5s");
    }

    [Fact]
    public void Short_dialogue_is_not_split()
    {
        var line = "Well enough. Well enough.";
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(line);
        Assert.Single(parts);
        Assert.Equal(line, parts[0]);
    }

    [Fact]
    public void Long_monologue_splits_into_multiple_model_safe_chunks()
    {
        // Poe-scale first confession speech (~80+ words) must not stay one 10s clip
        var line =
            "True!—nervous—very, very dreadfully nervous I had been and am; but why will you say that I am mad? " +
            "The disease had sharpened my senses—not destroyed—not dulled them. Above all was the sense of hearing acute. " +
            "I heard all things in the heaven and in the earth. I heard many things in hell. How, then, am I mad? " +
            "Hearken! and observe how healthily—how calmly I can tell you the whole story.";

        Assert.True(ClipDurationEstimator.DialogueExceedsModelMax(line));
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(line);
        Assert.True(parts.Count >= 2, $"expected split, got {parts.Count} part(s)");
        Assert.Equal(
            ClipDurationEstimator.CountWords(line),
            parts.Sum(ClipDurationEstimator.CountWords));

        foreach (var p in parts)
        {
            var uncapped = ClipDurationEstimator.EstimateUncapped(p, "", "dialogue", "spoken_on_camera");
            var budget = ClipDurationEstimator.MaxSeconds - ClipDurationEstimator.DialogueModelPaddingSeconds;
            Assert.True(uncapped <= budget + 0.5,
                $"chunk still too long ({uncapped:F1}s > budget {budget:F1}s): {p[..Math.Min(60, p.Length)]}…");
            var planned = ClipDurationEstimator.Estimate(p, "speaks", "dialogue");
            Assert.InRange(planned, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds);
        }
    }

    [Fact]
    public void SplitDialogueToFitModelMax_does_not_insert_spaces_around_em_dashes()
    {
        // Real Tell-Tale Heart lines observed to need word-level packing (SegmentDialogueUnits
        // alone wasn't enough) — the dash sits directly against both words in the source with no
        // surrounding space, matching this era's typographic convention.
        var line =
            "And this I did for seven long nights—every night just at midnight—but I found the eye " +
            "always closed; and so it was impossible to do the work; for it was not the old man who vexed me, but his Evil Eye.";

        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(line);
        var rejoined = string.Join(" ", parts);
        Assert.Contains("nights—every", rejoined);
        Assert.Contains("midnight—but", rejoined);
        Assert.DoesNotContain(" — ", rejoined);
    }

    [Fact]
    public void ExpandLongDialogueBeats_splits_and_preserves_stable_root()
    {
        var monologue =
            "It is impossible to say how first the idea entered my brain; but once conceived, it haunted me day and night. " +
            "Object there was none. Passion there was none. I loved the old man. He had never wronged me. " +
            "He had never given me insult. For his gold I had no desire. I think it was his eye! yes, it was this!";

        var root = PageToMovie.Core.Utils.StableBeatId.ForContent(
            "INT. ROOM - NIGHT", "dialogue", "Character_Narrator", monologue);

        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = PageToMovie.Core.Utils.StableBeatId.ForContent(
                    "INT. ROOM - NIGHT", "action", "", "Chair faces us."),
                ["action_class"] = "action",
                ["dialogue"] = "",
                ["delivery"] = "none",
                ["visual_event"] = "Chair faces us.",
            },
            new()
            {
                ["beat_id"] = root,
                ["action_class"] = "dialogue",
                ["dialogue"] = monologue,
                ["delivery"] = "spoken_on_camera",
                ["speaker"] = "Character_Narrator",
                ["visual_event"] = "NARRATOR speaks.",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["delivery"] = "spoken_on_camera",
                    ["speaker"] = "Character_Narrator",
                    ["dialogue"] = monologue,
                },
            },
        };

        var expanded = ClipDurationEstimator.ExpandLongDialogueBeats(beats);
        Assert.True(expanded.Count > 2, $"expected monologue expansion, count={expanded.Count}");
        Assert.Equal(beats[0]["beat_id"]?.ToString(), expanded[0]["beat_id"]?.ToString());
        Assert.Equal("", expanded[0]["dialogue"]?.ToString() ?? "");

        var speech = expanded.Skip(1).ToList();
        Assert.All(speech, b =>
        {
            Assert.Equal("Character_Narrator", b["speaker"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(b["dialogue"]?.ToString()));
            Assert.False(ClipDurationEstimator.DialogueExceedsModelMax(b["dialogue"]?.ToString()));
            var id = b["beat_id"]?.ToString() ?? "";
            Assert.StartsWith(root + "#p", id, StringComparison.Ordinal);
            Assert.Contains("of", id, StringComparison.Ordinal);
        });
        Assert.Equal(root + "#p1of" + speech.Count, speech[0]["beat_id"]?.ToString());
    }

    [Fact]
    public void Run_on_sentence_without_punctuation_still_packs_by_words()
    {
        var words = string.Join(" ", Enumerable.Repeat("madness", 60));
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(words);
        Assert.True(parts.Count >= 2);
        foreach (var p in parts)
            Assert.False(ClipDurationEstimator.DialogueExceedsModelMax(p));
    }
}

public class ClipSilenceTrimmerTests
{
    [Fact]
    public void ComputeCutPoint_trims_trailing_silence()
    {
        // Speech until 5.0s, then silence to 8.0s
        var log = """
            [silencedetect @ 0x] silence_start: 5.0
            """;
        var cut = ClipSilenceTrimmer.ComputeCutPoint(log, totalDuration: 8.0, keepTailSeconds: 0.35);
        Assert.NotNull(cut);
        Assert.InRange(cut!.Value, 5.2, 5.6);
    }

    [Fact]
    public void ComputeCutPoint_skips_when_no_trailing_silence()
    {
        var log = """
            [silencedetect @ 0x] silence_start: 1.0
            [silencedetect @ 0x] silence_end: 1.5
            """;
        // speech resumes and continues to end — no open trailing silence
        var cut = ClipSilenceTrimmer.ComputeCutPoint(log, totalDuration: 6.0, keepTailSeconds: 0.35);
        Assert.Null(cut);
    }

    private const string ChildrenLine = "Why does the lamb love Mary so?";
    private const string TeacherLine = "Oh, Mary loves the lamb, you know.";

    private static JsonElement TwoSpeakerClip => JsonDocument.Parse($$"""
        {
          "visual_prompt": "Two children face the teacher.",
          "audio_payload": {
            "speaker": "Character_Children",
            "dialogue": "{{ChildrenLine}}",
            "secondary_speaker": "Character_Teacher",
            "secondary_dialogue": "{{TeacherLine}}",
            "delivery": "spoken_on_camera"
          }
        }
        """).RootElement;

    private static JsonElement SingleSpeakerClip(string speaker, string line) =>
        JsonDocument.Parse($$"""
        {
          "visual_prompt": "Two children face the teacher.",
          "audio_payload": {
            "speaker": "{{speaker}}",
            "dialogue": "{{line}}",
            "delivery": "spoken_on_camera"
          }
        }
        """).RootElement;

    [Fact]
    public void EstimateForClip_two_hander_is_sized_for_both_lines()
    {
        // Regression: a two-hander clip (Mary's Teacher answering the children) used to be sized
        // for the primary line only, so the teacher's line was cut. It must now cover BOTH.
        var primaryOnly = ClipDurationEstimator.EstimateForClip(
            SingleSpeakerClip("Character_Children", ChildrenLine));
        var secondaryOnly = ClipDurationEstimator.EstimateForClip(
            SingleSpeakerClip("Character_Teacher", TeacherLine));
        var both = ClipDurationEstimator.EstimateForClip(TwoSpeakerClip);

        Assert.True(both > primaryOnly,
            $"two-hander ({both}s) must be strictly longer than the primary-only estimate ({primaryOnly}s)");
        Assert.True(both >= primaryOnly && both >= secondaryOnly,
            $"two-hander ({both}s) must be at least as long as each line ({primaryOnly}s / {secondaryOnly}s)");
        Assert.InRange(both, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds);
    }

    [Fact]
    public void ClipSpokenLines_returns_every_line_in_order_and_skips_silent()
    {
        // Primary only → exactly one line.
        var single = ClipSpokenLines.FromClipElement(
            SingleSpeakerClip("Character_Children", ChildrenLine));
        Assert.Single(single);
        Assert.Equal("Character_Children", single[0].Speaker);
        Assert.Equal(ChildrenLine, single[0].Dialogue);

        // Two-hander → two lines, primary first then secondary.
        var pair = ClipSpokenLines.FromClipElement(TwoSpeakerClip);
        Assert.Equal(2, pair.Count);
        Assert.Equal("Character_Children", pair[0].Speaker);
        Assert.Equal(ChildrenLine, pair[0].Dialogue);
        Assert.Equal("Character_Teacher", pair[1].Speaker);
        Assert.Equal(TeacherLine, pair[1].Dialogue);

        // delivery:"none" is the authoritative silent marker → zero spoken lines, even with text.
        var silent = JsonDocument.Parse("""
            { "audio_payload": { "speaker": "Character_X", "dialogue": "unsaid", "delivery": "none" } }
            """).RootElement;
        Assert.Empty(ClipSpokenLines.FromClipElement(silent));
    }
}
