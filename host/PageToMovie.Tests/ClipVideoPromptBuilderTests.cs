using System.Text.Json;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

public class ClipVideoPromptBuilderTests
{
    [Fact]
    public void Build_UsesPronunciationHintFromAudioPayload()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "The narrator speaks.",
              "characters_on_screen": ["Character_The_Narrator"],
              "audio_payload": {
                "speaker": "Character_The_Narrator",
                "dialogue": "Wind the clock!",
                "delivery": "spoken_on_camera",
                "pronunciation_hint": "Pronounce 'wind' as /waɪnd/ (turn or coil)"
              }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(clip, "proj", new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());
        Assert.Contains("<Pronunciation>Pronounce 'wind' as /waɪnd/ (turn or coil)</Pronunciation>", built.Prompt);
    }

    [Fact]
    public void Build_DropsPronunciationHintWhenTargetWordNotInDialogue()
    {
        // The hint targets 'wind', but the line never says it — the hint is noise and must be dropped.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "The narrator speaks.",
              "characters_on_screen": ["Character_The_Narrator"],
              "audio_payload": {
                "speaker": "Character_The_Narrator",
                "dialogue": "Hello there, friend!",
                "delivery": "spoken_on_camera",
                "pronunciation_hint": "Pronounce 'wind' as /waɪnd/ (turn or coil)"
              }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(clip, "proj", new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());
        Assert.DoesNotContain("Pronounce 'wind'", built.Prompt);
    }

    [Fact]
    public void Build_TwoSpeakerBeat_EmitsBothLipSyncLinesAndAllowsSecondMouth()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Nick and Character_Sionna talk on the porch.",
              "characters_on_screen": ["Character_Nick", "Character_Sionna"],
              "audio_payload": {
                "speaker": "Character_Nick",
                "dialogue": "You coming or not?",
                "secondary_speaker": "Character_Sionna",
                "secondary_dialogue": "Give me a second.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(clip, "proj", new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());

        Assert.Contains("Character_Nick ON CAMERA lip-syncs exactly: \"You coming or not?\"", built.Prompt);
        Assert.Contains("Then Character_Sionna ON CAMERA lip-syncs exactly: \"Give me a second.\"", built.Prompt);
        Assert.DoesNotContain("Other mouths closed", built.Prompt);
    }

    [Fact]
    public void Build_SingleSpeakerBeat_StillKeepsOtherMouthsClosed()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Nick speaks on the porch.",
              "characters_on_screen": ["Character_Nick"],
              "audio_payload": {
                "speaker": "Character_Nick",
                "dialogue": "You coming or not?",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(clip, "proj", new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());

        Assert.Contains("Other mouths closed", built.Prompt);
    }

    [Fact]
    public void Build_includes_character_variables_and_image_tags()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Buster runs across the grass. Character_Momma watches.",
              "characters_on_screen": ["Character_Buster", "Character_Momma"],
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "He's Buster the Noodle Head Dog.",
                "delivery": "voiceover_internal"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-prompt-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_buster_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_momma_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Buster"] = new()
            {
                Key = "Character_Buster",
                DisplayName = "Buster",
                Description = "Small black-and-white dog",
                VisualLock = "Always black-and-white patches",
                VoiceProfile = "nonverbal",
            },
            ["Character_Momma"] = new()
            {
                Key = "Character_Momma",
                DisplayName = "Momma",
                Description = "Adult woman, warm",
                VisualLock = "Same mother figure",
                VoiceProfile = "warm mid pitch",
            },
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                VoiceOnly = true,
                VoiceProfile = "calm storyteller",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("<Characters", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Buster", built.Prompt);
        // Refs are attached here, so the identity prose stands down to the pictures — the point of
        // this case is the tags and the audio block. Prose-vs-picture is InventedLookYieldsToImageTests.
        Assert.DoesNotContain("Small black-and-white dog", built.Prompt);
        Assert.Contains("<IMAGE_1>", built.Prompt);
        Assert.Contains("<VoiceLock>", built.Prompt);
        Assert.Contains("He's Buster the Noodle Head Dog.", built.Prompt);
        Assert.True(built.ReferenceImagePaths.Count >= 1);
        Assert.Null(built.StartFrameImagePath);
        Assert.True(built.Prompt.Length < ClipVideoPromptBuilder.MaxPromptChars);
        Assert.True(built.Prompt.Length > 200);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_silent_clip_tells_model_not_to_show_speaking()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "duration_seconds": 8,
              "visual_prompt": "Character_OldMan sleeps. Character_Narrator watches from the shadows.",
              "characters_on_screen": ["Character_OldMan", "Character_Narrator"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_OldMan"] = new()
            {
                Key = "Character_OldMan",
                DisplayName = "Old Man",
                Description = "frail elderly man",
                VoiceProfile = "raspy whisper",
            },
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                VoiceOnly = true,
                VoiceProfile = "calm storyteller",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);

        AssertFillNPacing(built.Prompt, 8);
        Assert.Contains("Silent beat", built.Prompt);
        Assert.Contains("do not show any on-screen character", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the spoken line", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("half a second", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_dialogue_clip_fills_planned_duration_audio_owns_end_pause()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "duration_seconds": 11,
              "visual_prompt": "Character_OldMan speaks.",
              "characters_on_screen": ["Character_OldMan"],
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_OldMan",
                "dialogue": "Come closer.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-dialogue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_OldMan"] = new()
            {
                Key = "Character_OldMan",
                DisplayName = "Old Man",
                Description = "frail elderly man",
                VoiceProfile = "raspy whisper",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);

        AssertFillNPacing(built.Prompt, 11);
        Assert.DoesNotContain("Silent beat", built.Prompt);
        AssertAudioOwnsClosedMouthPause(built.Prompt);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_extend_dialogue_uses_same_fill_n_pacing_as_fresh()
    {
        var clipJson = """
            {
              "clip_number": 2,
              "duration_seconds": 9,
              "visual_prompt": "Character_OldMan speaks.",
              "characters_on_screen": ["Character_OldMan"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": {
                "speaker": "Character_OldMan",
                "dialogue": "Come closer.",
                "delivery": "spoken_on_camera"
              }
            }
            """;
        var clip = JsonDocument.Parse(clipJson).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_OldMan"] = new()
            {
                Key = "Character_OldMan",
                DisplayName = "Old Man",
                Description = "frail elderly man",
                VoiceProfile = "raspy whisper",
            },
        };

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-pacing-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var prevVideo = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prevVideo, new byte[2048]);

        var fresh = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 3);
        var extend = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            profiles,
            previousClipVisualPrompt: "Character_OldMan stands in the doorway.",
            previousClipVideoPath: prevVideo,
            maxRefs: 3);

        Assert.Equal("video-extend", extend.Mode);
        AssertFillNPacing(fresh.Prompt, 9);
        AssertFillNPacing(extend.Prompt, 9);
        AssertAudioOwnsClosedMouthPause(fresh.Prompt);
        AssertAudioOwnsClosedMouthPause(extend.Prompt);
        Assert.Equal(1, CountOccurrences(fresh.Prompt, "This is a 9-second shot"));
        Assert.Equal(1, CountOccurrences(extend.Prompt, "This is a 9-second shot"));

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_live_prompt_has_no_tight_action_house_line()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "duration_seconds": 6,
              "visual_prompt": "Character_OldMan walks across the room.",
              "characters_on_screen": ["Character_OldMan"],
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(
            clip,
            Path.GetTempPath(),
            new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_OldMan"] = new()
                {
                    Key = "Character_OldMan",
                    DisplayName = "Old Man",
                    Description = "frail elderly man",
                },
            });

        AssertFillNPacing(built.Prompt, 6);
    }

    [Fact]
    public void Build_video_extend_mode_when_previous_clip_file_exists()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "Character_Buster skids and tumbles.",
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-cont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var prevVideo = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prevVideo, new byte[2048]);

        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            previousClipVisualPrompt: "Character_Buster rockets across the grass.",
            previousClipVideoPath: prevVideo,
            maxRefs: 3);

        Assert.Equal("video-extend", built.Mode);
        Assert.Contains("EXTENSION", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // The previous clip is attached as VIDEO. Re-describing it in prose is what made the model
        // replay it (Mary19 S02C02 restaged S02C01's door entrance), so it must not be here.
        // The tag, not the words — <Continuity> prose legitimately says "the previous clip".
        Assert.DoesNotContain("<PreviousClip", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("rockets across the grass", built.Prompt, StringComparison.Ordinal);
        Assert.Empty(built.ReferenceImagePaths);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Video_prompt_hard_cap_matches_xai_limit()
    {
        Assert.Equal(4000, ClipVideoPromptBuilder.VideoPromptHardCapChars);
        Assert.Equal(
            ClipVideoPromptBuilder.VideoPromptHardCapChars,
            ClipVideoPromptBuilder.MaxPromptChars);
    }

    [Fact]
    public void Build_unknown_video_model_fails_instead_of_using_catalog_default()
    {
        var clip = JsonDocument.Parse("""{"visual_prompt":"A quiet room."}""").RootElement;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ClipVideoPromptBuilder.Build(clip, "proj", videoModel: "totally-unknown-video-model"));
        Assert.Contains("video", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("totally-unknown-video-model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog default is not applied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_includes_cast_count_for_on_screen_keys()
    {
        var json = """
            {
              "visual_prompt": "INT. ROOM - DAY. Character_Hero and Character_Villain face off.",
              "characters_on_screen": ["Character_Hero", "Character_Villain"],
              "primary_subject": "Character_Hero",
              "audio_payload": { "speaker": "Character_Hero", "dialogue": "Stop.", "delivery": "spoken_on_camera" }
            }
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var built = ClipVideoPromptBuilder.Build(
            doc.RootElement,
            projectDir: Path.GetTempPath(),
            characters: new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Hero"] = new() { Key = "Character_Hero", DisplayName = "Hero", Description = "tall hero" },
                ["Character_Villain"] = new() { Key = "Character_Villain", DisplayName = "Villain", Description = "scarred villain" },
            });
        Assert.Contains("<CastCount>exactly 2", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", built.Prompt);
        Assert.Contains("Character_Villain", built.Prompt);
    }

    [Theory]
    [InlineData("Grok submit HTTP 400: prompt too long", true)]
    [InlineData("context_length_exceeded", true)]
    [InlineData("maximum context length exceeded", true)]
    [InlineData("HTTP 413 payload too large", true)]
    [InlineData("Grok job failed: bad face", false)]
    [InlineData("rate limit", false)]
    public void IsPromptTooLongError_detects_length_failures(string msg, bool expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.IsPromptTooLongError(msg));
    }

    [Fact]
    public void ShortenPromptForRetry_strips_house_rules_then_caps()
    {
        var core = "CHARACTER VARIABLES\n- Character_Hero: pale man in wool coat\n\nTHIS CLIP:\nAction beats go here.\n";
        var rules = "\nHOUSE RULES:\n- rule one\n";
        var full = core + rules + "\nPROJECT HOUSE RULES (approved):\n- period drama\n";

        var s1 = ClipVideoPromptBuilder.ShortenPromptForRetry(full, 1);
        Assert.DoesNotContain("HOUSE RULES:", s1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PROJECT HOUSE RULES", s1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", s1);
        Assert.True(s1.Length < full.Length);

        var huge = new string('x', 12_000) + "\n" + full;
        var s2 = ClipVideoPromptBuilder.ShortenPromptForRetry(huge, 2);
        Assert.True(s2.Length <= ClipVideoPromptBuilder.VideoPromptHardCapChars);
    }

    [Fact]
    public void CompressPromptText_maps_character_keys_and_image_tags_to_compact_aliases()
    {
        var input = "<Characters note=\"use these identities consistently; do not redesign faces\">\n" +
                    " Character_The_Narrator <IMAGE_1>: Lean man of middle years. <VisualLock>dark coat</VisualLock>.\n" +
                    " Character_Old_Man <IMAGE_2>: Elderly man with pale blue eye.\n" +
                    "<Clip>\n" +
                    "<Camera>Medium shot</Camera>. <Grade>Dark</Grade>. Character_The_Narrator ON CAMERA lip-syncs to Character_Old_Man <IMAGE_2>.";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);

        // Notes survive mechanical compression now — FitPromptToVideoBudget drops them only when
        // squeezing is not enough, so a block's one line of explanation outlives redundant prose.
        Assert.Contains("<Characters note=", compressed);
        Assert.DoesNotContain("Character_The_Narrator", compressed);
        Assert.DoesNotContain("Character_Old_Man", compressed);
        Assert.DoesNotContain("<IMAGE_1>", compressed);
        Assert.DoesNotContain("<IMAGE_2>", compressed);
        Assert.Contains("C1 I1", compressed);
        Assert.Contains("C2 I2", compressed);
        Assert.Contains("<Camera>Medium shot</Camera>", compressed);
        // Every field is a tag now, so there are no prose labels left for compression to rename.
        Assert.Contains("<Grade>Dark</Grade>", compressed);
        Assert.Contains("<VisualLock>dark coat</VisualLock>", compressed);
        Assert.Contains("C1 lip-syncs to C2 I2.", compressed);
    }

    [Fact]
    public void CompressPromptText_reduces_tell_tale_heart_prompt_significantly()
    {
        var original = "STYLE LOCK: Period gothic live-action, mid-19th-century interiors; candlelight and deep shadows; desaturated cool-gray palette; naturalistic skin and fabric texture; no illustration or stylized animation\n" +
                       "<Characters note=\"use these identities consistently; do not redesign faces\">\n" +
                       " Character_The_Narrator <IMAGE_1> [The Narrator]: Lean man of middle years (about 40–50), pale sallow skin, hollow cheeks, dark disordered medium-length hair, bright intense dark eyes, thin tense mouth; plain dark wool waistcoat over white shirtsleeves, dark trousers; period clothing. <VisualLock>Same lean pale face, dark disordered hair, and bright intense dark eyes in every scene; always plain dark waistcoat and shirtsleeves as default; never elderly, never white-haired, never the filmed blue eye.</VisualLock> <Voice>Male, middle years; medium-high tense pitch; precise, controlled pace that sharpens into fevered urgency; intimate confessional energy, same voice on-camera and in V.O.</Voice> Match appearance of reference <IMAGE_1> exactly.\n" +
                       "<CastCount>exactly 1 distinct on-screen character identity(ies) only — Character_The_Narrator. Do not invent extra people, duplicate faces, or crowd extras not listed.</CastCount>\n" +
                       "<Audio>REQUIRED native Grok dialogue. Character_The_Narrator ON CAMERA lip-syncs EXACTLY: \"Passion there was none. I loved the old man. He had never wronged me.\". Start speaking immediately with \"Passion\" — do not skip, delay, or swallow the opening word. After the last word, hold a brief natural pause with a closed mouth (about half a second); do not freeze mid-syllable or trail into empty staring. Other mouths closed. Speech intelligible; never silent. <Score>Melancholy warm strings undercut by unease</Score> <VoiceLock>Character_The_Narrator: Male, middle years; medium-high tense pitch; precise, controlled pace that sharpens into fevered urgency; intimate confessional energy, same voice on-camera and in V.O.</VoiceLock></Audio>\n" +
                       "<Context note=\"prior clip in scene — new cast plate refs attached; match location/lighting if still valid; identity from Characters + locked plates only\">\n" +
                       "INT. BARE ROOM - NIGHT. The Narrator speaks. Character_The_Narrator ON CAMERA lip-syncs \"but once conceived, it haunted me day and night. Object there was none.\". Character_The_Narrator still wears plain dark waistcoat, rolled cuffs <Camera>Steady close-up, 50mm lens, face half-shadowed while haunted obsession is described</Camera> <Performance>Acting intensity 6/10: Haunted stare, jaw clench, restless micro-twitch under eye</Performance> <Optics>f/1.8 shallow depth of field, intimate facial isolation</Optics> Color grading: Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights\n" +
                       "Follow the camera framing and location in this prompt exactly. Prioritize the PRIMARY subject and ONE clear action with visible motion; background characters may stay mostly still.\n" +
                       "<Clip>\n" +
                       "End cleanly when the spoken line and primary action finish — do not hold a frozen pose or empty silence after dialogue.\n" +
                       "INT. BARE ROOM - NIGHT. The Narrator speaks. Character_The_Narrator <IMAGE_1> ON CAMERA lip-syncs \"Passion there was none. I loved the old man. He had never wronged me.\". Character_The_Narrator <IMAGE_1> still wears plain dark waistcoat, white shirtsleeves, rolled cuffs <Camera>Medium shot drifting slightly, 35mm lens, calm delivery of love for the old man</Camera> <Performance>Acting intensity 4/10: Softened sincere eyes, gentle brow raise, open earnest expression</Performance> <Optics>f/2.0 shallow depth of field, soft background separation</Optics> Color grading: Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights / 480p, 24fps";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(original);

        // Compression alone cuts roughly a third. The hard contract is the budget ladder below:
        // whatever compression leaves, FitPromptToVideoBudget still gets under the cap.
        Assert.True(compressed.Length < original.Length * 3 / 4,
            $"Compressed {compressed.Length} vs Original {original.Length}");
        Assert.True(ClipVideoPromptBuilder.FitPromptToVideoBudget(original, 2500).Length <= 2500);
        Assert.DoesNotContain("Character_The_Narrator", compressed);
        Assert.DoesNotContain("<IMAGE_1>", compressed);
        Assert.DoesNotContain("/ 480p, 24fps", compressed);
        // No film-stock rewrite is asserted: that rule rewrote free prose from the grading
        // classifier rather than text this class builds, so it missed every stock but one.
        Assert.Contains("C1 I1", compressed);
        // Regression: this instruction used to be deleted outright rather than shortened, leaving
        // the focus character's reference image attached (I1) with no instruction to match it.
        Assert.Contains("Match I1 exactly.", compressed);
        // Per-character <Voice> descriptions are dropped; the SPEAKER's <VoiceLock> survives (shortened):
        // Grok Imagine generates the speech and this is the only cross-clip voice identity.
        Assert.DoesNotContain("<Voice>", compressed);
        Assert.Contains("<VoiceLock>C1: Male, middle years", compressed);
    }

    [Fact]
    public void CompressPromptText_voice_tag_strip_does_not_eat_dialogue_mentioning_voice()
    {
        // Regression: the old bare "Voice:" label match risked eating part of a dialogue line if
        // it ever happened to contain that literal substring. <Voice>/<VoiceLock> tags are
        // unambiguous — text outside the tags survives even if it says "voice:" verbatim.
        var input = "Character_Narrator <IMAGE_1>: Lean pale man. <Voice>Calm, measured tone.</Voice>\n" +
                    "AUDIO: Character_Narrator ON CAMERA lip-syncs EXACTLY: \"I heard a voice: faint and pleading, calling my name.\".";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);

        Assert.DoesNotContain("<Voice>", compressed);
        Assert.DoesNotContain("Calm, measured tone", compressed);
        Assert.Contains("I heard a voice: faint and pleading, calling my name.", compressed);
    }

    [Fact]
    public void CompressPromptText_alias_substitution_does_not_corrupt_prefix_keys()
    {
        // Regression: plain string Replace() in first-appearance order corrupted a key that is a
        // prefix of another (e.g. Character_Mom vs Character_Mom_Assistant) — replacing the
        // shorter one first mangled the longer one's occurrences into "C1_Assistant" before it
        // ever got its own alias, silently breaking that character's identity references.
        var input = "CHARACTER VARIABLES:\n" +
                    " Character_Mom <IMAGE_1>: Middle-aged woman.\n" +
                    " Character_Mom_Assistant <IMAGE_2>: Younger woman, Mom's assistant.\n" +
                    "THIS CLIP:\n" +
                    "Character_Mom ON CAMERA lip-syncs to Character_Mom_Assistant <IMAGE_2>.";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);

        Assert.DoesNotContain("Character_Mom", compressed);
        Assert.DoesNotContain("_Assistant", compressed);
        // Both characters must end up with their own clean, distinct alias.
        var aliases = CommonRegex.Matches(compressed, @"\bC\d+\b")
            .Select(m => m.Value).Distinct().ToList();
        Assert.Equal(2, aliases.Count);
    }

    [Fact]
    public void CompressPromptText_aliases_leftover_display_names_to_the_same_C_index()
    {
        var input =
            "<Characters>Character_Mary <Name>Mary</Name>: school-age girl. Character_The_Lamb: tiny white lamb.</Characters>\n" +
            "<CastCount>exactly 2 — Character_Mary, Character_The_Lamb.</CastCount>\n" +
            "<Action>Mary walks. THE LAMB follows. Character_Mary still wears a pale pinafore.</Action>";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);

        Assert.DoesNotContain("Character_Mary", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Character_The_Lamb", compressed, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<![A-Za-z_])Mary(?![A-Za-z_])", compressed);
        Assert.DoesNotMatch(@"(?<![A-Za-z_])THE LAMB(?![A-Za-z_])", compressed);
        Assert.DoesNotMatch(@"(?<![A-Za-z_])The Lamb(?![A-Za-z_])", compressed);
        Assert.Contains("C1 walks", compressed, StringComparison.Ordinal);
        Assert.Contains("C2 follows", compressed, StringComparison.Ordinal);
        Assert.Contains("C1 still wears", compressed, StringComparison.Ordinal);
    }

    [Fact]
    public void CompressPromptText_does_not_rewrite_quoted_display_names()
    {
        var input = "Character_Mary ON CAMERA lip-syncs EXACTLY: \"Mary had a little lamb.\".";
        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);
        Assert.Contains("C1 lip-syncs", compressed, StringComparison.Ordinal);
        Assert.Contains("\"Mary had a little lamb.\"", compressed, StringComparison.Ordinal);
    }

    [Fact]
    public void FitPromptToVideoBudget_under_cap_keeps_Character_keys_and_never_emits_C_index()
    {
        var prompt =
            "<Characters>Character_Mary: school-age girl. Character_The_Lamb: tiny white lamb.</Characters>\n" +
            "<CastCount>exactly 2 — Character_Mary, Character_The_Lamb.</CastCount>\n" +
            "<Identity>On-screen: Character_Mary, Character_The_Lamb.</Identity>\n" +
            "<VoiceLock>Character_Mary: bright child.</VoiceLock>\n" +
            "<Action>Character_Mary walks. Character_The_Lamb follows.</Action>";

        Assert.True(prompt.Length < ClipVideoPromptBuilder.VideoPromptHardCapChars);
        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(prompt);
        Assert.Equal(prompt, fitted);
        Assert.DoesNotMatch(@"\bC\d+\b", fitted);
        Assert.Contains("Character_Mary", fitted, StringComparison.Ordinal);
        Assert.Contains("Character_The_Lamb", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_on_screen_set_does_not_leave_stale_C3_in_fresh_or_extend()
    {
        var prevThree =
            "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
            "<Cast>Character_Mary, Character_The_Lamb, Character_Teacher</Cast> " +
            "<Action>also on screen: C3, C2. Character_Teacher watches.</Action> " +
            "<Lighting>Soft warm daylight.</Lighting>";
        var look = ClipVideoPromptBuilder.PreviousClipLookOnly(
            prevThree, new[] { "Character_Mary", "Character_The_Lamb" });
        Assert.DoesNotContain("C3", look, StringComparison.Ordinal);
        Assert.DoesNotContain("also on screen", look, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Teacher", look, StringComparison.Ordinal);

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 3,
              "visual_prompt": "<Setting>INT. SCHOOLROOM - DAY</Setting> <Cast>Character_Mary, Character_The_Lamb</Cast> <Action>Mary and The Lamb wait by the door.</Action>",
              "characters_on_screen": ["Character_Mary", "Character_The_Lamb"],
              "audio_payload": { "speaker": "Character_Narrator", "delivery": "voiceover_internal", "dialogue": "And so the teacher turned him out." }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new() { Key = "Character_Mary", DisplayName = "Mary", Description = "girl" },
            ["Character_The_Lamb"] = new() { Key = "Character_The_Lamb", DisplayName = "The Lamb", Description = "lamb" },
            ["Character_Teacher"] = new() { Key = "Character_Teacher", DisplayName = "Teacher", Description = "adult" },
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator", Description = "voice", VoiceOnly = true },
        };

        var fresh = ClipVideoPromptBuilder.Build(
            clip, Path.GetTempPath(), profiles, previousClipVisualPrompt: prevThree);
        Assert.Equal("fresh", fresh.Mode);
        Assert.DoesNotMatch(@"\bC\d+\b", fresh.Prompt);
        Assert.DoesNotContain("C3", fresh.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_Mary", fresh.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_The_Lamb", fresh.Prompt, StringComparison.Ordinal);

        var extend = ClipVideoPromptBuilder.Build(
            clip, Path.GetTempPath(), profiles, previousClipExtendFileId: "file_prev");
        Assert.Equal("video-extend", extend.Mode);
        Assert.DoesNotMatch(@"\bC\d+\b", extend.Prompt);
        Assert.DoesNotContain("C3", extend.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("also on screen: C3", extend.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_Mary", extend.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FitPromptToVideoBudget_strips_house_rules_before_first_send()
    {
        var core = "CHARACTER VARIABLES\n- Character_Hero: pale man\n\nTHIS CLIP:\nHe walks.\n";
        var rules = "\nHOUSE RULES:\n" + new string('z', 4500);
        var full = core + rules;
        Assert.True(full.Length > ClipVideoPromptBuilder.VideoPromptHardCapChars);

        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(full);
        Assert.True(fitted.Length <= ClipVideoPromptBuilder.VideoPromptHardCapChars);
        Assert.DoesNotContain("HOUSE RULES:", fitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", fitted);
    }

    [Fact]
    public void FitPromptToVideoBudget_does_not_sacrifice_Grade()
    {
        var grade = $"<{PromptFieldTags.Grade}>watercolor wash on cold-press paper, muted primaries</{PromptFieldTags.Grade}>";
        var optics = $"<{PromptFieldTags.Optics}>" + new string('O', 200) + $"</{PromptFieldTags.Optics}>";
        var performance = $"<{PromptFieldTags.Performance}>" + new string('P', 450) + $"</{PromptFieldTags.Performance}>";
        var house = "\nHOUSE RULES:\n" + new string('z', 250);
        var notes = PromptTags.WrapWithNote("Context", new string('N', 150), "prior look");
        var core = "STYLE LOCK: 2D watercolor picture-book, never photoreal\n\n" +
                   "CHARACTER VARIABLES Character_Hero\n" + new string('A', 3400) + "\n" +
                   grade + optics + performance + notes;
        var full = core + house;
        Assert.True(full.Length > ClipVideoPromptBuilder.VideoPromptHardCapChars);

        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(full);
        Assert.True(fitted.Length <= ClipVideoPromptBuilder.VideoPromptHardCapChars);
        Assert.Contains($"<{PromptFieldTags.Grade}>", fitted, StringComparison.Ordinal);
        Assert.Contains("watercolor wash on cold-press paper", fitted, StringComparison.Ordinal);
        Assert.DoesNotContain("HOUSE RULES:", fitted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_illustrated_prompt_has_no_house_style_example_media()
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 1,
            visual_prompt = "<Grade>watercolor wash</Grade> INT. SCHOOLROOM - DAY. Character_Mary walks in.",
            characters_on_screen = new[] { "Character_Mary" },
            veo_continuation_source = "none",
            audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
        })).RootElement.Clone();

        var built = ClipVideoPromptBuilder.Build(
            clip,
            Path.GetTempPath(),
            styleHead: "STYLE LOCK: 2D watercolor picture-book, never photoreal",
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.DoesNotContain("picture-book CG", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photoreal, etc.", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HOUSE RULES:", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- Style:", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("<Grade>watercolor wash</Grade>", built.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_does_not_attach_cast_only_named_in_dialogue()
    {
        // Blueprint: Narrator only. Dialogue names "the old man" — must not promote Old Man on screen.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 4,
              "visual_prompt": "INT. CONFESSION ROOM. Character_Narrator ON CAMERA lip-syncs \"I loved the old man. I think it was his eye!\".",
              "characters_on_screen": ["Character_Narrator"],
              "primary_subject": "Character_Narrator",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "I loved the old man. I think it was his eye!",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-cast-dlg-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "lean pale man",
            },
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly white-haired man",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Equal(1, built.CastCount);
        Assert.DoesNotContain("Character_Old_Man", built.OnScreenKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("<CastCount>exactly 1", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Old_Man", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Single(built.ReferenceImagePaths);
        Assert.Contains("narrator", Path.GetFileName(built.ReferenceImagePaths[0]), StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void SanitizeActionText_strips_embedded_cast_count()
    {
        var raw = "INT. ROOM. CAST COUNT: exactly 1 on-screen identity(ies) — Character_Narrator. No extra people. An OLD MAN sleeps. / 480p, 24fps";
        var clean = ClipVideoPromptBuilder.SanitizeActionText(raw, new[] { "Character_Narrator", "Character_Old_Man" });
        Assert.DoesNotContain("CAST COUNT", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Old_Man", clean);
        // Roster lives in Characters + CastCount — do not restated-append the missing key.
        Assert.DoesNotContain("is on screen", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "NARRATOR (CONT'D). Character_Narrator ON CAMERA lip-syncs \"Hello.\"",
        "NARRATOR. Character_Narrator ON CAMERA lip-syncs \"Hello.\"")]
    [InlineData(
        "Character_Narrator He steadies his hands on his knees.",
        "Character_Narrator steadies his hands on his knees.")]
    [InlineData(
        "Character_Hero Character_Hero walks in.",
        "Character_Hero walks in.")]
    public void StripFountainLeakage_removes_contd_and_token_pronoun_glue(string raw, string expected)
    {
        var clean = ClipVideoPromptBuilder.StripFountainLeakage(raw);
        Assert.Equal(expected, clean);
        Assert.DoesNotContain("CONT", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Narrator He", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "True!-nervous-very, very dreadfully nervous I had been and am;",
        "True! Nervous — very, very dreadfully nervous I had been and am;")]
    [InlineData(
        "True!—nervous—very, very dreadfully nervous I had been and am;",
        "True! Nervous — very, very dreadfully nervous I had been and am;")]
    [InlineData(
        "healthily-how calmly I can tell you",
        "healthily — how calmly I can tell you")]
    [InlineData("Wait -- please!", "Wait — please!")]
    [InlineData("Oh God -- what have I done?", "Oh God — what have I done?")]
    [InlineData("", "")]
    [InlineData("  Hello world.  ", "Hello world.")]
    public void SanitizeSpokenDialogue_speech_safe_pauses(string raw, string expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.SanitizeSpokenDialogue(raw));
    }

    /// <summary>Real compounds must stay hyphenated (not become speech pauses).</summary>
    [Theory]
    [InlineData("Why is a raven like a writing-desk?", "writing-desk")]
    [InlineData("Good-bye, feet!", "Good-bye")]
    [InlineData("Come dine with us to-morrow.", "to-morrow")]
    [InlineData("What's to-day?", "to-day")]
    [InlineData("I am here to-night to warn you.", "to-night")]
    [InlineData("It's always tea-time.", "tea-time")]
    [InlineData("The stupidest tea-party I ever was at.", "tea-party")]
    [InlineData("Dead as a door-nail.", "door-nail")]
    [InlineData("A well-known fact.", "well-known")]
    [InlineData("An age-old idea.", "age-old")]
    [InlineData("Half-past one.", "Half-past")]
    [InlineData("I cut some more bread-and-butter.", "bread-and-butter")]
    [InlineData("Ah! Bed-curtains!", "Bed-curtains")]
    public void SanitizeSpokenDialogue_preserves_hyphenated_compounds(string raw, string mustContain)
    {
        var cleaned = ClipVideoPromptBuilder.SanitizeSpokenDialogue(raw);
        Assert.Contains(mustContain, cleaned, StringComparison.OrdinalIgnoreCase);
        // Must not have turned that compound into an em-dash pause
        var broken = mustContain.Replace("-", " — ", StringComparison.Ordinal);
        Assert.DoesNotContain(broken, cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("True! Nervous — very, very dreadfully nervous I had been and am;", "True!")]
    [InlineData("Hello world.", "Hello")]
    [InlineData("", "")]
    public void FirstSpokenToken_extracts_opening(string line, string expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.FirstSpokenToken(line));
    }

    [Fact]
    public void Build_audio_requires_opening_word()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. ROOM. Character_Narrator ON CAMERA lip-syncs \"True! Nervous very.\"",
              "characters_on_screen": ["Character_Narrator"],
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "True! Nervous very.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "pale man",
            },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Contains("Start speaking immediately with \"True!\"", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("exactly: \"True! Nervous very.\"", built.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_audio_and_visual_use_sanitized_spoken_dialogue()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. BARE ROOM - NIGHT. The Narrator speaks. Character_The_Narrator ON CAMERA lip-syncs \"True!-nervous-very, very dreadfully nervous I had been and am;\"",
              "characters_on_screen": ["Character_The_Narrator"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": {
                "speaker": "Character_The_Narrator",
                "dialogue": "True!-nervous-very, very dreadfully nervous I had been and am;",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_The_Narrator"] = new()
            {
                Key = "Character_The_Narrator",
                DisplayName = "Narrator",
                Description = "pale man",
                VoiceProfile = "tense confessor",
            },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.DoesNotContain("True!-nervous", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("True! Nervous — very", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("lip-syncs", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InferKeysFromProse_promotes_old_man_and_officers()
    {
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man" },
            ["Character_Officer"] = new() { Key = "Character_Officer", DisplayName = "Officer" },
            ["Character_Officer_Two"] = new() { Key = "Character_Officer_Two", DisplayName = "Officer Two" },
            ["Character_Officer_Three"] = new() { Key = "Character_Officer_Three", DisplayName = "Officer Three" },
        };
        var keys = ClipVideoPromptBuilder.InferKeysFromProse(
            "An OLD MAN sleeps. Three OFFICERS sit over the boards.", profiles);
        Assert.Contains("Character_Old_Man", keys);
        Assert.Contains("Character_Officer", keys);
        Assert.Contains("Character_Officer_Two", keys);
        Assert.Contains("Character_Officer_Three", keys);
    }

    [Fact]
    public void Build_uses_characters_on_screen_and_single_cast_count()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. CHAMBER. CAST COUNT: exactly 1 on-screen identity(ies) — Character_Narrator. No extra people. An OLD MAN sleeps behind a curtained bed. / 480p, 24fps",
              "characters_on_screen": ["Character_Narrator", "Character_Old_Man"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator", Description = "pale man" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man", Description = "elderly" },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Equal(2, built.CastCount);
        Assert.Equal(2, built.OnScreenKeys.Count);
        Assert.Contains("<CastCount>exactly 2", built.Prompt);
        Assert.Single(CommonRegex.Matches(built.Prompt, "<CastCount>"));
        Assert.DoesNotContain("CAST COUNT: exactly 1", built.Prompt);
        Assert.True(built.Prompt.IndexOf("<CastCount", StringComparison.OrdinalIgnoreCase) <
                    built.Prompt.IndexOf("<Clip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FitPromptToVideoBudget_strictly_enforces_1000_char_budget()
    {
        var longPrompt = new string('A', 1500);
        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(longPrompt, 1000);
        Assert.True(fitted.Length <= 1000, $"Expected fitted prompt length <= 1000, but got {fitted.Length}");
    }

    // --- PR2: identity continuity (fresh / extend / cast-change reseed) ---

    [Fact]
    public void Build_fresh_attaches_refs_and_no_identity_reinforce()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Old_Man sleeps in bed.",
              "characters_on_screen": ["Character_Old_Man"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-fresh-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly pale man in nightshirt",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 3);
        Assert.Equal("fresh", built.Mode);
        Assert.True(built.RefsAttachedToApi);
        Assert.NotEmpty(built.ReferenceImagePaths);
        Assert.DoesNotContain("<Identity>Match locked plate", built.Prompt, StringComparison.Ordinal);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_video_extend_same_cast_no_api_refs_has_identity_reinforce()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "Character_Old_Man stirs under the covers.",
              "characters_on_screen": ["Character_Old_Man"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-extend-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);
        var prevVideo = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prevVideo, new byte[2048]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly pale man in nightshirt",
            },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            characters: profiles,
            previousClipVisualPrompt: "Character_Old_Man sleeps.",
            previousClipVideoPath: prevVideo,
            maxRefs: 3);

        Assert.Equal("video-extend", built.Mode);
        Assert.False(built.RefsAttachedToApi);
        Assert.Empty(built.ReferenceImagePaths);
        Assert.Contains("<Identity>Match locked plate", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("On-screen:", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_Old_Man", built.Prompt);
        Assert.Contains("EXTENSION", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_cast_change_reseed_is_fresh_with_refs_when_prev_video_cleared()
    {
        // FilmJobService nulls previousClipVideoPath on cast-set change (IdentityReseedOnCastChange).
        // Builder then attaches locked plates like clip 1.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 3,
              "visual_prompt": "Three OFFICERS enter. Character_Officer speaks.",
              "characters_on_screen": ["Character_Officer", "Character_Officer_Two", "Character_Officer_Three"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "Character_Officer", "dialogue": "A noise?", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-reseed-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_two_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_three_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Officer"] = new() { Key = "Character_Officer", DisplayName = "Officer", Description = "uniformed officer" },
            ["Character_Officer_Two"] = new() { Key = "Character_Officer_Two", DisplayName = "Officer Two", Description = "second officer" },
            ["Character_Officer_Three"] = new() { Key = "Character_Officer_Three", DisplayName = "Officer Three", Description = "third officer" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man", Description = "elderly" },
        };

        // prev video path NOT passed → same as FilmJobService reseed after cast change
        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            characters: profiles,
            previousClipVisualPrompt: "Character_Old_Man sleeps. (prior cast for prose only)",
            previousClipVideoPath: null,
            maxRefs: 5);

        Assert.Equal("fresh", built.Mode);
        Assert.True(built.RefsAttachedToApi);
        Assert.True(built.ReferenceImagePaths.Count >= 1);
        Assert.Equal(3, built.CastCount);
        Assert.DoesNotContain("<Identity>Match locked plate", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("<IMAGE_1>", built.Prompt);
        // Reseed carries the prior clip's LOOK when there is one. This prior prompt is pure action
        // with no slug, lighting or grade, so there is nothing to carry — and the action itself
        // must not ride in, or the reseed replays it.
        Assert.DoesNotContain("sleeps", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_compacts_non_focus_characters_from_primary_and_speaker()
    {
        // No motion verbs required — Old Man is non-focus via metadata only
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. BEDCHAMBER. Character_Narrator at the door. Character_Old_Man in the bed.",
              "characters_on_screen": ["Character_Narrator", "Character_Old_Man"],
              "primary_subject": "Character_Narrator",
              "audio_payload": { "speaker": "Character_Narrator", "dialogue": "I opened it gently.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-multi-compact-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        // No reference images: identity prose only ships when no picture carries it, and that is
        // exactly the case this regression is about — Tell-Tale Heart's Old Man drifted in
        // video-extend clips, which cannot attach refs at all, when his line was truncated.

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "Lean pale man in waistcoat",
                VisualLock = "Same lean pale face",
            },
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "Frail elderly man with sparse white hair and blue eye",
                VisualLock = "Always elderly, white-haired",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("Character_Narrator", built.Prompt);
        Assert.Contains("Also present (not shot focus)", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passive background", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // Narrator should keep full visual lock prose
        Assert.Contains("<VisualLock>", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_non_focus_compaction_preserves_visual_lock_not_truncated_description()
    {
        // Regression: the non-focus "compact" identity line used to build from a 60-char-truncated
        // Description and never included VisualLock at all — a distinguishing trait (e.g. the Old
        // Man's filmy pale eye) that fell after character 57 in the description, or that only lived
        // in VisualLock, silently vanished from every clip where that character was present but not
        // the shot's focus. VisualLock must now be what the compact line is built from when present.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. BEDCHAMBER. Character_Narrator at the door. Character_Old_Man in the bed.",
              "characters_on_screen": ["Character_Narrator", "Character_Old_Man"],
              "primary_subject": "Character_Narrator",
              "audio_payload": { "speaker": "Character_Narrator", "dialogue": "I opened it gently.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-multi-compact-vlock-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        // No reference images: identity prose only ships when no picture carries it, and that is
        // exactly the case this regression is about — Tell-Tale Heart's Old Man drifted in
        // video-extend clips, which cannot attach refs at all, when his line was truncated.

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "Lean pale man in waistcoat",
                VisualLock = "Same lean pale face",
            },
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "Frail elderly man, thin stooped build, sparse white hair, deeply lined pale face; one pale blue eye with a dull filmy veil, the other eye ordinary; wears a plain white period nightshirt.",
                VisualLock = "Always elderly, white-haired, frail; signature constant is the single pale blue eye with dull filmy veil that must not drift to clear blue.",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("Also present (not shot focus)", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pale blue eye", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_keeps_the_visual_lock_whole_and_takes_wardrobe_from_the_plan()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "<Setting>EXT. COUNTRY LANE - DAY</Setting> <Cast>Character_Mary</Cast> <Action>MARY walks.</Action> <Wardrobe>Character_Mary still wears pale pinafore, rose ribbon</Wardrobe>",
              "characters_on_screen": ["Character_Mary"],
              "primary_subject": "Character_Mary",
              "audio_payload": { "speaker": "Character_Mary", "dialogue": "Hello.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-vlock-wardrobe-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        // No reference image — visual_lock ships only when no picture carries the identity.

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new()
            {
                Key = "Character_Mary",
                DisplayName = "Mary",
                Description = "School-age girl with brown braids",
                // Face / markings only — clothes live on wardrobe_always and reach the model
                // through the plan's Wardrobe clause, so nothing is cut back out here.
                VisualLock = "brown braids, grey eyes, school-age girl",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("pale pinafore", built.ActionText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rose ribbon", built.ActionText, StringComparison.OrdinalIgnoreCase);
        var lockInner = System.Text.RegularExpressions.Regex.Match(
            built.CharacterVariables, @"<VisualLock>(.*?)</VisualLock>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(lockInner));
        Assert.Equal("brown braids, grey eyes, school-age girl", lockInner.Trim());

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_strips_garments_from_description_so_a_put_on_coat_is_not_fought()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "<Setting>EXT. COUNTRY LANE - DAY</Setting> <Action>MARY walks.</Action> <Wardrobe>Character_Mary still wears pale pinafore, rose ribbon, tweed walking coat</Wardrobe>",
              "characters_on_screen": ["Character_Mary"],
              "primary_subject": "Character_Mary",
              "audio_payload": { "speaker": "Character_Mary", "dialogue": "Hello.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new()
            {
                Key = "Character_Mary",
                DisplayName = "Mary",
                Description = "School-age girl with brown braids, a pale pinafore, and a rose ribbon.",
                VisualLock = "brown braids, grey eyes, school-age girl, pale pinafore",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Contains("<Wardrobe>", built.ActionText, StringComparison.Ordinal);
        Assert.Contains("tweed walking coat", built.ActionText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pale pinafore", built.ActionText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brown braids", built.CharacterVariables, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinafore", built.CharacterVariables, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ribbon", built.CharacterVariables, StringComparison.OrdinalIgnoreCase);
        var lockInner = System.Text.RegularExpressions.Regex.Match(
            built.CharacterVariables, @"<VisualLock>(.*?)</VisualLock>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("brown braids", lockInner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinafore", lockInner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFocusKeys_big_action_keeps_all_on_screen()
    {
        var keys = ClipVideoPromptBuilder.ResolveFocusKeys(
            new[] { "Character_A", "Character_B" },
            primarySubject: "Character_A",
            speaker: null,
            actionClass: "big_action");
        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_A", keys);
        Assert.Contains("Character_B", keys);
    }

    [Fact]
    public void ResolveFocusKeys_locks_both_speakers_on_a_cross_speaker_beat()
    {
        var keys = ClipVideoPromptBuilder.ResolveFocusKeys(
            new[] { "Character_Nick", "Character_Sionna", "Character_Ma" },
            primarySubject: null,
            speaker: "Character_Nick",
            actionClass: "dialogue",
            secondarySpeaker: "Character_Sionna");

        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_Nick", keys);
        Assert.Contains("Character_Sionna", keys);
        Assert.DoesNotContain("Character_Ma", keys);
    }

    [Fact]
    public void ResolveFocusKeysForClip_includes_secondary_speaker_from_audio_payload()
    {
        var clip = JsonDocument.Parse("""
            {
              "characters_on_screen": ["Character_Nick", "Character_Sionna", "Character_Ma"],
              "action_class": "dialogue",
              "audio_payload": {
                "speaker": "Character_Nick",
                "dialogue": "You coming or not?",
                "secondary_speaker": "Character_Sionna",
                "secondary_dialogue": "Give me a second.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var keys = ClipVideoPromptBuilder.ResolveFocusKeysForClip(
            new[] { "Character_Nick", "Character_Sionna", "Character_Ma" }, clip);
        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_Nick", keys);
        Assert.Contains("Character_Sionna", keys);
    }

    [Fact]
    public void ResolveFocusKeysForClip_prefers_explicit_focus_keys()
    {
        var clip = JsonDocument.Parse("""
            {
              "characters_on_screen": ["Character_A", "Character_B", "Character_C"],
              "primary_subject": "Character_A",
              "focus_keys": ["Character_B", "Character_C"],
              "audio_payload": { "speaker": "Character_A", "dialogue": "Hello.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;
        var keys = ClipVideoPromptBuilder.ResolveFocusKeysForClip(
            new[] { "Character_A", "Character_B", "Character_C" }, clip);
        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_B", keys);
        Assert.Contains("Character_C", keys);
        Assert.DoesNotContain("Character_A", keys);
    }

    [Fact]
    public void Stage2_CoalesceShortMonologueBeats_merges_consecutive_short_monologues()
    {
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Narrator",
            ["dialogue"] = "True! Nervous I had been.",
            ["delivery"] = "spoken_on_camera",
            ["visual_event"] = "Narrator sits at table.",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Narrator",
            ["dialogue"] = "But why will you say I am mad?",
            ["delivery"] = "spoken_on_camera",
            ["visual_event"] = "Narrator leans in.",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceShortMonologueBeats(beats);
        Assert.Single(coalesced);
        Assert.Contains("True! Nervous I had been. But why will you say I am mad?", coalesced[0]["dialogue"]?.ToString());
    }

    [Theory]
    [InlineData("dismembering: head, arms, legs", "working in methodical silence: head, arms, legs")]
    [InlineData("deposits the remains between scantlings", "deposits the contents between scantlings")]
    [InlineData("first I dismembered the corpse", "first I working in methodical silence the quiet form")]
    public void ScrubContentSafetyTriggers_softens_moderation_keywords(string input, string expected)
    {
        var result = ClipVideoPromptBuilder.ScrubContentSafetyTriggers(input);
        Assert.Equal(expected, result);
        Assert.DoesNotContain("dismember", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpse", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_AttachesLocationPlate_WhenSlotRemains()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-loc-ref-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        Directory.CreateDirectory(locDir);
        // minimal non-empty png-ish blob (>=64 bytes)
        var plate = Path.Combine(locDir, "loc_ithaca_palace_ref.png");
        File.WriteAllBytes(plate, Enumerable.Repeat((byte)0x42, 128).ToArray());

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Wide of the empty hall.",
              "characters_on_screen": [],
              "location_id": "Loc_Ithaca_Palace",
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(clip, dir, new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(), maxRefs: 3);

        Assert.True(built.LocationRefAttached);
        Assert.Equal("Loc_Ithaca_Palace", built.LocationKey);
        Assert.Equal("<IMAGE_1>", built.LocationImageTag);
        Assert.Single(built.ReferenceImagePaths);
        Assert.Contains("SetReference", built.Prompt);
        Assert.Contains("<IMAGE_1>", built.Prompt);
        Assert.Contains("Match architecture, materials, props, and depth of that plate.", built.Prompt);
        Assert.DoesNotContain("and lighting of that plate", built.Prompt, StringComparison.Ordinal);

        try { Directory.Delete(dir, true); } catch { /* temp */ }
    }

    [Fact]
    public void Generate_prompt_emits_one_Lighting_and_one_Grade()
    {
        const string fixture =
            "<StyleLock>watercolor</StyleLock> " +
            "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
            "<Action>Character_Mary walks in.</Action> " +
            "<Lighting>Soft warm daylight through tall windows.</Lighting> " +
            "<Grade>Kodak Vision3 250D 5207 film stock, warm honey-amber woods</Grade>";
        const string previous =
            "<Setting>EXT. LANE - DAY</Setting> " +
            "<Lighting>Harsh noon glare, volumetric dust motes.</Lighting> " +
            "<Grade>Fuji Eterna 500T, cool teal</Grade>";

        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt = fixture,
            characters_on_screen = Array.Empty<string>(),
            location_id = "Loc_Schoolroom",
            audio_payload = new { dialogue = "" },
        })).RootElement;

        var built = ClipVideoPromptBuilder.Build(
            clip,
            Path.GetTempPath(),
            previousClipVisualPrompt: previous,
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.Equal("fresh", built.Mode);
        Assert.Equal(1, CountTag(built.Prompt, "Lighting"));
        Assert.Equal(1, CountTag(built.Prompt, "Grade"));
        Assert.Contains("Soft warm daylight through tall windows.", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("Kodak Vision3 250D", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("volumetric dust motes", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fuji Eterna", built.Prompt, StringComparison.Ordinal);
    }

    private static int CountTag(string prompt, string tag) =>
        CommonRegex.Matches(prompt ?? "", $@"<{tag}>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

    [Fact]
    public void Build_SkipsLocationPlate_WhenNoSlot()
    {
        // maxRefs=1: no reserved set slot; one character fills the only IMAGE.
        var dir = Path.Combine(Path.GetTempPath(), "ptm-loc-ref-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        var charDir = Path.Combine(dir, "assets", "characters");
        Directory.CreateDirectory(locDir);
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(locDir, "loc_hall_ref.png"), Enumerable.Repeat((byte)1, 128).ToArray());
        File.WriteAllBytes(Path.Combine(charDir, "character_a_ref.png"), Enumerable.Repeat((byte)2, 128).ToArray());

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "A stands in the hall.",
              "characters_on_screen": ["Character_A"],
              "location_id": "Loc_Hall",
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_A"] = new() { Key = "Character_A", DisplayName = "A" },
        };

        var built = ClipVideoPromptBuilder.Build(clip, dir, profiles, maxRefs: 1);
        Assert.Single(built.ReferenceImagePaths);
        Assert.False(built.LocationRefAttached);
        Assert.Equal("Loc_Hall", built.LocationKey);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Build_ReservesSlot_ForLocationPlate_WhenLocked()
    {
        // maxRefs=2 with 2 characters + locked set: char budget 1, then location → both attached.
        var dir = Path.Combine(Path.GetTempPath(), "ptm-loc-ref-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        var charDir = Path.Combine(dir, "assets", "characters");
        Directory.CreateDirectory(locDir);
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(locDir, "loc_hall_ref.png"), Enumerable.Repeat((byte)1, 128).ToArray());
        File.WriteAllBytes(Path.Combine(charDir, "character_a_ref.png"), Enumerable.Repeat((byte)2, 128).ToArray());
        File.WriteAllBytes(Path.Combine(charDir, "character_b_ref.png"), Enumerable.Repeat((byte)3, 128).ToArray());

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "A and B stand in the hall.",
              "characters_on_screen": ["Character_A", "Character_B"],
              "location_id": "Loc_Hall",
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_A"] = new() { Key = "Character_A", DisplayName = "A" },
            ["Character_B"] = new() { Key = "Character_B", DisplayName = "B" },
        };

        var built = ClipVideoPromptBuilder.Build(clip, dir, profiles, maxRefs: 2);
        Assert.Equal(2, built.ReferenceImagePaths.Count);
        Assert.True(built.LocationRefAttached);
        Assert.Equal("Loc_Hall", built.LocationKey);
        Assert.Contains("loc_hall_ref.png", built.ReferenceImagePaths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Build_UsesFallbackLocationKey_FromScene()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-loc-ref-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        Directory.CreateDirectory(locDir);
        File.WriteAllBytes(Path.Combine(locDir, "loc_ithaca_ref.png"), Enumerable.Repeat((byte)9, 128).ToArray());

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Empty courtyard.",
              "characters_on_screen": [],
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(
            clip, dir, new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(),
            maxRefs: 3, fallbackLocationKey: "Loc_Ithaca");

        Assert.True(built.LocationRefAttached);
        Assert.Equal("Loc_Ithaca", built.LocationKey);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Build_Recognizes_ClientJson_Offloaded_Plates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-client-ref-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        var charDir = Path.Combine(dir, "assets", "characters");
        Directory.CreateDirectory(locDir);
        Directory.CreateDirectory(charDir);

        // Write .client.json markers
        File.WriteAllText(Path.Combine(locDir, "loc_country_lane_ref.png.client.json"), "{\"storage\":\"client\"}");
        File.WriteAllText(Path.Combine(charDir, "character_old_man_ref.png.client.json"), "{\"storage\":\"client\"}");

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "An old man on a country lane.",
              "characters_on_screen": ["Character_Old_Man"],
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>
        {
            ["Character_Old_Man"] = new() { Description = "Old man" }
        };

        var built = ClipVideoPromptBuilder.Build(
            clip, dir, profiles, maxRefs: 5, fallbackLocationKey: "Loc_Country_Lane");

        Assert.True(built.LocationRefAttached);
        Assert.Equal("Loc_Country_Lane", built.LocationKey);
        Assert.Equal(2, built.ReferenceImagePaths.Count);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Build_Sets_VideoExtend_Mode_When_ExtendFileId_Provided()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "Camera pans right across the fence.",
              "characters_on_screen": [],
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(
            clip, Path.GetTempPath(),
            characters: new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(),
            previousClipExtendFileId: "file_d7647878-38b9-4844-a7e1-9a73bea080a3");

        Assert.Equal(ClipVideoPromptBuilder.ModeVideoExtend, built.Mode);
    }

    [Fact]
    public void MediaDataUri_IsExistingMediaPath_Recognizes_ClientMarkers_And_Variants()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-media-data-uri-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var refPath = Path.Combine(dir, "loc_country_lane_ref.png");
            var clientMarker = refPath + ProjectStore.ClientMarkerExtension;
            var variantPath = Path.Combine(dir, "loc_country_lane_variant_01.png");

            Assert.False(MediaDataUri.IsExistingMediaPath(refPath));

            // 1. Client marker enables IsExistingMediaPath
            File.WriteAllText(clientMarker, "{\"storage\":\"client\"}");
            Assert.True(MediaDataUri.IsExistingMediaPath(refPath));

            // 2. Variant resolution when ref is missing
            File.WriteAllBytes(variantPath, Enumerable.Repeat((byte)7, 128).ToArray());
            var resolved = MediaDataUri.ResolveExistingMediaPath(refPath);
            Assert.NotNull(resolved);
            Assert.Equal(variantPath, resolved);

            // 3. Direct ref file resolution when present
            File.WriteAllBytes(refPath, Enumerable.Repeat((byte)8, 128).ToArray());
            resolved = MediaDataUri.ResolveExistingMediaPath(refPath);
            Assert.NotNull(resolved);
            Assert.Equal(refPath, resolved);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Build_Tags_LocationKey_In_ActionText_When_LocationPlate_Attached()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-loc-tag-" + Guid.NewGuid().ToString("N"));
        var locDir = Path.Combine(dir, "assets", "locations");
        Directory.CreateDirectory(locDir);
        File.WriteAllBytes(Path.Combine(locDir, "loc_country_lane_ref.png"), Enumerable.Repeat((byte)1, 128).ToArray());

        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Walking along Loc_Country_Lane into the distance.",
              "characters_on_screen": [],
              "location_id": "Loc_Country_Lane",
              "audio_payload": { "dialogue": "" }
            }
            """).RootElement;

        var built = ClipVideoPromptBuilder.Build(
            clip, dir, new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(), maxRefs: 3);

        Assert.True(built.LocationRefAttached);
        Assert.Equal("<IMAGE_1>", built.LocationImageTag);
        Assert.Contains("Loc_Country_Lane <IMAGE_1>", built.Prompt);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void ProjectAssetNaming_Matches_Canonical_And_Alias_Filenames()
    {
        var locCandidates = ProjectAssetNaming.LocationRefFileNameCandidates("Loc_Country_Lane").ToList();
        Assert.Contains("loc_country_lane_ref.png", locCandidates);
        Assert.Contains("country_lane_ref.png", locCandidates);

        var charCandidates = ProjectAssetNaming.CharacterRefFileCandidates("Character_Mary").ToList();
        Assert.Contains("character_mary_ref.png", charCandidates);
        Assert.Contains("mary_ref.png", charCandidates);
    }

    [Fact]
    public async Task MediaDataUri_FileToDataUriAsync_Throws_FileNotFoundException_When_Only_Marker_Exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-marker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "loc_country_lane_ref.png");
            File.WriteAllText(target + ProjectStore.ClientMarkerExtension, "{\"storage\":\"client\"}");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                MediaDataUri.FileToDataUriAsync(target, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void AssertFillNPacing(string prompt, int seconds)
    {
        Assert.Contains($"This is a {seconds}-second shot", prompt, StringComparison.Ordinal);
        Assert.Contains($"full {seconds} seconds", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("End cleanly when the spoken line", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("End cleanly when the primary physical action finishes", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tight action", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("avoid long empty holds", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Prefer tight action after speech", prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertAudioOwnsClosedMouthPause(string prompt)
    {
        const string pause =
            "After the last word, hold a brief natural pause with a closed mouth (about half a second)";
        Assert.Contains(pause, prompt, StringComparison.Ordinal);
        var clipIdx = prompt.IndexOf("<Clip>", StringComparison.Ordinal);
        Assert.True(clipIdx >= 0, "expected a <Clip> section");
        var clipSection = prompt[clipIdx..];
        Assert.DoesNotContain("half a second", clipSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("closed mouth", clipSection, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }
}


public class PromptCompressionKeepsVoiceLockTests
{
    /// <summary>Mary19 S02C05: every prompt over the cap was compressed and compression deleted the
    /// speaker's VoiceLock, so the narrator profile ("adult male…") never reached the model.</summary>
    [Fact]
    public void Compression_keeps_the_speakers_VoiceLock_shortened_not_dropped()
    {
        var longProfile = "Adult male, 50s, warm baritone storyteller, even mid register, measured couplet cadence, never rushed, " +
                          "exactly this one voice (same sex, age and timbre) as in every other clip of this film.";
        var input = "<Audio>REQUIRED native Grok off-camera voiceover. Character_Narrator narrates exactly: \"And so the teacher turned him out.\". " +
                    "<VoiceLock>Character_Narrator: " + longProfile + "</VoiceLock></Audio> <Voice>Character_Teacher: calm</Voice>";
        var compressed = PageToMovie.Engine.ClipVideoPromptBuilder.CompressPromptText(input);
        Assert.Contains("<VoiceLock>", compressed);
        Assert.Contains("Adult male, 50s", compressed);
        Assert.DoesNotContain("<Voice>", compressed);
        var lockText = System.Text.RegularExpressions.Regex.Match(compressed, "<VoiceLock>(.*?)</VoiceLock>").Groups[1].Value;
        Assert.True(lockText.Length <= 140, lockText);
    }
}

public class PreviousClipQuoteRedactionTests
{
    /// <summary>
    /// Mary19 S03C02 re-spoke S03C01's narration because the previous clip's quoted line rode into
    /// the next prompt. That used to be handled by redacting the quote; it is now structurally
    /// impossible — no previous-clip speech reaches a prompt by any path, so there is nothing to
    /// redact. This asserts the structure rather than the old workaround.
    /// </summary>
    [Fact]
    public void Previous_clip_speech_cannot_reach_the_next_prompt()
    {
        const string prev =
            "<Setting>EXT. LANE - DAY</Setting> <Action>MARY walks</Action> " +
            "<Speech>Character_Narrator says \"But still he lingered near.\"</Speech> " +
            "<Lighting>Soft light.</Lighting>";

        // Fresh reseed: look survives, speech does not.
        var look = ClipVideoPromptBuilder.PreviousClipLookOnly(prev, Array.Empty<string>());
        Assert.Contains("<Lighting>", look, StringComparison.Ordinal);
        Assert.DoesNotContain("lingered near", look, StringComparison.Ordinal);
        Assert.DoesNotContain("<Speech>", look, StringComparison.Ordinal);

        // Extend / continue: no previous-clip prose at all.
        foreach (var mode in new[] { "video-extend", "continue" })
            Assert.DoesNotContain("lingered near", InvokeContinuityBlock(mode, prev), StringComparison.Ordinal);
    }

    private const string PrevClipPrompt =
        "<StyleLock>watercolor</StyleLock> <Setting>INT. SCHOOLROOM - DAY</Setting> " +
        "<Action>MARY comes through the door. THE LAMB follows at her heel into the aisle</Action> " +
        "<Lighting>Soft warm daylight through tall windows.</Lighting> " +
        "<Camera>Wide 27mm locked.</Camera> <Grade>hand-tinted print stock, cream paper</Grade>";

    /// <summary>
    /// The predecessor video (or its last frame) IS the input — re-describing it in prose made the
    /// model replay it. Mary19 S02C02 restaged S02C01's "comes through the door" from this block.
    /// </summary>
    [Theory]
    [InlineData("video-extend")]
    [InlineData("continue")]
    public void Clip_with_visual_input_gets_no_previous_clip_prose(string mode)
    {
        var block = InvokeContinuityBlock(mode, PrevClipPrompt);
        Assert.DoesNotContain("PreviousClip", block, StringComparison.Ordinal);
        Assert.DoesNotContain("comes through the door", block, StringComparison.Ordinal);
        Assert.Contains("Continuity", block, StringComparison.Ordinal);
    }

    /// <summary>Cast-change reseed has no visual input, so the LOOK is kept — the action is not.</summary>
    [Fact]
    public void Fresh_reseed_context_keeps_look_and_drops_action()
    {
        var look = ClipVideoPromptBuilder.PreviousClipLookOnly(PrevClipPrompt, Array.Empty<string>());
        Assert.Contains("INT. SCHOOLROOM", look, StringComparison.Ordinal);
        Assert.Contains("<Lighting>", look, StringComparison.Ordinal);
        Assert.Contains("hand-tinted print stock", look, StringComparison.Ordinal);
        Assert.DoesNotContain("comes through the door", look, StringComparison.Ordinal);
        Assert.DoesNotContain("follows at her heel", look, StringComparison.Ordinal);
        Assert.DoesNotContain("<Camera>", look, StringComparison.Ordinal);
    }

    /// <summary>
    /// DropRepeatedDirectives only drops byte-identical copies. If a fresh reseed re-embeds the
    /// previous clip's Lighting/Grade while this clip already has its own, two looks ship.
    /// </summary>
    [Fact]
    public void Fresh_reseed_does_not_reembed_Lighting_or_Grade_this_clip_already_has()
    {
        const string current =
            "<Setting>INT. SCHOOLROOM - NIGHT</Setting> " +
            "<Lighting>Moonlight through tall windows, hard shadows.</Lighting> " +
            "<Grade>Fuji Eterna 500T, cool moonlight</Grade>";
        var look = ClipVideoPromptBuilder.PreviousClipLookOnly(
            PrevClipPrompt, Array.Empty<string>(), current);
        Assert.DoesNotContain("<Lighting>", look, StringComparison.Ordinal);
        Assert.DoesNotContain("<Grade>", look, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setting>", look, StringComparison.Ordinal);
    }

    /// <summary>A byte-identical second copy of a descriptive block buys nothing.</summary>
    [Fact]
    public void Identical_descriptive_blocks_are_emitted_once()
    {
        const string lighting = "<Lighting>Soft warm daylight through tall windows.</Lighting>";
        var deduped = ClipVideoPromptBuilder.DropRepeatedDirectives($"A {lighting} B {lighting} C");
        Assert.Equal(1, CountOccurrences(deduped, lighting));

        // Two DIFFERENT blocks are a real instruction — never silently merged.
        var kept = ClipVideoPromptBuilder.DropRepeatedDirectives(
            $"A {lighting} B <Lighting>Harsh noon glare.</Lighting> C");
        Assert.Equal(2, CountOccurrences(kept, "<Lighting>"));
    }

    /// <summary>
    /// A half-written directive is worse than a missing one — the model still tries to honour it.
    /// Real Mary19 traffic ended at "&lt;Camera&gt;Medium tracking".
    /// </summary>
    [Fact]
    public void Budget_cut_never_leaves_a_dangling_tag()
    {
        var prompt = new string('x', 400) + " <Camera>Medium tracking shot that runs well past the cap</Camera>";
        var capped = ClipVideoPromptBuilder.TagSafeHeadCap(prompt, 430);
        Assert.True(capped.Length <= 430);
        Assert.DoesNotContain("<Camera>", capped, StringComparison.Ordinal);
    }

    /// <summary>Notes explain their block; they outlive mechanical squeezing and die only under real pressure.</summary>
    [Fact]
    public void Section_notes_survive_compression_but_not_a_tight_budget()
    {
        var withNote = PromptTags.WrapWithNote("Context", "its action already happened", "prior look");
        Assert.Contains("note=", ClipVideoPromptBuilder.CompressPromptText(withNote), StringComparison.Ordinal);

        var padded = withNote + " " + new string('y', 4000);
        Assert.DoesNotContain("note=", ClipVideoPromptBuilder.FitPromptToVideoBudget(padded, 300), StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    /// <summary>
    /// The plan writes &lt;Action&gt; from a story beat, so a continuation clip whose beat restages
    /// the room contradicts this block. Stage 2 is where that is prevented; this only settles which
    /// way the model leans when a plan built before the staging test still contradicts itself —
    /// holding position beats teleporting across the set.
    /// </summary>
    [Theory]
    [InlineData("video-extend")]
    [InlineData("continue")]
    public void Continuing_clip_takes_positions_from_the_previous_last_frame(string mode)
    {
        var block = InvokeContinuityBlock(mode, PrevClipPrompt);
        Assert.Contains("Positions come from that frame", block, StringComparison.Ordinal);
        Assert.Contains("not a new arrangement", block, StringComparison.Ordinal);
        Assert.DoesNotContain("wardrobe", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outfits", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fresh_clip_is_not_told_to_hold_a_previous_frame()
    {
        var block = InvokeContinuityBlock("fresh", null!);
        Assert.DoesNotContain("Positions come from that frame", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_generate_prompt_has_one_roster_and_one_Negative()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "<Setting>EXT. LANE - DAY</Setting> <Cast>also on screen: Character_The_Lamb</Cast> <Action>Character_Mary walks. Character_The_Lamb is on screen.</Action> <MustNot>no crowd extras; no extra hats</MustNot>",
              "characters_on_screen": ["Character_Mary", "Character_The_Lamb"],
              "negative_prompt": "no crowd extras, no extra hats",
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new() { Key = "Character_Mary", DisplayName = "Mary", Description = "school-age girl" },
            ["Character_The_Lamb"] = new() { Key = "Character_The_Lamb", DisplayName = "The Lamb", Description = "tiny white lamb" },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip, Path.GetTempPath(), profiles,
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.Equal(2, built.CastCount);
        Assert.Contains("<CastCount>exactly 2 distinct on-screen character identity(ies) only — Character_Mary, Character_The_Lamb.", built.Prompt, StringComparison.Ordinal);
        Assert.Single(CommonRegex.Matches(built.Prompt, "<CastCount>"));
        Assert.Contains("<Characters", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("do not redesign faces", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("do not redesign faces or wardrobe", built.Prompt, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("<Cast>", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("also on screen", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("On-screen:", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("is on screen", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<MustNot>", built.Prompt, StringComparison.Ordinal);

        Assert.Contains("<Negative>", built.Prompt, StringComparison.Ordinal);
        Assert.Single(CommonRegex.Matches(built.Prompt, "<Negative>"));
        Assert.Single(CommonRegex.Matches(built.Prompt, "no crowd extras", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Single(CommonRegex.Matches(built.Prompt, "no extra hats", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("no legible text", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("photoreal", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\bC\d+\b", built.Prompt);
    }

    [Fact]
    public void Build_extend_IdentityReinforce_has_no_On_screen_roster()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "<Action>Character_Mary walks. Character_The_Lamb follows.</Action>",
              "characters_on_screen": ["Character_Mary", "Character_The_Lamb"],
              "negative_prompt": "no crowd extras",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new() { Key = "Character_Mary", DisplayName = "Mary", Description = "school-age girl" },
            ["Character_The_Lamb"] = new() { Key = "Character_The_Lamb", DisplayName = "The Lamb", Description = "tiny white lamb" },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip, Path.GetTempPath(), profiles, previousClipExtendFileId: "file_prev");

        Assert.Equal("video-extend", built.Mode);
        Assert.Contains("<Identity>", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("do not drift", built.Prompt, StringComparison.OrdinalIgnoreCase);
        var identity = System.Text.RegularExpressions.Regex.Match(
            built.Prompt, @"<Identity>(.*?)</Identity>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("wardrobe", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outfit", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("On-screen:", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<Cast>", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("is on screen", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<CastCount>exactly 2", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_Mary", built.CastCountLine, StringComparison.Ordinal);
        Assert.Contains("Character_The_Lamb", built.CastCountLine, StringComparison.Ordinal);
    }

    private static string InvokeContinuityBlock(string mode, string previousClipVisualPrompt)
    {
        var m = typeof(ClipVideoPromptBuilder).GetMethod(
            "BuildContinuityBlock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildContinuityBlock not found");
        return (string)m.Invoke(null, new object?[]
        {
            mode, Array.Empty<string>(), false, previousClipVisualPrompt, null, null,
        })!;
    }
}
