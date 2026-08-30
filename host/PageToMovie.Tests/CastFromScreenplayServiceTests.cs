using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CastFromScreenplayServiceTests
{
    [Fact]
    public async Task Prompt_file_exists_and_mentions_silent_cast()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true);
            return;
        }

        var text = await CastFromScreenplayService.LoadSystemPromptAsync(root);
        Assert.Contains("cast_seeds", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silent", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BOOK-FIRST", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("performance_lock", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUDIENCE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cast_kind", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wardrobe_always", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no clothes", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Character_Children", "Children", null, null, "group")]
    [InlineData("Character_Mary", "Mary", null, null, "individual")]
    [InlineData("Character_X", "X", "chorus", null, "group")]
    [InlineData("Character_Kids", "Kids", "individual", null, "individual")]
    public void ResolveCastKind_normalizes_model_and_heuristics(
        string key, string display, string? modelKind, string? desc, string expected)
    {
        Assert.Equal(expected, CastFromScreenplayService.ResolveCastKind(key, display, modelKind, desc));
    }

    [Fact]
    public async Task Visual_literalize_prompt_exists_and_targets_figurative_language()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true);
            return;
        }

        var text = await CastVisualLiteralizeService.LoadSystemPromptAsync(root);
        Assert.Contains("figurative", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("literal", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", text, StringComparison.OrdinalIgnoreCase);
        // Base-look vs later wardrobe (general, not book-specific lists)
        Assert.Contains("later", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BASE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wardrobe", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Narrator, as described in the screenplay.", true)]
    [InlineData("as in the screenplay", true)]
    [InlineData("Match Bob consistently across scenes.", true)]
    [InlineData("short", true)]
    [InlineData("Pale nervous adult man, mid-30s, thin face, dark wool coat, 1840s photoreal.", false)]
    public void IsStubLook_detects_placeholders(string text, bool expected)
    {
        Assert.Equal(expected, CastFromScreenplayService.IsStubLook(text));
    }

    [Fact]
    public void SelectTextForPrompt_keeps_short_books_whole()
    {
        var book = "Once upon a time there was a pale man and an old man with a vulture eye.";
        var selected = CastFromScreenplayService.SelectTextForPrompt(book, 100_000);
        Assert.Equal(book, selected);
    }

    [Fact]
    public void SelectTextForPrompt_samples_long_books_with_spine_windows()
    {
        var head = new string('A', 50_000);
        var mid = new string('B', 50_000);
        var tail = new string('C', 50_000);
        var book = head + mid + tail;
        var selected = CastFromScreenplayService.SelectTextForPrompt(book, 40_000);
        Assert.True(selected.Length <= 45_000);
        Assert.Contains('A', selected);
        Assert.Contains('C', selected);
        Assert.Contains("sampled for length", selected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnrichStubLooksFromSources_fills_model_seed_only_does_not_add_cast()
    {
        // Model already chose Buster + Mom; Buster has a stub look — enrich from book.
        var seeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mom"] = new Dictionary<string, object?>
            {
                ["canonical_given_name"] = "Mom",
                ["description"] = "Adult woman, gentle smile, soft brown hair, cardigan.",
                ["visual_lock"] = "Same soft brown hair and cardigan every scene.",
                ["display_name_policy"] = "ok_anytime",
            },
            ["Character_Buster"] = new Dictionary<string, object?>
            {
                ["canonical_given_name"] = "Buster",
                ["description"] = "as described in the screenplay",
                ["visual_lock"] = "",
                ["display_name_policy"] = "ok_anytime",
            },
        };
        var book = """
            He's Buster the Noodle Head Dog.

            He's small, black, and white with floppy ears and a soft rounded head.

            When Momma says bed time he wants to rest his furry head.
            """;
        var beforeKeys = seeds.Keys.OrderBy(k => k).ToList();
        var n = CastFromScreenplayService.EnrichStubLooksFromSources(seeds, book, fountainText: null);
        Assert.True(n >= 1);
        Assert.Equal(beforeKeys, seeds.Keys.OrderBy(k => k).ToList());
        var buster = (Dictionary<string, object?>)seeds["Character_Buster"]!;
        var desc = buster["description"]?.ToString() ?? "";
        Assert.False(CastFromScreenplayService.IsStubLook(desc));
        Assert.True(
            desc.Contains("black", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Buster", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("floppy", StringComparison.OrdinalIgnoreCase),
            "expected look text from book; got: " + desc);
        // Must not invent kitchen/backyard cast
        Assert.DoesNotContain(seeds.Keys, k => k.Contains("Kitchen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seeds.Keys, k => k.Contains("Backyard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnrichStubLooksFromSources_does_not_add_missing_heroes()
    {
        // Model forgot Buster — we do NOT invent him via heuristics.
        var seeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mom"] = new Dictionary<string, object?>
            {
                ["canonical_given_name"] = "Mom",
                ["description"] = "Adult woman.",
                ["display_name_policy"] = "ok_anytime",
            },
        };
        var book = "He's Buster the Noodle Head Dog. Small black and white dog.";
        var n = CastFromScreenplayService.EnrichStubLooksFromSources(seeds, book, null);
        Assert.Equal(0, n);
        Assert.Single(seeds);
        Assert.DoesNotContain(seeds.Keys, k => k.Contains("Buster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeCastDoc_links_age_variant_to_base_via_variant_of()
    {
        var parsed = new Dictionary<string, object?>
        {
            ["movie_title"] = "Nick and Me",
            ["character_seed_tokens"] = new Dictionary<string, object?>
            {
                ["Character_Nick"] = new Dictionary<string, object?>
                {
                    ["canonical_given_name"] = "Nick",
                    ["description"] = "Adult man, mid-20s, reddish-brown messy hair, early scars on chin.",
                    ["visual_lock"] = "Same scarred chin and reddish-brown hair every scene.",
                    ["display_name_policy"] = "ok_anytime",
                },
                ["Character_Young_Nick"] = new Dictionary<string, object?>
                {
                    ["canonical_given_name"] = "Young Nick",
                    ["description"] = "Boy, about 12, sturdy preteen build, same reddish-brown hair.",
                    ["visual_lock"] = "Same reddish-brown hair, preteen build every scene.",
                    ["display_name_policy"] = "ok_anytime",
                    ["age_band"] = "child_8_9",
                    ["variant_of"] = "Character_Nick",
                },
            },
        };

        var normalized = CastFromScreenplayService.NormalizeCastDoc(parsed, "budcribar/NickAndMe");
        var seeds = (Dictionary<string, object?>)normalized["character_seed_tokens"]!;

        Assert.True(seeds.ContainsKey("Character_Nick"));
        Assert.True(seeds.ContainsKey("Character_Young_Nick"));

        var baseSeed = (Dictionary<string, object?>)seeds["Character_Nick"]!;
        Assert.False(baseSeed.ContainsKey("age_band"));
        Assert.False(baseSeed.ContainsKey("variant_of"));

        var variantSeed = (Dictionary<string, object?>)seeds["Character_Young_Nick"]!;
        Assert.Equal("child_8_9", variantSeed["age_band"]);
        Assert.Equal("Character_Nick", variantSeed["variant_of"]);
    }

    [Fact]
    public void NormalizeCastDoc_drops_variant_of_pointing_at_a_nonexistent_seed()
    {
        var parsed = new Dictionary<string, object?>
        {
            ["character_seed_tokens"] = new Dictionary<string, object?>
            {
                ["Character_Young_Nick"] = new Dictionary<string, object?>
                {
                    ["canonical_given_name"] = "Young Nick",
                    ["description"] = "Boy, about 12.",
                    ["display_name_policy"] = "ok_anytime",
                    ["variant_of"] = "Character_Nick", // base seed never emitted — dangling pointer
                },
            },
        };

        var normalized = CastFromScreenplayService.NormalizeCastDoc(parsed, "budcribar/NickAndMe");
        var seeds = (Dictionary<string, object?>)normalized["character_seed_tokens"]!;
        var variantSeed = (Dictionary<string, object?>)seeds["Character_Young_Nick"]!;
        Assert.False(variantSeed.ContainsKey("variant_of"));
    }

    [Fact]
    public void NameToCharacterKey_pascalizes()
    {
        Assert.Equal("Character_Buster", CastFromScreenplayService.NameToCharacterKey("BUSTER"));
        Assert.Equal("Character_Buster", CastFromScreenplayService.NameToCharacterKey("Buster the Dog"));
        Assert.Equal("Character_Bob_Cratchit", CastFromScreenplayService.NameToCharacterKey("BOB CRATCHIT"));
        Assert.Equal("Character_Queen_Of_Hearts", CastFromScreenplayService.NameToCharacterKey("QUEEN OF HEARTS"));
    }

    [Fact]
    public void SelectBookTextForCastPrompt_without_hints_uses_spine_only()
    {
        var early = string.Join("\n\n", Enumerable.Range(0, 120).Select(i =>
            $"Chapter filler {i}. " + new string('x', 1_200)));
        var lateLook =
            "\n\nZara stepped into the firelight. She had silver hair and a green velvet coat with brass buttons.\n\n";
        var after = string.Join("\n\n", Enumerable.Range(0, 40).Select(i =>
            $"Epilogue pad {i}. " + new string('y', 600)));
        var book = early + lateLook + after;
        Assert.True(book.Length > CastFromScreenplayService.BookPromptChars);

        // Production cast prompt path: no name-list guessing.
        var selected = CastFromScreenplayService.SelectBookTextForCastPrompt(
            book, maxChars: 40_000, nameHints: null);
        Assert.Contains("NARRATIVE SPINE", selected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOOK EXCERPTS", selected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spine only", selected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBookTextForCastPrompt_with_model_names_can_pull_late_looks()
    {
        // After model chooses Zara, look harvest may use her name — not cast inventing.
        var early = string.Join("\n\n", Enumerable.Range(0, 120).Select(i =>
            $"Chapter filler {i}. " + new string('x', 1_200)));
        var lateLook =
            "\n\nZara stepped into the firelight. She had silver hair and a green velvet coat with brass buttons.\n\n";
        var after = string.Join("\n\n", Enumerable.Range(0, 40).Select(i =>
            $"Epilogue pad {i}. " + new string('y', 600)));
        var book = early + lateLook + after;

        var selected = CastFromScreenplayService.SelectBookTextForCastPrompt(
            book, maxChars: 40_000, nameHints: new[] { "Zara" });

        Assert.Contains("silver hair", selected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("green velvet", selected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOOK EXCERPTS", selected, StringComparison.OrdinalIgnoreCase);
        var headOnly = book[..40_000];
        Assert.DoesNotContain("silver hair", headOnly, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HarvestNameLookExcerpts_prefers_appearance_language()
    {
        var book = """
            Bob walked down the street and said nothing interesting for a long while about weather.

            Bob had curly red hair and a blue wool coat that marked him in every scene.

            Alice smiled once.
            """;
        var harvested = CastFromScreenplayService.HarvestNameLookExcerpts(
            book, new[] { "Bob", "Alice" }, maxChars: 2_000);
        Assert.Contains("curly red hair", harvested, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blue wool coat", harvested, StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public async Task Cast_system_prompt_is_model_decided_without_forced_name_lists()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true);
            return;
        }

        var text = await CastFromScreenplayService.LoadSystemPromptAsync(root);
        Assert.Contains("external name list", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("forced candidate", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scene headings", text, StringComparison.OrdinalIgnoreCase);
        // Must not reintroduce product-code name discovery contract
        Assert.DoesNotContain("DETECTED ON-SCREEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_puts_clothes_in_wardrobe_always_not_visual_lock()
    {
        var prompt = CastFromScreenplayService.BuildUserPrompt(
            "Title: Test\n\nINT. ROOM - DAY\n\nHERO\nHello.",
            book: null);
        Assert.Contains("wardrobe_always", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("face / markings / species", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clothes go in wardrobe_always", prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoWithPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "fountain_to_cast.txt");
            if (File.Exists(candidate))
                return dir.FullName;
        }
        var known = @"C:\Users\budcr\source\repos\gemini\PageToMovie";
        if (File.Exists(Path.Combine(known, "prompts", "fountain_to_cast.txt")))
            return known;
        return null;
    }

}
