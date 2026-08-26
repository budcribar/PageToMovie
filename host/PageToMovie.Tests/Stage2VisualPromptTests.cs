using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Fountain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

/// <summary>
/// Upstream visual_prompt quality: no "as described in the screenplay" stubs,
/// no mid-quote ellipsis from aggressive Stage 2 packing.
/// </summary>
public class Stage2VisualPromptTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public Stage2VisualPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-s2-vp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Fountain_import_does_not_use_as_described_in_screenplay_stubs()
    {
        var fountain = """
            Title: Stub Check

            INT. ROOM - DAY

            STEEL
            Hello.

            BRICK
            Hi.
            """;
        var parsed = FountainParser.Parse(fountain);
        var doc = FountainStage1Importer.BuildStage1(parsed);
        var gpv = Assert.IsType<Dictionary<string, object?>>(doc["global_production_variables"]);
        var chars = Assert.IsType<Dictionary<string, object?>>(gpv["character_seed_tokens"]);
        Assert.True(chars.Count >= 2);

        foreach (var (_, val) in chars)
        {
            var seed = Assert.IsType<Dictionary<string, object?>>(val);
            var desc = seed.TryGetValue("description", out var d) ? d?.ToString() ?? "" : "";
            var vlock = seed.TryGetValue("visual_lock", out var v) ? v?.ToString() ?? "" : "";
            Assert.DoesNotContain("as described in the screenplay", desc, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("as described in the screenplay", vlock, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("as cast for this production", vlock, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Narrator, as described in the screenplay.", true)]
    [InlineData("Narrator, as described in the scr…", true)]
    [InlineData("Match Steel as cast for this production.", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Steel (voice only; not on screen).", true)]
    [InlineData("Adult pale nervous man, dark wool coat, 1840s photoreal.", false)]
    public void IsPlaceholderIdentityText_detects_stubs(string text, bool expected)
    {
        Assert.Equal(expected, Stage2PlannerService.IsPlaceholderIdentityText(text));
    }

    [Theory]
    [InlineData("spoken_on_camera", true)]
    [InlineData("on_camera", true)]
    [InlineData("spoken", true)]
    [InlineData("voiceover_internal", false)]
    [InlineData("none", false)]
    public void On_camera_delivery_aliases(string delivery, bool onCam)
    {
        Assert.Equal(onCam, Stage2PlannerService.IsOnCameraDelivery(delivery));
        if (onCam)
            Assert.Equal("spoken_on_camera", Stage2PlannerService.NormalizeDelivery(delivery));
    }

    [Theory]
    [InlineData("Character_Narrator faces the lens.", "Character_Narrator", true)]
    [InlineData("A pale NARRATOR faces us in candlelight.", "Character_Narrator", true)]
    [InlineData("Candlelight. Empty room.", "Character_Narrator", false)]
    [InlineData("The Old Man sleeps.", "Character_Old_Man", true)]
    public void VisualMentionsSubject_avoids_awkward_prepend(string visual, string key, bool mentions)
    {
        Assert.Equal(mentions, Stage2PlannerService.VisualMentionsSubject(visual, key));
    }

    [Theory]
    [InlineData("He steadies his hands on his knees. A thin smile.", "Character_Narrator", "Narrator",
        "Narrator steadies his hands on his knees. A thin smile.")]
    [InlineData("She turns toward the door.", "Character_Mom", "Mom", "Mom turns toward the door.")]
    [InlineData("His eyes widen.", "Character_Hero", "Hero", "Hero's eyes widen.")]
    [InlineData("Candlelight fills the room.", "Character_Narrator", "Narrator",
        "Narrator Candlelight fills the room.")]
    [InlineData("Narrator leans forward.", "Character_Narrator", "Narrator", "Narrator leans forward.")]
    public void AttachPrimaryToVisual_uses_display_name_not_token_plus_pronoun(
        string visual, string key, string display, string expected)
    {
        var result = Stage2PlannerService.AttachPrimaryToVisual(visual, key, display);
        Assert.Equal(expected, result);
        Assert.DoesNotContain("Character_Narrator He", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Mom She", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NARRATOR (CONT'D)", null, "Narrator continues.")]
    [InlineData("NARRATOR", null, "Narrator speaks.")]
    [InlineData("NARRATOR", "whispering", "Narrator (whispering).")]
    [InlineData("OFFICER REYNOLDS (V.O.)", null, "Officer Reynolds speaks.")]
    public void BuildDialogueVisualEvent_strips_fountain_extensions(
        string rawCue, string? paren, string expected)
    {
        // Simulate importer: clean name then build visual
        var name = CommonRegex.Replace(rawCue, @"\s*\([^)]*\)\s*", " ").Trim();
        if (name.Length > 0 && name.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name = string.Join(' ', parts.Select(p =>
                p.Length <= 1 ? p.ToUpperInvariant()
                : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        }
        var visual = FountainStage1Importer.BuildDialogueVisualEvent(name, paren, rawCue);
        Assert.Equal(expected, visual);
        Assert.DoesNotContain("CONT'D", visual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(CONT", visual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("V.O", visual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStoryNegativePrompt_dedupes_and_omits_global()
    {
        var beat = new Dictionary<string, object?>
        {
            ["must_not"] = new List<object?> { "no watermarks", "no crowd extras", "no watermarks" },
        };
        var wardrobe = new Dictionary<string, List<string>>
        {
            ["Character_Hero"] = new List<string> { "coat" },
        };
        var neg = Stage2PlannerService.BuildStoryNegativePrompt(
            beat, wardrobe, new List<string> { "Character_Hero" });
        Assert.DoesNotContain("no legible text", neg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no watermarks", neg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no crowd extras", neg, StringComparison.OrdinalIgnoreCase);
        Assert.Single(CommonRegex.Matches(neg, "no watermarks", RegexOptions.IgnoreCase));
        Assert.Contains("no extra unmentioned hats", neg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClipBuilder_appends_global_and_story_negatives()
    {
        var clip = System.Text.Json.JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Hero walks. / 480p, 24fps",
              "characters_on_screen": ["Character_Hero"],
              "negative_prompt": "no crowd extras",
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Hero"] = new() { Key = "Character_Hero", DisplayName = "Hero", Description = "tall" },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Contains("<Negative>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no legible text", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no crowd extras", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // Resolution/fps are real, separate API request fields (GrokVideoClient), not prompt text —
        // the accidental "/ 480p, 24fps" in the input visual_prompt is stripped and nothing is
        // re-appended in its place.
        Assert.Empty(CommonRegex.Matches(built.Prompt, @"/\s*\d+p\s*,\s*\d+fps", RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task Stage2_visual_prompts_omit_resolution_fps_suffix()
    {
        const string projectId = "Demo";
        var fountain = """
            Title: Res Check

            INT. ROOM - DAY

            HERO
            Hello world.
            """;
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "720p", scenes: "all");
        Assert.True(result.Ok);
        Assert.True(File.Exists(result.OutPath));

        var bp = await File.ReadAllTextAsync(result.OutPath!);
        Assert.DoesNotContain("24fps", bp, StringComparison.OrdinalIgnoreCase);
        // visual_prompt values should not end with bare /720p technical suffix
        using var doc = System.Text.Json.JsonDocument.Parse(bp);
        var anyClip = false;
        void Walk(System.Text.Json.JsonElement el)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (el.TryGetProperty("visual_prompt", out var vp))
                {
                    anyClip = true;
                    var text = vp.GetString() ?? "";
                    Assert.DoesNotContain("24fps", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotMatch(new Regex(@"/\s*\d{3,4}p\s*$", RegexOptions.IgnoreCase, CommonRegex.Timeout), text);
                }
                foreach (var p in el.EnumerateObject())
                    Walk(p.Value);
            }
            else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var i in el.EnumerateArray())
                    Walk(i);
            }
        }
        Walk(doc.RootElement);
        Assert.True(anyClip);
    }

    [Fact]
    public async Task Stage2_visual_prompts_omit_as_described_stubs_and_keep_full_dialogue()
    {
        const string projectId = "Demo";
        var longLine =
            "True! Nervous - very, very dreadfully nervous I had been and am. " +
            "But why will you say that I am mad? The disease had sharpened my senses.";
        var fountain = $"""
            Title: Prompt Quality

            INT. CHAMBER - NIGHT

            NARRATOR
            {longLine}

            The narrator leans closer. A floorboard creaks.
            """;
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok);
        Assert.True(File.Exists(result.OutPath));

        var bp = await File.ReadAllTextAsync(result.OutPath!);
        Assert.DoesNotContain("as described in the screenplay", bp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("as described in the scr", bp, StringComparison.OrdinalIgnoreCase);

        // Full dialogue should appear in audio_payload and not be mid-cut in visual speech with "say t…"
        Assert.Contains("dreadfully nervous", bp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("say t…", bp, StringComparison.Ordinal);
        Assert.DoesNotContain("say t\u2026", bp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage2_with_real_visual_lock_embeds_usable_identity_not_stub()
    {
        const string projectId = "Demo";
        var fountain = """
            Title: Real Lock

            INT. LAB - DAY

            SCIENTIST
            Almost there.
            """;
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        Assert.True(ScreenplayService.SignOff(_store, projectId).Ok);

        // After sign-off, inject a real cast seed with a proper visual lock
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "cast_seeds.json"), """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Scientist": {
                  "description": "Middle-aged woman with wire glasses and a white lab coat",
                  "visual_lock": "Always the same middle-aged woman with wire glasses and white lab coat; identity fixed.",
                  "voice_profile": "Calm precise alto",
                  "canonical_given_name": "Scientist",
                  "display_name_policy": "ok_anytime"
                }
              }
            }
            """);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "720p", scenes: "all");
        Assert.True(result.Ok);
        var bp = await File.ReadAllTextAsync(result.OutPath!);
        Assert.DoesNotContain("as described", bp, StringComparison.OrdinalIgnoreCase);
        // Real lock prose may appear in visual_prompt identity cues
        Assert.Contains("wire glasses", bp, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NARRATOR", null, false)]
    [InlineData("NARRATOR", "(V.O.)", true)]
    [InlineData("NARRATOR", "V.O.", true)]
    [InlineData("NARRATOR", "(V.O.) (CONT'D)", true)]
    [InlineData("NARRATOR", "(O.S.)", true)]
    [InlineData("HERO", "(CONT'D)", false)]
    [InlineData("NARRATOR (V.O.)", null, true)]
    public void IsOffScreenCue_reads_meta_not_only_bare_name(string name, string? meta, bool expected)
    {
        Assert.Equal(expected, FountainStage1Importer.IsOffScreenCue(name, meta));
    }

    [Fact]
    public void BuildStage1_vo_cue_sets_voiceover_internal_not_lip_sync_visual()
    {
        var fountain = """
            Title: VO Import

            INT. BEDCHAMBER - NIGHT

            THE OLD MAN sleeps under heavy covers.

            NARRATOR (V.O.)
            It took me an hour to place my whole head within the opening.

            NARRATOR
            And now I speak on camera in another beat.
            """;
        var doc = Stage1Normalizer.Normalize(
            FountainStage1Importer.BuildStage1(FountainParser.Parse(fountain)));
        var scenes = Assert.IsType<List<object?>>(doc["scenes"]);
        Assert.NotEmpty(scenes);
        var scene = Assert.IsType<Dictionary<string, object?>>(scenes[0]);
        var beats = Assert.IsType<List<object?>>(scene["story_beats"]);
        var dicts = beats.OfType<Dictionary<string, object?>>().ToList();

        var vo = dicts.First(b =>
            (b.TryGetValue("dialogue", out var d) ? d?.ToString() : null)?.Contains("hour") == true);
        Assert.Equal("voiceover_internal", vo["delivery"]?.ToString());
        var voAudio = Assert.IsType<Dictionary<string, object?>>(vo["audio"]);
        Assert.Equal("voiceover_internal", voAudio["delivery"]?.ToString());
        var voVisual = vo["visual_event"]?.ToString() ?? "";
        Assert.Contains("OLD MAN", voVisual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("speaks", voVisual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lip-sync", voVisual, StringComparison.OrdinalIgnoreCase);

        var onCam = dicts.First(b =>
            (b.TryGetValue("dialogue", out var d) ? d?.ToString() : null)?.Contains("on camera") == true);
        Assert.Equal("spoken_on_camera", onCam["delivery"]?.ToString());
        Assert.Contains("speaks", onCam["visual_event"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocationLockPhrase_prefers_scene_setting_time_of_day()
    {
        var sceneDay = new Dictionary<string, object?>
        {
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - DAY",
            ["primary_location_id"] = "Loc_Old_Mans_Bedchamber",
        };
        var sceneNight = new Dictionary<string, object?>
        {
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - NIGHT",
            ["primary_location_id"] = "Loc_Old_Mans_Bedchamber",
        };
        var beat = new Dictionary<string, object?>
        {
            ["location_id"] = "Loc_Old_Mans_Bedchamber",
        };
        // Seed frozen on first DAY visit (legacy poison) — plan must still use current heading
        var seeds = new Dictionary<string, object?>
        {
            ["Loc_Old_Mans_Bedchamber"] = new Dictionary<string, object?>
            {
                ["visual_lock"] = "INT. OLD MAN'S BEDCHAMBER - DAY",
                ["description"] = "INT. OLD MAN'S BEDCHAMBER - DAY",
            },
        };

        Assert.Equal(
            "INT. OLD MAN'S BEDCHAMBER - DAY",
            Stage2PlannerService.LocationLockPhrase(sceneDay, beat, seeds));
        Assert.Equal(
            "INT. OLD MAN'S BEDCHAMBER - NIGHT",
            Stage2PlannerService.LocationLockPhrase(sceneNight, beat, seeds));
        Assert.True(Stage2PlannerService.LooksLikeSceneHeading("INT. ROOM - NIGHT"));
        Assert.False(Stage2PlannerService.LooksLikeSceneHeading("OLD MAN'S BEDCHAMBER"));
    }

    [Fact]
    public async Task Stage2_night_scene_prompt_keeps_night_not_day_from_shared_loc()
    {
        const string projectId = "Demo";
        var fountain = """
            Title: TOD Check

            INT. OLD MAN'S BEDCHAMBER - DAY

            THE OLD MAN sits by the window.

            NARRATOR (V.O.)
            I saw his eye by day.

            INT. OLD MAN'S BEDCHAMBER - NIGHT

            Pitch black. A thin ray finds the pillow.

            NARRATOR (V.O.)
            And at midnight the eye was closed.
            """;
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        Assert.True(ScreenplayService.SignOff(_store, projectId).Ok);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok);

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.OutPath!));
        var scenes = doc.RootElement.GetProperty("scenes");
        Assert.True(scenes.GetArrayLength() >= 2);

        string? nightPrompt = null;
        string? nightDelivery = null;
        foreach (var scene in scenes.EnumerateArray())
        {
            var setting = scene.GetProperty("setting").GetString() ?? "";
            if (!setting.Contains("NIGHT", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var clip in scene.GetProperty("veo_clips").EnumerateArray())
            {
                var vp = clip.GetProperty("visual_prompt").GetString() ?? "";
                if (vp.Contains("BEDCHAMBER", StringComparison.OrdinalIgnoreCase) ||
                    vp.Contains("Pitch black", StringComparison.OrdinalIgnoreCase))
                {
                    nightPrompt = vp;
                    nightDelivery = clip.GetProperty("audio_payload").GetProperty("delivery").GetString();
                    if (nightDelivery == "voiceover_internal")
                        break;
                }
            }
            if (nightDelivery == "voiceover_internal")
                break;
        }

        Assert.NotNull(nightPrompt);
        Assert.Contains("NIGHT", nightPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEDCHAMBER - DAY", nightPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("voiceover_internal", nightDelivery);
        // The delivery is the plan's only statement about how the line is spoken. The line itself
        // is not restated in the visual prompt: audio_payload carries it, and the AUDIO block
        // built at generation time is the single copy the model gets.
        Assert.DoesNotContain("ON CAMERA lip-syncs", nightPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFF-CAMERA VOICEOVER", nightPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ── Bug regression tests ────────────────────────────────────────────────

    /// <summary>
    /// Bug 1 regression: STYLE LOCK must appear in visual_prompt for animal-only scenes.
    /// Previously the fallback only fired when the cast contained "mom", "dad", or "human",
    /// so a backyard scene with only Character_Buster (a dog) + a V.O.-only Narrator got
    /// no style lock and rendered in a different visual style from all subsequent scenes.
    /// </summary>
    [Fact]
    public async Task StyleLock_included_for_animal_only_scene()
    {
        const string projectId = "Demo";

        // Fountain: Narrator is V.O. only; Buster is the visual lead (animal).
        // BUSTER must have an on-camera line BEFORE the narrator V.O. so Stage1's
        // CurrentOnScreen includes him in the pictureCast for the narrator beat.
        const string fountain = """
            Title: Buster StyleLock Regression

            EXT. SUBURBAN BACKYARD - DAY

            BUSTER
            (barks excitedly)
            Woof!

            NARRATOR (V.O.)
            He's Buster the Noodle Head Dog.
            """;

        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, fountain);
        Assert.True(ScreenplayService.SignOff(_store, projectId).Ok);

        // Inject cast_seeds.json: Narrator = never_on_screen, Buster = ok_anytime animal
        var sourceDir = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "cast_seeds.json"), """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "canonical_given_name": "Narrator",
                  "display_name_policy": "never_on_screen",
                  "description": "Narrator (voice only; not on screen).",
                  "voice_profile": "Warm adult storyteller."
                },
                "Character_Buster": {
                  "canonical_given_name": "Buster",
                  "display_name_policy": "ok_anytime",
                  "description": "Small black-and-white dog.",
                  "visual_lock": "Always the same small dog.",
                  "species_kind": "animal"
                }
              }
            }
            """);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok, "PlanAsync failed");
        Assert.True(File.Exists(result.OutPath));

        var bp = await File.ReadAllTextAsync(result.OutPath!);
        using var doc = System.Text.Json.JsonDocument.Parse(bp);

        // Every clip in the animal-only Scene 1 must carry a style lock. Stage 2 emits it as a
        // <StyleLock> tag now rather than a "STYLE LOCK:" prose label.
        var foundStyleLock = false;
        foreach (var scene in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            foreach (var clip in scene.GetProperty("veo_clips").EnumerateArray())
            {
                var vp = clip.GetProperty("visual_prompt").GetString() ?? "";
                if (vp.Contains($"<{PromptFieldTags.StyleLock}>", StringComparison.OrdinalIgnoreCase))
                    foundStyleLock = true;
            }
        }
        Assert.True(foundStyleLock,
            $"Expected at least one clip to carry a <{PromptFieldTags.StyleLock}> block in its " +
            "visual_prompt, but none did. Animal-only scenes were not getting a style lock injected.");
    }

    /// <summary>
    /// Bug 2 regression: ClipCastTokens must not short-circuit and drop visible characters
    /// when characters_on_screen only lists a never_on_screen (V.O.) character.
    /// Previously it returned immediately after finding the Narrator, silently dropping
    /// Buster who was described in the visual_event prose — so no reference portrait was
    /// attached for the on-screen animal lead.
    /// </summary>
    [Fact]
    public void ClipCastTokens_finds_visible_character_in_prose_when_cos_lists_only_narrator()
    {
        var charSeeds = new Dictionary<string, object?>
        {
            ["Character_Narrator"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "never_on_screen",
                ["canonical_given_name"] = "Narrator",
            },
            ["Character_Buster"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "Buster",
            },
        };

        // Only the V.O. narrator is in characters_on_screen; Buster is in the action prose
        var beat = new Dictionary<string, object?>
        {
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
            ["visual_event"] = "Character_Buster bounds across the grass, leaping like a frog.",
            ["primary_subject"] = (object?)null,
            ["speaker"] = "Character_Narrator",
        };

        var scene = new Dictionary<string, object?>
        {
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
            ["story_beats"] = new List<object?> { beat },
        };

        var cast = Stage2PlannerService.ClipCastTokensPublic(scene, beat, charSeeds);

        // Buster must appear — was silently dropped before the bug fix
        Assert.Contains("Character_Buster", cast, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClipCastTokens_includes_all_characters_in_visual_shot_even_if_stage1_only_listed_one()
    {
        var charSeeds = new Dictionary<string, object?>
        {
            ["Character_The_Lamb"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "The Lamb",
            },
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "Mary",
            },
            ["Character_Narrator"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "never_on_screen",
                ["canonical_given_name"] = "Narrator",
            },
        };

        // Stage 1 initially classified only Character_The_Lamb on-screen, but the visual shot describes Mary alongside the lamb
        var beat = new Dictionary<string, object?>
        {
            ["characters_on_screen"] = new List<object?> { "Character_The_Lamb" },
            ["visual_event"] = "The lamb's white wash brightens on the lane. Medium two-shot tracking Mary and the lamb side by side.",
            ["primary_subject"] = (object?)null,
            ["speaker"] = "Character_Narrator",
        };

        var scene = new Dictionary<string, object?>
        {
            ["characters_on_screen"] = new List<object?> { "Character_Mary", "Character_The_Lamb" },
            ["story_beats"] = new List<object?> { beat },
        };

        var cast = Stage2PlannerService.ClipCastTokensPublic(scene, beat, charSeeds);

        // Both Mary and The Lamb must be included so both locked reference plates are attached
        Assert.Contains("Character_Mary", cast, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Character_The_Lamb", cast, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureEndCreditsScene_appends_credits_scene_when_missing()
    {
        var scenes = new List<Dictionary<string, object?>>
        {
            new() { ["scene_number"] = 1, ["scene_heading"] = "INT. ROOM - DAY" },
            new() { ["scene_number"] = 2, ["scene_heading"] = "EXT. PARK - DAY" },
        };

        Stage2PlannerService.EnsureEndCreditsScene(scenes);

        Assert.Equal(3, scenes.Count);
        var credits = scenes.Last();
        Assert.Equal(3, credits["scene_number"]);
        Assert.Equal("FADE OUT. END CREDITS", credits["scene_heading"]);
        Assert.True((bool)credits["is_credits"]!);
    }

    [Fact]
    public void EnsureEndCreditsScene_uses_supplied_credits_prompt()
    {
        var scenes = new List<Dictionary<string, object?>>
        {
            new() { ["scene_number"] = 1, ["scene_heading"] = "INT. ROOM - DAY" },
        };

        Stage2PlannerService.EnsureEndCreditsScene(scenes, "SHARED CREDITS CARD · pagetomovie.com");

        var credits = scenes.Last();
        var clip = ((List<object?>)credits["veo_clips"]!).Cast<Dictionary<string, object?>>().First();
        Assert.Equal("SHARED CREDITS CARD · pagetomovie.com", clip["visual_prompt"]);
    }
}
