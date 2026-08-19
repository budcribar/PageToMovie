using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Project-wide cast gate: every single-face character needs voice + locked image (voice-only and group/chorus: voice only)
/// before video gen to avoid wasted API spend.
/// </summary>
public class CastReadinessTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "CastGate";

    public CastReadinessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-cast-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
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
    public void Empty_cast_is_not_ready()
    {
        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(0, status.Total);
        Assert.False(status.ReadyForShots);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.NotEmpty(missing);
        Assert.Contains(missing, m => m.Contains("no cast seeds", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mary19 S02C05: a profile that names no sex/age ("Warm adult storytelling voice, even mid
    /// register") let the model re-cast the narrator per clip. Such a profile is not a voice lock — the
    /// cast is not ready, and the reason says what to add.</summary>
    [Fact]
    public void Voice_profile_without_sex_and_age_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": "Warm adult storytelling voice, even mid register, measured couplet cadence."
                }
              }
            }
            """);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("male/female", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Voice_only_with_voice_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": "Adult male, 50s, calm storyteller, mid pitch"
                }
              }
            }
            """);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(1, status.Total);
        Assert.Equal(1, status.Ready);
        Assert.True(status.ReadyForShots);
        Assert.Empty(status.Missing);
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Voice_only_without_voice_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": ""
                }
              }
            }
            """);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        Assert.Contains("Character_Narrator", status.Missing);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void On_screen_with_variant_but_no_lock_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "Adult female, 30s, warm mid pitch",
                  "description": "a hero"
                }
              }
            }
            """);

        // Unlocked draft variant only — HasPreferred true, Locked false
        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_variant_01.png"), new byte[128]);

        var rows = _store.ListCharacters(ProjectId);
        var hero = Assert.Single(rows);
        Assert.True(hero.HasPreferred);
        Assert.False(hero.Locked);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        Assert.Contains("Character_Hero", status.Missing);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("locked image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void On_screen_with_locked_image_and_voice_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "Adult female, 30s, warm mid pitch",
                  "description": "a hero"
                },
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": "Adult male, 60s, calm storyteller"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_ref.png"), new byte[128]);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Ready);
        Assert.True(status.ReadyForShots);
        Assert.Empty(status.Missing);
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void On_screen_locked_without_voice_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "",
                  "description": "a hero"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_ref.png"), new byte[128]);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Animal_on_screen_without_voice_is_ready_with_locked_image()
    {
        // Reproduces Mary4: the Lamb (species_kind=animal, voice_label set but no voice profile)
        // must NOT be blocked for a missing voice — only a locked image is required.
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Lamb": {
                  "canonical_given_name": "Lamb",
                  "species_kind": "animal",
                  "voice_label": "Lamb",
                  "voice_profile": "",
                  "description": "a small white lamb"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_lamb_ref.png"), new byte[128]);

        var lamb = Assert.Single(_store.ListCharacters(ProjectId));
        Assert.Equal("animal", lamb.SpeciesKind);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.DoesNotContain(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(missing); // locked image present → fully ready, no fake voice needed
    }

    [Fact]
    public void Animal_on_screen_without_lock_needs_image_not_voice()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Lamb": {
                  "species_kind": "animal",
                  "voice_label": "Lamb",
                  "voice_profile": "",
                  "description": "a small white lamb"
                }
              }
            }
            """);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("locked image", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gen_gate_exempts_group_but_still_requires_individual_lock()
    {
        // The per-scene video-gen gate (GetUnlockedOnScreenCharacters → EnsureSceneCharactersLocked)
        // must exempt a group/ensemble on-screen (Children — no single portrait) while still
        // requiring a locked image for an individual (Mary). Regression for the divergence where the
        // client readiness gate skipped groups but the gen gate blocked on them, erroring every batch
        // ("locked character refs required … Missing lock(s): Character_Children").
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Children": { "cast_kind": "group", "description": "Several schoolchildren", "voice_profile": "" },
                "Character_Mary": { "canonical_given_name": "Mary", "description": "Young girl", "voice_profile": "Girl, about 8, gentle" }
              }
            }
            """);
        WriteBlueprint("""
            { "scenes": [ { "scene_number": 1, "characters_on_screen": ["Character_Children", "Character_Mary"], "veo_clips": [] } ] }
            """);

        // Neither locked: the group is exempt, the individual is not.
        var unlocked = _store.GetUnlockedOnScreenCharacters(ProjectId, 1);
        Assert.Contains("Character_Mary", unlocked);
        Assert.DoesNotContain("Character_Children", unlocked);

        // Lock Mary → the scene gate clears entirely (the group is never counted).
        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_mary_ref.png"), new byte[128]);
        Assert.Empty(_store.GetUnlockedOnScreenCharacters(ProjectId, 1));
    }

    private void WriteBlueprint(string json)
    {
        var dir = _store.GetProjectDir(ProjectId);
        File.WriteAllText(Path.Combine(dir, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json"}""");
        File.WriteAllText(Path.Combine(dir, "blueprint.clips.grok.json"), json);
    }

    private void WriteSeeds(string json)
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "cast_seeds.json"), json);
    }

    private void WriteScreenplay(string fountain)
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "screenplay.fountain"), fountain);
    }

    private void WriteLambRef()
    {
        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_lamb_ref.png"), new byte[128]);
    }

    private const string LambAnimalSeeds = """
        {
          "schema_version": "cast_seeds.v1",
          "character_seed_tokens": {
            "Character_Lamb": {
              "canonical_given_name": "Lamb",
              "species_kind": "animal",
              "voice_profile": "",
              "description": "A small white lamb"
            }
          }
        }
        """;

    [Fact]
    public void Silent_animal_with_locked_image_needs_no_voice()
    {
        // The lamb never speaks a line → exempt from the voice requirement (locked image only).
        WriteSeeds(LambAnimalSeeds);
        WriteScreenplay("Title: Test\n\nINT. FIELD - DAY\n\nThe lamb grazes quietly in the sun.\n");
        WriteLambRef();

        var lamb = Assert.Single(_store.ListCharacters(ProjectId));
        Assert.False(lamb.Speaks);
        var status = _store.ReadCastStatus(ProjectId);
        Assert.True(status.ReadyForShots, string.Join("; ", status.Missing));
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Talking_animal_needs_a_voice_like_any_speaker()
    {
        // The lamb has a dialogue cue → it is a speaking role and must have a voice, animal or not.
        WriteSeeds(LambAnimalSeeds);
        WriteScreenplay("Title: Test\n\nINT. FIELD - DAY\n\nThe lamb turns to Mary.\n\nLAMB\nI am not afraid, Mary.\n");
        WriteLambRef();

        var lamb = Assert.Single(_store.ListCharacters(ProjectId));
        Assert.True(lamb.Speaks);
        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots); // now needs a voice
        Assert.Contains(status.Missing, m => m.Contains("Lamb", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_store.GetCastNotReadyForVideo(ProjectId),
            m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Talking_animal_with_voice_and_lock_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Lamb": {
                  "canonical_given_name": "Lamb",
                  "species_kind": "animal",
                  "voice_profile": "Young lamb, male, gentle voice",
                  "description": "A small talking lamb"
                }
              }
            }
            """);
        WriteScreenplay("Title: Test\n\nINT. FIELD - DAY\n\nThe lamb turns to Mary.\n\nLAMB\nI am not afraid, Mary.\n");
        WriteLambRef();

        var lamb = Assert.Single(_store.ListCharacters(ProjectId));
        Assert.True(lamb.Speaks);
        var status = _store.ReadCastStatus(ProjectId);
        Assert.True(status.ReadyForShots, string.Join("; ", status.Missing)); // voice + lock present
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Group_with_voice_no_locked_image_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Children": {
                  "canonical_given_name": "Children",
                  "cast_kind": "group",
                  "description": "A small group of school-age children in simple period play clothes.",
                  "voice_profile": "eager mixed children voices"
                },
                "Character_Mary": {
                  "canonical_given_name": "Mary",
                  "description": "Young girl with brown braids",
                  "voice_profile": "Girl, about 8, gentle"
                }
              }
            }
            """);

        // Mary still needs a locked face; Children (group) does not.
        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_mary_ref.png"), new byte[128]);

        var rows = _store.ListCharacters(ProjectId);
        var children = Assert.Single(rows, c => c.Key.Contains("Children", StringComparison.OrdinalIgnoreCase));
        Assert.True(children.IsGroup);
        Assert.False(children.Locked);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.True(status.ReadyForShots, string.Join("; ", status.Missing));
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Group_alone_is_ready_without_voice_or_portrait()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Children": {
                  "canonical_given_name": "Children",
                  "cast_kind": "group",
                  "description": "Several schoolchildren",
                  "voice_profile": ""
                }
              }
            }
            """);

        // Groups are not operator-pinned; a cast of only groups is "ready" so pin_characters
        // is not stuck — UI hides them and shows empty until real individuals exist.
        var status = _store.ReadCastStatus(ProjectId);
        Assert.True(status.ReadyForShots, string.Join("; ", status.Missing));
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Animal_with_locked_image_and_no_voice_is_ready_for_shots()
    {
        // Mary-had-a-little-lamb: the Lamb (species_kind=animal, locked image, no voice) must be
        // counted ready by the Scenes readiness gate, not just the video-spend gate. Regression for
        // the gate divergence where ReadCastStatus still demanded a voice the animal never needs.
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Lamb": {
                  "canonical_given_name": "Lamb",
                  "species_kind": "animal",
                  "voice_label": "Lamb",
                  "voice_profile": "",
                  "description": "A small white lamb"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_lamb_ref.png"), new byte[128]);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.True(status.ReadyForShots, string.Join("; ", status.Missing));
        Assert.DoesNotContain(status.Missing, m => m.Contains("Lamb", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Animal_without_locked_image_is_not_ready_but_needs_image_not_voice()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Lamb": {
                  "canonical_given_name": "Lamb",
                  "species_kind": "animal",
                  "voice_profile": "",
                  "description": "A small white lamb"
                }
              }
            }
            """);

        // No locked ref image → not ready. The blocker is the image, never a voice.
        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        Assert.Contains(status.Missing, m => m.Contains("Lamb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_store.GetCastNotReadyForVideo(ProjectId),
            m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

}
