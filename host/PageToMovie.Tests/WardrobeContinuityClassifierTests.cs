using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class WardrobeContinuityClassifierTests
{
    private sealed class MockChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "";
        public string? LastUserPrompt { get; private set; }
        public string? LastSystemPrompt { get; private set; }

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task ClassifySceneWardrobeAsync_ParsesAttireCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "wardrobe": [
                {
                  "character_key": "Character_The_Narrator",
                  "attire": "plain dark waistcoat, white shirtsleeves, rolled cuffs"
                },
                {
                  "character_key": "Character_The_Old_Man",
                  "attire": "loose white cotton nightshirt"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyWardrobeContinuityWithChat = true });
        var classifier = new WardrobeContinuityClassifier(mockChat, opts, NullLogger<WardrobeContinuityClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - DAY"
        };

        var cast = new List<string> { "Character_The_Narrator", "Character_The_Old_Man" };

        var wardrobe = await classifier.ClassifySceneWardrobeAsync(scene, cast);

        Assert.NotNull(wardrobe);
        Assert.Equal("plain dark waistcoat, white shirtsleeves, rolled cuffs", wardrobe!["Character_The_Narrator"]);
        Assert.Equal("loose white cotton nightshirt", wardrobe["Character_The_Old_Man"]);
    }

    [Fact]
    public async Task ClassifySceneWardrobeAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyWardrobeContinuityWithChat = false });
        var classifier = new WardrobeContinuityClassifier(mockChat, opts, NullLogger<WardrobeContinuityClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var cast = new List<string> { "Character_The_Narrator" };

        var wardrobe = await classifier.ClassifySceneWardrobeAsync(scene, cast);

        Assert.Null(wardrobe);
    }

    [Fact]
    public async Task ClassifySceneWardrobeAsync_user_prompt_includes_identity_wardrobe()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "wardrobe": [
                { "character_key": "Character_Mary", "attire": "wool walking coat" }
              ]
            }
            """
        };
        var opts = Options.Create(new PageToMovieOptions { ClassifyWardrobeContinuityWithChat = true });
        var classifier = new WardrobeContinuityClassifier(mockChat, opts, NullLogger<WardrobeContinuityClassifier>.Instance);
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 3,
            ["setting"] = "EXT. COUNTRY LANE - DAY",
            ["story_beats"] = new List<object?>
            {
                new Dictionary<string, object?> { ["visual_event"] = "Mary walks the lamb along the lane." },
            },
        };
        var cast = new List<string> { "Character_Mary" };
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["wardrobe_always"] = new List<object?> { "pale pinafore", "rose ribbon" },
            },
        };

        var wardrobe = await classifier.ClassifySceneWardrobeAsync(scene, cast, charSeeds: seeds);

        Assert.NotNull(wardrobe);
        Assert.Equal("wool walking coat", wardrobe!["Character_Mary"]);
        Assert.Contains("pale pinafore", mockChat.LastUserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rose ribbon", mockChat.LastUserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT WARDROBE", mockChat.LastUserPrompt, StringComparison.Ordinal);
        Assert.Contains("DELTA", mockChat.LastSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("heavy wool trench coat", mockChat.LastSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_lists_wardrobe_always_and_scene_sticky()
    {
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BEDCHAMBER - NIGHT",
            ["wardrobe_by_character"] = new Dictionary<string, object?>
            {
                ["Character_Mary"] = new List<object?> { "house slippers" },
            },
        };
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["wardrobe_always"] = new List<object?> { "pale pinafore", "rose ribbon" },
            },
        };

        var prompt = WardrobeContinuityClassifier.BuildUserPrompt(
            scene, new List<string> { "Character_Mary" }, seeds);

        Assert.Contains("pale pinafore", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rose ribbon", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("house slippers", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
