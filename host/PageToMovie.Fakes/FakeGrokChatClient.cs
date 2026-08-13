using System.Text.RegularExpressions;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

using PageToMovie.Core.Utils;
namespace PageToMovie.Fakes;

/// <summary>
/// Deterministic chat stubs for offline / Playwright fakes mode.
/// Returns valid-looking Fountain, cast seeds, auto-review, and learning text.
/// </summary>
public sealed class FakeGrokChatClient : IChatClient
{
    private readonly ILogger<FakeGrokChatClient> _log;
    private readonly ProjectTelemetryService _telemetry;

    public FakeGrokChatClient(ILogger<FakeGrokChatClient> log, ProjectTelemetryService telemetry)
    {
        _log = log;
        _telemetry = telemetry;
    }

    public bool IsConfigured => true;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null)
    {
        _log.LogInformation(
            "Fake chat complete model={Model} mode={Mode} sysLen={Sys} userLen={User}",
            model, mode ?? "(none)", systemPrompt?.Length ?? 0, userPrompt?.Length ?? 0);

        var sys = systemPrompt ?? "";
        var user = userPrompt ?? "";
        var blob = sys + "\n" + user;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Fakes emit the same api_calls.jsonl telemetry as live clients so the feedback loop /
        // AI-Calls analytics page has data offline. Kind = the call mode when set, else "chat".
        var result = Respond(sys, user, mode);
        sw.Stop();
        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = string.IsNullOrWhiteSpace(mode) ? "chat" : mode,
                Mode = mode,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                PromptChars = blob.Length,
                ResponseChars = result.Length,
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }
        return result;
    }

    private static string Respond(string sys, string user, string? mode)
    {

        // ── Cast from screenplay → cast_seeds-shaped JSON ──────────────────
        if (sys.Contains("casting director", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("CLOSED CAST", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("fountain_to_cast", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("closed cast", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("character_seed_tokens", StringComparison.OrdinalIgnoreCase) ||
            (sys.Contains("character", StringComparison.OrdinalIgnoreCase) &&
             sys.Contains("seed", StringComparison.OrdinalIgnoreCase) &&
             !sys.Contains("literal", StringComparison.OrdinalIgnoreCase)))
        {
            // Poe fixture keeps its hand-authored cast; any other screenplay gets a cast generated
            // from its own character cues so speaking roles, species and groups line up with the
            // fountain (lets varied fixtures — animals, crowds, big/solo casts — drive the real UI).
            if (IsPoe(user)) return PoeCastJson;
            var generated = BuildCastJsonFromScreenplay(user);
            return (generated ?? DefaultCastJson);
        }

        // ── Auto-review / QC JSON ──────────────────────────────────────────
        if (sys.Contains("auto-review", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("auto_review", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("QC", StringComparison.Ordinal) ||
            user.Contains("visual_prompt", StringComparison.OrdinalIgnoreCase) &&
            user.Contains("clip", StringComparison.OrdinalIgnoreCase))
        {
            return (AutoReviewJson);
        }

        // ── Learning propose ───────────────────────────────────────────────
        if (sys.Contains("house rules", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("QC fail", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("Recent film QC fails", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "- Keep candlelight and deep shadows consistent across chamber clips.\n" +
                "- Match the Narrator's pale face and dark coat on every cut.\n" +
                "- Heartbeat tension: hold tight on floorboards before the confession scream.\n" +
                "- Prefer continuity from previous clip tail; flag jumps as fail when clear.");
        }

        // ── Visual literalize ──────────────────────────────────────────────
        if (sys.Contains("literal", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("figurative", StringComparison.OrdinalIgnoreCase) ||
            (sys.Contains("wardrobe", StringComparison.OrdinalIgnoreCase) &&
             sys.Contains("visual", StringComparison.OrdinalIgnoreCase)))
        {
            return (IsPoe(user) ? PoeCastJson : DefaultCastJson);
        }

        // ── Book → Fountain ────────────────────────────────────────────────
        if (sys.Contains("Fountain", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("--- PAGE", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("BEGIN BOOK", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("book_to_fountain", StringComparison.OrdinalIgnoreCase))
        {
            return (IsPoe(user) ? PoeFountain : DefaultFountain);
        }

        // ── Silent beat duration classes ───────────────────────────────────
        if (sys.Contains("DURATION BUDGETING", StringComparison.OrdinalIgnoreCase) ||
            mode == ChatCallModes.SilentBeatClassify)
        {
            return (BuildSilentBeatLabelsJson(user));
        }

        if (mode == ChatCallModes.AmbientSfxClassify ||
            sys.Contains("ambient bed vs SFX", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("ambient BED vs transient SFX", StringComparison.OrdinalIgnoreCase))
        {
            return (BuildIdLabels(user, id =>
                $$"""{"id":"{{id}}","ambient":"","sfx":""}"""));
        }

        if (mode == ChatCallModes.OnScreenCastClassify ||
            sys.Contains("ON CAMERA", StringComparison.OrdinalIgnoreCase))
        {
            return (BuildIdLabels(user, id =>
                $$"""{"id":"{{id}}","keys":[]}"""));
        }

        if (mode == ChatCallModes.ExtendCutClassify ||
            sys.Contains("hard_cut vs extend", StringComparison.OrdinalIgnoreCase))
        {
            return (BuildIdLabels(user, id =>
            {
                var cls = id.EndsWith("_b1") ? "hard_cut" : "extend";
                return $$"""{"id":"{{id}}","class":"{{cls}}"}""";
            }));
        }

        if (mode == ChatCallModes.SpeciesKindClassify ||
            sys.Contains("animal|human|other", StringComparison.OrdinalIgnoreCase))
        {
            // key-based payload
            var labels = new List<string>();
            foreach (Match m in CommonRegex.Matches(user, @"""key""\s*:\s*""([^""]+)"""))
            {
                var key = m.Groups[1].Value;
                var cls = key.Contains("Narrator", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Officer", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Man", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Mom", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Dad", StringComparison.OrdinalIgnoreCase)
                    ? "human"
                    : "other";
                labels.Add($$"""{"key":"{{key}}","class":"{{cls}}"}""");
            }
            return ("""{"labels":[""" + string.Join(",", labels) + "]}");
        }

        if (mode == ChatCallModes.PlateRankClassify ||
            sys.Contains("book image basenames", StringComparison.OrdinalIgnoreCase))
        {
            var names = CommonRegex.Matches(user, @"""([^""]+\.(?:png|jpe?g|webp))""", RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(n => "\"" + n.Replace("\\", "\\\\") + "\"")
                .ToList();
            return ("""{"ranked":[""" + string.Join(",", names) + "]}");
        }

        if (mode == ChatCallModes.ShotPlanRefineClassify ||
            sys.Contains("cinematographer refining shot plans", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"refinements":[]}""");
        }

        if (mode == ChatCallModes.BeatPacingClassify ||
            sys.Contains("duration pacing for screenplay beats", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"pacing":[]}""");
        }

        if (mode == ChatCallModes.CinematicLightingClassify ||
            sys.Contains("cinematographer and lighting director", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"lighting_token":"Chiaroscuro flickering candlelight with deep obsidian shadows and desaturated cool-gray volumetric fog"}""");
        }

        if (mode == ChatCallModes.CameraDirectorClassify ||
            sys.Contains("Virtuoso Film Director and Director of Photography", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"directives":[]}""");
        }

        if (mode == ChatCallModes.NegativePromptClassify ||
            sys.Contains("Period Visual Continuity Guard", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"negative_tokens":"no modern wristwatches, no electric light bulbs, no plastic, no zippers, no printed text"}""");
        }

        if (mode == ChatCallModes.WardrobeContinuityClassify ||
            sys.Contains("Costume Department Supervisor", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"wardrobe":[]}""");
        }

        if (mode == ChatCallModes.CharacterEmotionArcClassify ||
            sys.Contains("Acting Coach and Performance Director", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"emotions":[]}""");
        }

        if (mode == ChatCallModes.SoundDesignComposerClassify ||
            sys.Contains("Sound Designer and Audio Supervisor", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"sound_design":[]}""");
        }

        if (mode == ChatCallModes.DepthOfFieldClassify ||
            sys.Contains("Focus Puller and Optical Cinematographer", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"dof":[]}""");
        }

        if (mode == ChatCallModes.ColorPaletteGradingClassify ||
            sys.Contains("Master Colorist and Film Stock Director", StringComparison.OrdinalIgnoreCase))
        {
            return ("""{"film_stock":"Kodak Vision3 500T 5219 film stock, subtle 35mm grain","color_palette":"Desaturated cool-teal shadow tones with warm amber candle highlights","grading_prompt":"Color grading: Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights"}""");
        }

        // ── Minimal Stage1-shaped stub ─────────────────────────────────────
        return ("""
            {
              "schema_version": "stage1.v1",
              "movie_title": "Untitled",
              "global_production_variables": {
                "character_seed_tokens": {},
                "location_seed_tokens": {},
                "target_aspect_ratio": "16:9",
                "resolution": "480p",
                "frame_rate": 24,
                "total_runtime_target_seconds": 480
              },
              "scenes": []
            }
            """);
    }

    /// <summary>Echo beat ids with deterministic classes for fakes/CI (heuristic-shaped).</summary>
    private static string BuildSilentBeatLabelsJson(string user) =>
        BuildIdLabels(user, id =>
        {
            var cls = CommonRegex.IsMatch(id, @"_b1$") ? "establishing" : "action";
            return $$"""{"id":"{{id}}","class":"{{cls}}","reason":"fake"}""";
        });

    private static string BuildIdLabels(string user, Func<string, string> labelForId)
    {
        var labels = new List<string>();
        foreach (Match m in CommonRegex.Matches(user, @"""id""\s*:\s*""([^""]+)"""))
            labels.Add(labelForId(m.Groups[1].Value));
        return """{"labels":[""" + string.Join(",", labels) + "]}";
    }

    // Detect the Tell-Tale Heart fixture from the SCREENPLAY only (pass `user`, never sys+user):
    // the cast prompt template mentions "Old Man" and "eyes" as examples, so matching the combined
    // blob made every story look like Poe. Use specific title/plot markers, not generic words.
    private static bool IsPoe(string screenplay) =>
        screenplay.Contains("Tell-Tale", StringComparison.OrdinalIgnoreCase) ||
        screenplay.Contains("Edgar Allan Poe", StringComparison.OrdinalIgnoreCase) ||
        screenplay.Contains("hideous heart", StringComparison.OrdinalIgnoreCase) ||
        screenplay.Contains("vulture", StringComparison.OrdinalIgnoreCase);

    private const string PoeFountain = """
        Title: The Tell-Tale Heart
        Credit: Written by
        Author: Edgar Allan Poe (adaptation)
        Source: The Tell-Tale Heart
        Draft date: 7/19/2026

        FADE IN:

        INT. CHAMBER - NIGHT

        Candlelight. A pale NARRATOR faces us — too calm.

        NARRATOR
        True! Nervous — very, very dreadfully nervous I had been and am.
        But why will you say that I am mad?

        He leans closer. A floorboard creaks.

        NARRATOR (CONT'D)
        The disease had sharpened my senses — not destroyed them.
        Above all was the sense of hearing acute.

        INT. CHAMBER - NIGHT - LATER

        An OLD MAN sleeps behind a curtained bed. One pale blue eye glints.

        NARRATOR (V.O.)
        I loved the old man. He had never wronged me.
        I think it was his eye! Yes, it was this!

        The Narrator opens the door a crack. Lantern light crawls in.

        NARRATOR (V.O.)
        You should have seen how wisely I proceeded —
        with what caution — with what foresight.

        INT. CHAMBER - NIGHT - THE EIGHTH NIGHT

        The veiled eye opens. The Narrator's breath shakes.

        NARRATOR
        (whisper)
        It is the beating of his hideous heart!

        A single terrible moment. Then stillness. Planks. Dark wood floor.

        INT. CHAMBER - DAY

        Three OFFICERS sit over the very boards. Polite. Suspecting nothing.

        OFFICER
        A cry was heard in the night. We were obliged to investigate.

        NARRATOR
        The old man is away in the country. Search — search well.

        He smiles. The smile dies. A sound — soft at first — under the floor.

        NARRATOR (CONT'D)
        Villains! Dissemble no more! I admit the deed!
        Tear up the planks! Here, here! —
        It is the beating of his hideous heart!

        FADE OUT.

        THE END
        """;

    private const string DefaultFountain = """
        Title: Cinematic Short
        Credit: Written by
        Author: Test
        Source: Adapted from book
        Draft date: 1/1/2026

        INT. ROOM - NIGHT

        A figure waits in dim light.

        NARRATOR
        Once, in a quiet room, the story began.

        FADE OUT.

        THE END
        """;

    private const string PoeCastJson = """
        {
          "schema_version": "cast_seeds.v1",
          "movie_title": "The Tell-Tale Heart",
          "render_style_lock": "STYLE LOCK: photoreal live-action period drama circa 1840s; candlelight; naturalistic skin and fabric",
          "performance_lock": "PERFORMANCE LOCK: first-person confessional; when the Narrator speaks on camera he often addresses an implied listener/viewer; other characters are observed in the chamber rather than addressing the audience.",
          "character_seed_tokens": {
            "Character_Narrator": {
              "canonical_given_name": "Narrator",
              "species_kind": "human",
              "display_name_policy": "ok_anytime",
              "description": "Pale nervous adult man, mid-30s, thin gaunt face, dark shoulder-length hair, dark 1840s wool coat, white linen shirt, haunted open eyes, photoreal period drama",
              "visual_lock": "Always the same pale thin-faced adult man with dark hair and dark wool coat; distinct from the elderly Old Man; no modern clothing",
              "voice_profile": "Adult male, intimate, articulate, rising panic under calm diction; same on-camera and V.O.",
              "voice_label": "Narrator",
              "performance_notes": "Confessional speaker; on-camera dialogue often directed toward the implied listener/viewer rather than only at the Old Man.",
              "wardrobe_always": ["dark wool coat", "white linen shirt", "period trousers"],
              "reference_image_placeholder": "character_narrator_ref.png"
            },
            "Character_Old_Man": {
              "canonical_given_name": "Old Man",
              "species_kind": "human",
              "display_name_policy": "ok_anytime",
              "description": "Elderly frail man in pale nightshirt, sparse white-gray hair, one distinctive pale blue filmed eye that catches light, deeply lined face",
              "visual_lock": "Always the same frail elderly man with sparse white-gray hair and one pale blue eye; never the Narrator's younger face",
              "voice_profile": "No spoken dialogue on screen; silent if any breath is heard",
              "voice_label": "Old Man",
              "wardrobe_always": ["pale period nightshirt"],
              "reference_image_placeholder": "character_old_man_ref.png"
            },
            "Character_Officer": {
              "canonical_given_name": "Officer",
              "species_kind": "human",
              "display_name_policy": "ok_anytime",
              "description": "Adult man, solid build, neat short brown hair, clean-shaven, mid-19th-century dark wool constable coat with brass buttons, calm polite expression",
              "visual_lock": "Same neat brown-haired clean-shaven man in dark wool constable coat; composed official bearing",
              "voice_profile": "Adult male, medium pitch, polite official tone, moderate pace",
              "voice_label": "Officer",
              "wardrobe_always": ["dark wool constable coat with brass buttons", "dark trousers"],
              "reference_image_placeholder": "character_officer_ref.png"
            }
          },
          "location_seed_tokens": {
            "Loc_Old_Man_Chamber": {
              "display_name": "OLD MAN'S CHAMBER",
              "location_type": "INT",
              "description": "Cramped 1840s bedchamber: low plaster ceiling, dark wood floorboards, narrow sash window with heavy drapes, iron bedstead, washstand with ewer, single tallow candle casting long shadows; walls close and airless.",
              "visual_lock": "Always the same cramped candlelit chamber with iron bed, washstand, and heavy drapes; no modern fixtures.",
              "reference_image_placeholder": "loc_old_man_chamber_ref.png"
            }
          }
        }
        """;

    private const string DefaultCastJson = """
        {
          "schema_version": "cast_seeds.v1",
          "movie_title": "Untitled",
          "character_seed_tokens": {
            "Character_Narrator": {
              "canonical_given_name": "Narrator",
              "species_kind": "human",
              "display_name_policy": "ok_anytime",
              "description": "Adult human with clear face and period-appropriate clothing suitable for portrait lock",
              "visual_lock": "Same face, hair, and primary wardrobe in every scene",
              "voice_profile": "Adult clear voice, consistent every scene",
              "voice_label": "Narrator",
              "wardrobe_always": [],
              "reference_image_placeholder": "character_narrator_ref.png"
            }
          },
          "location_seed_tokens": {
            "Loc_Quiet_Room": {
              "display_name": "QUIET ROOM",
              "location_type": "INT",
              "description": "Simple interior room with soft ambient light, plain walls, and minimal furniture suitable for a test set lock.",
              "visual_lock": "Same quiet room geometry and soft light every scene.",
              "reference_image_placeholder": "loc_quiet_room_ref.png"
            }
          }
        }
        """;

    // ── Fake cast generation from a screenplay ──────────────────────────────────────────────
    // Test scaffolding only: unlike the real classifier (which reads the story), the fake uses small
    // keyword sets to decide species/group from character names so fixtures can deterministically
    // exercise the cast-display gates (talking vs silent animals, groups/ensembles, big/solo casts).

    private static readonly string[] AnimalWords =
    {
        "WOLF", "WOLVES", "BEAR", "CAT", "KITTEN", "DOG", "HOUND", "PUPPY", "LION", "TIGER", "FOX",
        "OWL", "CROW", "RAVEN", "LAMB", "SHEEP", "HORSE", "PONY", "MOUSE", "RAT", "SNAKE", "RABBIT",
        "HARE", "DEER", "MONKEY", "APE", "ELEPHANT", "PARROT", "TOAD", "FROG", "PIG", "GOAT",
        "DONKEY", "MULE", "CROCODILE", "PANTHER", "LEOPARD", "EAGLE", "HAWK", "SPIDER", "BEE",
        "SWAN", "GOOSE", "DUCK", "COW", "BULL", "CALF", "BADGER", "OTTER", "MOLE", "STOAT", "TORTOISE",
    };

    private static readonly string[] CreatureWords =
    {
        "GHOST", "SPIRIT", "DEMON", "MONSTER", "GOBLIN", "TROLL", "DRAGON", "WRAITH", "PHANTOM",
        "SHADE", "GOLEM", "ROBOT", "ANDROID", "ALIEN",
    };

    private static readonly string[] GroupWords =
    {
        "CROWD", "CHILDREN", "KIDS", "VILLAGERS", "MOB", "CLASSMATES", "GUESTS", "SOLDIERS",
        "SERVANTS", "PEASANTS", "TOWNSFOLK", "WORKERS", "STUDENTS", "MEN", "WOMEN", "NEIGHBORS",
        "SAILORS", "OFFICERS", "GUARDS", "CHORUS", "ANIMALS", "BIRDS", "WOLVES", "SHEEP",
        "CROWDS", "PEOPLE", "PASSENGERS", "REPORTERS", "FANS",
    };

    // Fountain keywords / transition tokens that look like cues but are not characters.
    private static readonly HashSet<string> CueStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INT", "EXT", "EST", "INT./EXT", "INT/EXT", "I/E", "FADE", "CUT", "DISSOLVE", "SMASH",
        "MATCH", "TO", "IN", "OUT", "BACK", "THE END", "TITLE", "SUPER", "INSERT", "MONTAGE",
        "CONTINUOUS", "LATER", "MOMENTS", "NIGHT", "DAY", "DAWN", "DUSK", "MORNING", "EVENING",
        "FADE IN", "FADE OUT", "CUT TO", "THE",
    };

    private static bool ContainsWord(string upperName, string[] words)
    {
        var tokens = CommonRegex.Split(upperName, "[^A-Z]+");
        return words.Any(w => Array.IndexOf(tokens, w) >= 0);
    }

    private static void AddCastName(List<string> order, Dictionary<string, bool> speaks, string name, bool spoken)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        if (!speaks.ContainsKey(name))
        {
            order.Add(name);
            speaks[name] = spoken;
        }
        else if (spoken)
        {
            speaks[name] = true;
        }
    }

    private static bool TryCollectCastNames(string[] lines, out List<string> order)
    {
        order = new List<string>();
        var speaks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool IsSceneOrTransition(string t) =>
            CommonRegex.IsMatch(t, @"^(INT|EXT|EST|INT\.?/EXT|I/E)[\. ]", RegexOptions.IgnoreCase)
            || t.EndsWith("TO:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("FADE", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("CUT", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;

            // Character cue: an UPPERCASE line (optionally with a "(V.O.)"-style extension) that is
            // preceded by a blank line and followed by dialogue (a non-blank next line).
            var cueMatch = CommonRegex.Match(t, @"^([A-Z][A-Z0-9 .'’\-]*?)(\s*\([^)]*\))?$");
            var prevBlank = i == 0 || lines[i - 1].Trim().Length == 0;
            var nextNonBlank = i + 1 < lines.Length && lines[i + 1].Trim().Length > 0;
            if (cueMatch.Success && prevBlank && nextNonBlank && !IsSceneOrTransition(t)
                && CommonRegex.IsMatch(t, "[A-Z]"))
            {
                var name = cueMatch.Groups[1].Value.Trim();
                if (!CueStopWords.Contains(name) && name.Length >= 2)
                {
                    AddCastName(order, speaks, name, spoken: true);
                    continue;
                }
            }

            // Action line: pick up inline UPPERCASE animal/creature/group names (e.g. a silent "LAMB")
            // so non-speaking cast still appear. Keyword-gated to avoid matching FADE/INT/etc.
            foreach (var w in CommonRegex.Matches(t, @"\b[A-Z][A-Z'’\-]{1,}\b").Select(m => m.Value))
            {
                if (CueStopWords.Contains(w)) continue;
                if (ContainsWord(w, AnimalWords) || ContainsWord(w, CreatureWords) || ContainsWord(w, GroupWords))
                    AddCastName(order, speaks, w, spoken: false);
            }
        }

        return order.Count > 0;
    }

    /// <summary>Parse character cues + inline-introduced animal/creature/group names from a fountain
    /// and emit cast_seeds JSON. Returns null when no characters are found (caller falls back).</summary>
    private static string? BuildCastJsonFromScreenplay(string screenplay)
    {
        if (string.IsNullOrWhiteSpace(screenplay)) return null;

        var lines = screenplay.Replace("\r\n", "\n").Split('\n');
        if (!TryCollectCastNames(lines, out var order))
            return null;

        static string TitleCase(string upper) =>
            string.Join(' ', upper.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"schema_version\": \"cast_seeds.v1\",");
        sb.AppendLine("  \"movie_title\": \"Fake Test Film\",");
        sb.AppendLine("  \"render_style_lock\": \"STYLE LOCK: photoreal test render\",");
        sb.AppendLine("  \"performance_lock\": \"PERFORMANCE LOCK: fake test cast\",");
        sb.AppendLine("  \"character_seed_tokens\": {");
        for (var idx = 0; idx < order.Count; idx++)
        {
            var name = order[idx];
            var upper = name.ToUpperInvariant();
            var species = ContainsWord(upper, AnimalWords) ? "animal"
                : ContainsWord(upper, CreatureWords) ? "creature" : "human";
            var isGroup = ContainsWord(upper, GroupWords);
            var display = TitleCase(name);
            var key = "Character_" + CommonRegex.Replace(display, @"\s+", "_");
            var castKind = isGroup ? "group" : "individual";
            sb.AppendLine("    \"" + key + "\": {");
            sb.AppendLine("      \"canonical_given_name\": \"" + display + "\",");
            sb.AppendLine("      \"species_kind\": \"" + species + "\",");
            sb.AppendLine("      \"cast_kind\": \"" + castKind + "\",");
            sb.AppendLine("      \"display_name_policy\": \"ok_anytime\",");
            sb.AppendLine("      \"description\": \"" + display + " — " + species
                + (isGroup ? " ensemble" : "") + ", photoreal test character\",");
            sb.AppendLine("      \"visual_lock\": \"Consistent " + display + " across scenes\",");
            sb.AppendLine("      \"voice_profile\": \"" + (species == "human" ? "Clear test voice" : "Non-human test voice") + "\",");
            sb.AppendLine("      \"voice_label\": \"" + display + "\",");
            sb.AppendLine("      \"wardrobe_always\": [],");
            sb.AppendLine("      \"reference_image_placeholder\": \"" + key.ToLowerInvariant() + "_ref.png\"");
            sb.AppendLine("    }" + (idx < order.Count - 1 ? "," : ""));
        }
        sb.AppendLine("  },");
        sb.AppendLine("  \"location_seed_tokens\": {");
        sb.AppendLine("    \"Loc_Test_Set\": {");
        sb.AppendLine("      \"display_name\": \"TEST SET\",");
        sb.AppendLine("      \"location_type\": \"INT\",");
        sb.AppendLine("      \"description\": \"Neutral interior test set with soft key light and plain walls\",");
        sb.AppendLine("      \"visual_lock\": \"Same neutral interior geometry every scene\",");
        sb.AppendLine("      \"reference_image_placeholder\": \"loc_test_set_ref.png\"");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private const string AutoReviewJson = """
        {
          "suggestion": "fail",
          "confidence": "medium",
          "continuity": "weak",
          "category": "continuity",
          "summary": "Slight jump in wardrobe and light between previous tail and this clip.",
          "edits": [
            {
              "field": "visual_prompt",
              "action": "append",
              "text": " Match wardrobe and candlelight from previous clip tail; same coat, same room shadows."
            }
          ]
        }
        """;
}
