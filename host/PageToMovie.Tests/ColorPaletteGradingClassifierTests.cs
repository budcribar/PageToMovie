using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ColorPaletteGradingClassifierTests
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
    public async Task ClassifySceneColorGradingAsync_ParsesColorDirectivesCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "film_stock": "Kodak Vision3 500T 5219 film stock, subtle 35mm grain",
              "color_palette": "Desaturated cool-teal shadow tones with warm amber candle highlights",
              "grading_prompt": "Color grading: Kodak Vision3 500T 5219 film stock, desaturated cool-teal shadows and warm amber candle highlights"
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyColorPaletteGradingWithChat = true });
        var classifier = new ColorPaletteGradingClassifier(mockChat, opts, NullLogger<ColorPaletteGradingClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - NIGHT",
            ["render_style_lock"] = "19th century gothic live-action"
        };

        var color = await classifier.ClassifySceneColorGradingAsync(scene);

        Assert.NotNull(color);
        Assert.Contains("Kodak Vision3 500T", color!.FilmStock);
        Assert.Contains("Desaturated cool-teal", color.ColorPalette);
        Assert.DoesNotContain("Color grading:", color.GradingPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Kodak Vision3 500T", color.GradingPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_owns_emulsion_not_lighting()
    {
        var prompt = ColorPaletteGradingClassifier.SystemPrompt();
        Assert.Contains("emulsion", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do NOT prefix it with \"Color grading:\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Lighting owns", prompt, StringComparison.Ordinal);
        Assert.Contains("Do NOT describe light sources", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Color grading: Kodak Vision3 500T, teal shadows", "Kodak Vision3 500T, teal shadows")]
    [InlineData("Colour grading: Fuji Eterna 500T", "Fuji Eterna 500T")]
    [InlineData("Grade: hand-tinted print stock", "hand-tinted print stock")]
    [InlineData("Kodak Vision3 250D, honeyed amber", "Kodak Vision3 250D, honeyed amber")]
    public void StripGradeLabel_drops_prose_prefix(string raw, string expected)
    {
        Assert.Equal(expected, ColorPaletteGradingClassifier.StripGradeLabel(raw));
    }

    [Fact]
    public async Task ClassifySceneColorGradingAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyColorPaletteGradingWithChat = false });
        var classifier = new ColorPaletteGradingClassifier(mockChat, opts, NullLogger<ColorPaletteGradingClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };

        var color = await classifier.ClassifySceneColorGradingAsync(scene);

        Assert.Null(color);
    }
}
