using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CinematicLightingClassifierTests
{
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
    public async Task ClassifySceneLightingAsync_ParsesLightingTokenCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "lighting_token": "Chiaroscuro flickering candlelight with deep obsidian shadows and desaturated cool-gray volumetric fog"
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyCinematicLightingWithChat = true });
        var classifier = new CinematicLightingClassifier(mockChat, opts, NullLogger<CinematicLightingClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - DAY",
            ["render_style_lock"] = "Period gothic live-action"
        };

        var token = await classifier.ClassifySceneLightingAsync(scene);

        Assert.NotNull(token);
        Assert.Contains("Chiaroscuro flickering candlelight", token);
        Assert.Contains("obsidian shadows", token);
        AssertNoGradeOrStock(token);
    }

    [Fact]
    public void SystemPrompt_owns_light_not_grade_or_stock()
    {
        var prompt = CinematicLightingClassifier.SystemPrompt();
        Assert.Contains("light SOURCES", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("color temperature palette", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warm amber color grade", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do NOT say \"color grade\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Do NOT name film stock", prompt, StringComparison.Ordinal);
        Assert.Contains("Grade owns", prompt, StringComparison.Ordinal);
        var exampleStart = prompt.IndexOf("Example (Gothic/Night)", StringComparison.Ordinal);
        var exampleEnd = prompt.IndexOf("Do NOT say", StringComparison.Ordinal);
        Assert.True(exampleStart >= 0 && exampleEnd > exampleStart);
        var examples = prompt[exampleStart..exampleEnd];
        Assert.DoesNotContain("grade", examples, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stock", examples, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Warm golden-hour sunlight at low angle, high contrast shadows with warm amber color grade.",
        "color grade")]
    [InlineData(
        "Chiaroscuro candlelight, Kodak Vision3 500T film stock, deep shadows.",
        "film stock")]
    public void SanitizeLightingToken_strips_grade_and_stock(string raw, string banned)
    {
        var clean = CinematicLightingClassifier.SanitizeLightingToken(raw);
        Assert.NotNull(clean);
        AssertNoGradeOrStock(clean);
        Assert.DoesNotContain(banned, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shadow", clean, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoGradeOrStock(string? text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("color grade", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color grading", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("film stock", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifySceneLightingAsync_strips_grade_clause_from_model_output()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "lighting_token": "Warm golden-hour sunlight at low angle, high contrast shadows with warm amber color grade."
            }
            """
        };
        var opts = Options.Create(new PageToMovieOptions { ClassifyCinematicLightingWithChat = true });
        var classifier = new CinematicLightingClassifier(mockChat, opts, NullLogger<CinematicLightingClassifier>.Instance);

        var token = await classifier.ClassifySceneLightingAsync(new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "EXT. LANE - DAY",
        });

        Assert.NotNull(token);
        Assert.Contains("golden-hour sunlight", token);
        AssertNoGradeOrStock(token);
    }

    [Fact]
    public async Task ClassifySceneLightingAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyCinematicLightingWithChat = false });
        var classifier = new CinematicLightingClassifier(mockChat, opts, NullLogger<CinematicLightingClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };

        var token = await classifier.ClassifySceneLightingAsync(scene);

        Assert.Null(token);
    }
}
