using System.Text.Json;
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
        Assert.Contains("Small black-and-white dog", built.Prompt);
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

        Assert.Contains("Silent beat", built.Prompt);
        Assert.Contains("do not show any on-screen character", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the spoken line", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_clip_with_dialogue_still_uses_spoken_line_closing()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
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

        Assert.Contains("the spoken line and primary action finish", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silent beat", built.Prompt);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
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
        Assert.Contains("PREVIOUS CLIP", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rockets across the grass", built.Prompt);
        Assert.Contains("EXTENSION", built.Prompt, StringComparison.OrdinalIgnoreCase);
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
        var input = "<Characters note=\"use these identities consistently; do not redesign faces or wardrobe\">\n" +
                    " Character_The_Narrator <IMAGE_1>: Lean man of middle years. <VisualLock>dark coat</VisualLock>.\n" +
                    " Character_Old_Man <IMAGE_2>: Elderly man with pale blue eye.\n" +
                    "<Clip>\n" +
                    "<Camera>Medium shot</Camera>. Color grading: Dark. Character_The_Narrator ON CAMERA lip-syncs to Character_Old_Man <IMAGE_2>.";

        var compressed = ClipVideoPromptBuilder.CompressPromptText(input);

        Assert.Contains("<Characters>", compressed);
        Assert.DoesNotContain("Character_The_Narrator", compressed);
        Assert.DoesNotContain("Character_Old_Man", compressed);
        Assert.DoesNotContain("<IMAGE_1>", compressed);
        Assert.DoesNotContain("<IMAGE_2>", compressed);
        Assert.Contains("C1 I1", compressed);
        Assert.Contains("C2 I2", compressed);
        Assert.Contains("<Camera>Medium shot</Camera>", compressed);
        Assert.Contains("Grade: Dark.", compressed);
        Assert.Contains("<VisualLock>dark coat</VisualLock>", compressed);
        Assert.Contains("C1 lip-syncs to C2 I2.", compressed);
    }

    [Fact]
    public void CompressPromptText_reduces_tell_tale_heart_prompt_significantly()
    {
        var original = "STYLE LOCK: Period gothic live-action, mid-19th-century interiors; candlelight and deep shadows; desaturated cool-gray palette; naturalistic skin and fabric texture; no illustration or stylized animation\n" +
                       "<Characters note=\"use these identities consistently; do not redesign faces or wardrobe\">\n" +
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

        Assert.True(compressed.Length < 2500, $"Compressed {compressed.Length} vs Original {original.Length}");
        Assert.DoesNotContain("Character_The_Narrator", compressed);
        Assert.DoesNotContain("<IMAGE_1>", compressed);
        Assert.DoesNotContain("Kodak Vision3 500T 5219 film stock", compressed);
        Assert.DoesNotContain("/ 480p, 24fps", compressed);
        Assert.Contains("Kodak 500T film", compressed);
        Assert.Contains("C1 I1", compressed);
        // Regression: this instruction used to be deleted outright rather than shortened, leaving
        // the focus character's reference image attached (I1) with no instruction to match it.
        Assert.Contains("Match I1 exactly.", compressed);
        // Voice descriptions/locks are dropped (visual video models don't use voice tuning text).
        Assert.DoesNotContain("<Voice>", compressed);
        Assert.DoesNotContain("<VoiceLock>", compressed);
        Assert.DoesNotContain("medium-high tense pitch", compressed);
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
        Assert.Contains("Character_Narrator", clean);
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
        Assert.Contains("new cast plate refs attached", built.Prompt, StringComparison.OrdinalIgnoreCase);

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
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

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
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

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

        try { Directory.Delete(dir, true); } catch { /* temp */ }
    }

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

}
