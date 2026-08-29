using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Offline regressions for cast extract membership.
/// Product code must not invent cast from scene headings / CAPS action verbs.
/// The old heuristic path would have force-added Kitchen/Backyard/Bounds — these tests fail if that returns.
/// </summary>
public sealed class CastExtractRegressionTests
{
    private static string GoldBusterDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CastExtractGold", "buster");
            if (Directory.Exists(dir)) return dir;
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CastExtractGold", "buster"));
        }
    }

    /// <summary>
    /// Fixture fountain has many INT./EXT. places + CAPS verbs (BOUNDS, SPIN).
    /// Model returns only real cast — product must not expand membership from the fountain.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_does_not_inject_slugline_places_or_stage_verbs()
    {
        var fountain = await File.ReadAllTextAsync(Path.Combine(GoldBusterDir, "screenplay.fountain"));
        var book = await File.ReadAllTextAsync(Path.Combine(GoldBusterDir, "book.txt"));

        // Model returns a clean closed cast only (what a good cast extract should produce).
        var modelJson = """
            {
              "schema_version": "cast_seeds.v1",
              "movie_title": "Buster",
              "render_style_lock": "STYLE LOCK: picture-book",
              "performance_lock": "PERFORMANCE LOCK: storybook",
              "character_seed_tokens": {
                "Character_Buster": {
                  "canonical_given_name": "Buster",
                  "description": "small black-and-white dog",
                  "visual_lock": "black and white short-haired dog",
                  "display_name_policy": "ok_anytime",
                  "species_kind": "animal"
                },
                "Character_Mom": {
                  "canonical_given_name": "Mom",
                  "description": "gentle adult woman",
                  "visual_lock": "adult woman caregiver",
                  "display_name_policy": "ok_anytime",
                  "species_kind": "human"
                },
                "Character_Narrator": {
                  "canonical_given_name": "Narrator",
                  "description": "storybook voice",
                  "visual_lock": "",
                  "display_name_policy": "never_on_screen",
                  "species_kind": "human"
                }
              }
            }
            """;

        var (result, keys, userPrompt, castSeeds, rules) = await RunExtractAsync(fountain, book, modelJson);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, keys.Count);
        Assert.Contains("black and white short-haired dog", castSeeds, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style_from_cast", rules, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"render_style_lock\"", castSeeds, StringComparison.Ordinal);
        Assert.Contains("Do NOT invent a film-level STYLE LOCK", userPrompt, StringComparison.Ordinal);
        Assert.Contains(keys, k => k.Equals("Character_Buster", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, k => k.Equals("Character_Mom", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, k => k.Equals("Character_Narrator", StringComparison.OrdinalIgnoreCase));

        // These appeared as staging in the real Buster fountain; product must never force them in.
        string[] forbidden =
        [
            "Kitchen", "Backyard", "Hallway", "Living", "Suburban", "Bounds", "Leaps",
            "Evening", "Day", "Spin", "Lapping", "Room", "Grass", "Fence",
        ];
        foreach (var bad in forbidden)
        {
            var hit = keys.FirstOrDefault(k =>
                k.Contains(bad, StringComparison.OrdinalIgnoreCase));
            Assert.True(hit is null,
                $"Product injected non-character cast key '{hit}' from slugline/action (fragment '{bad}'). " +
                $"Keys: {string.Join(", ", keys)}");
        }

        // No forced candidate list in the user message (heuristic reintroduction guard)
        Assert.DoesNotContain("DETECTED ON-SCREEN", userPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CRITICAL RULE: Check every entity", userPrompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the model under-casts (omits silent lead), product must not invent members from CAPS action.
    /// Old EnsureDiscoveredCastMembers would have force-added Buster/Daddy from the fountain.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_does_not_force_add_caps_action_names_when_model_omits_them()
    {
        const string fountain = """
            EXT. BACKYARD - DAY

            BUSTER bounds across the grass. DADDY waves.

            INT. KITCHEN - NIGHT

            MOM
            Dinner!
            """;

        // Model only returns Mom — incomplete but must not be "fixed" by name heuristics.
        var modelJson = """
            {
              "schema_version": "cast_seeds.v1",
              "movie_title": "Test",
              "character_seed_tokens": {
                "Character_Mom": {
                  "canonical_given_name": "Mom",
                  "description": "adult woman",
                  "visual_lock": "adult woman",
                  "display_name_policy": "ok_anytime",
                  "species_kind": "human"
                }
              }
            }
            """;

        var (result, keys, _, _, _) = await RunExtractAsync(fountain, book: null, modelJson);

        Assert.True(result.Ok, result.Error);
        Assert.Single(keys);
        Assert.Equal("Character_Mom", keys[0], ignoreCase: true);
        Assert.DoesNotContain(keys, k => k.Contains("Buster", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("Daddy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("Backyard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("Kitchen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("Bounds", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Membership after extract equals the model's seed keys (normalize may rename shape, not invent places).
    /// </summary>
    [Fact]
    public async Task ExtractAsync_membership_equals_model_seed_keys()
    {
        const string fountain = """
            INT. OFFICE - DAY
            HERO
            Hello.
            """;
        var modelJson = """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "canonical_given_name": "Hero",
                  "description": "young adult",
                  "visual_lock": "young adult",
                  "display_name_policy": "ok_anytime",
                  "species_kind": "human"
                }
              }
            }
            """;

        var (result, keys, _, _, _) = await RunExtractAsync(fountain, book: null, modelJson);
        Assert.True(result.Ok, result.Error);
        Assert.Single(keys);
        Assert.Equal("Character_Hero", keys[0], ignoreCase: true);
        Assert.DoesNotContain(keys, k => k.Contains("Office", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("Day", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Buster_gold_forbids_slugline_cast_fragments()
    {
        var metaPath = Path.Combine(GoldBusterDir, "expected_keys.json");
        Assert.True(File.Exists(metaPath), metaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var forbidden = doc.RootElement.GetProperty("forbidden_key_substrings").EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();

        // Must list place/verb fragments that the old pipeline force-added from this fountain
        foreach (var must in new[] { "Kitchen", "Backyard", "Hallway", "Bounds", "Suburban" })
        {
            Assert.True(
                forbidden.Any(f => f.Contains(must, StringComparison.OrdinalIgnoreCase)),
                $"buster expected_keys.json missing forbidden fragment covering '{must}' — " +
                "CI would not catch slugline cast regression.");
        }
    }

    private static async Task<(CastFromScreenplayService.ExtractResult Result, List<string> Keys, string UserPrompt, string CastSeeds, string Rules)>
        RunExtractAsync(string fountain, string? book, string modelJson)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ptm_cast_reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // Prompts required for LoadSystemPromptAsync / literalize (literalize fails open)
            var promptsSrc = FindRepoPromptsDir();
            Assert.True(promptsSrc is not null, "prompts/ not found");
            var promptsDst = Path.Combine(workspace, "prompts");
            Directory.CreateDirectory(promptsDst);
            foreach (var f in Directory.GetFiles(promptsSrc!))
                File.Copy(f, Path.Combine(promptsDst, Path.GetFileName(f)), overwrite: true);

            var opts = Options.Create(new PageToMovieOptions
            {
                WorkspaceRoot = workspace,
                EnableReadCaches = false,
            });
            var store = new ProjectStore(opts);
            var project = await store.CreateProjectAsync("CastReg");
            await OfflineTestModelConfig.ApplyAsync(store, project.Id);
            var dir = store.GetProjectDir(project.Id);
            Directory.CreateDirectory(Path.Combine(dir, "source"));
            await File.WriteAllTextAsync(Path.Combine(dir, "source", "screenplay.fountain"), fountain);
            if (!string.IsNullOrWhiteSpace(book))
                await File.WriteAllTextAsync(Path.Combine(dir, "source", "book_full.txt"), book);

            var chat = new ScriptedChatClient(modelJson);
            var literalize = new CastVisualLiteralizeService(
                store, chat, NullLogger<CastVisualLiteralizeService>.Instance);
            var learning = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);
            var rules = new ProjectRulesService(
                store, learning, NullLogger<ProjectRulesService>.Instance);
            var cast = new CastFromScreenplayService(
                store, chat, literalize, rules, NullLogger<CastFromScreenplayService>.Instance);

            var result = await cast.ExtractAsync(project.Id, force: true);
            var keys = result.CharacterKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var castPath = ScreenplayService.GetCastSeedsPath(store, project.Id);
            var castSeeds = File.Exists(castPath) ? await File.ReadAllTextAsync(castPath) : "";
            var rulesPath = Path.Combine(dir, "project_rules.json");
            var rules = File.Exists(rulesPath) ? await File.ReadAllTextAsync(rulesPath) : "";
            return (result, keys, chat.LastCastUserPrompt ?? "", castSeeds, rules);
        }
        finally
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* */ }
                }
                Directory.Delete(workspace, true);
            }
            catch { /* best effort */ }
        }
    }

    private static string? FindRepoPromptsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "prompts", "fountain_to_cast.txt");
            if (File.Exists(p))
                return Path.GetDirectoryName(p);
        }
        return null;
    }

    /// <summary>
    /// Returns fixed cast JSON for cast_from_screenplay; invalid JSON for literalize so it fails open.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly string _castJson;
        public string? LastCastUserPrompt { get; private set; }
        public bool IsConfigured => true;

        public ScriptedChatClient(string castJson) => _castJson = castJson;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            if (string.Equals(mode, ChatCallModes.CastFromScreenplay, StringComparison.OrdinalIgnoreCase) ||
                userPrompt.Contains("BEGIN FOUNTAIN", StringComparison.OrdinalIgnoreCase))
            {
                LastCastUserPrompt = userPrompt;
                return Task.FromResult(_castJson);
            }

            // Literalize: force fail-open (keep seeds) with non-JSON
            return Task.FromResult("not-json");
        }
    }
}
