using PageToMovie.Adaptation.Conversion;
using Xunit;

namespace PageToMovie.Tests;

public sealed class AdaptationPromptTokenTests
{
    [Fact]
    public void ApplyPromptTokens_resolves_runtime_and_rejects_unknown()
    {
        var body = "Target {{TOTAL_RUNTIME_MINUTES}} · {{RUNTIME_TARGET_DIRECTIVE}} · medium: {{VISUAL_MEDIUM}}";
        var outText = AdaptationPromptPack.ApplyPromptTokens(
            body,
            AdaptationPromptTokens.Default(null, "photoreal"));
        Assert.DoesNotContain("{{", outText);
        Assert.Contains("unlimited", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("photoreal", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyPromptTokens_throws_on_unknown_token()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdaptationPromptPack.ApplyPromptTokens(
                "Hello {{NOT_A_REAL_TOKEN}}",
                AdaptationPromptTokens.Default()));
        Assert.Contains("NOT_A_REAL_TOKEN", ex.Message);
    }

    [Fact]
    public void ApplyPromptTokens_scene_band_open_when_unset()
    {
        var body = "Scenes {{SCENE_COUNT_MIN}} to {{SCENE_COUNT_MAX}} ({{SCENE_COUNT_BAND}}).";
        var outText = AdaptationPromptPack.ApplyPromptTokens(body, AdaptationPromptTokens.Default(5));
        Assert.DoesNotContain("{{", outText);
        Assert.Contains("no fixed scene-count band", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_prompt_has_no_leftover_tokens()
    {
        // Uses embedded/disk production book_to_fountain.txt
        var text = await AdaptationPromptPack.LoadBookToFountainSystemPromptAsync(null);
        Assert.DoesNotContain("{{", text);
        Assert.Contains("unlimited", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_prompt_has_mid_script_join_policy()
    {
        var text = await AdaptationPromptPack.LoadBookToFountainSystemPromptAsync(null);
        Assert.Contains("BETWEEN-SCENE JOINS", text, StringComparison.Ordinal);
        Assert.Contains("DISSOLVE TO:", text, StringComparison.Ordinal);
        Assert.Contains("> FADE OUT.", text, StringComparison.Ordinal);
        Assert.Contains("do NOT stamp DISSOLVE TO:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[CARD:", text, StringComparison.Ordinal);
        Assert.Contains("Bare FADE TO WHITE", text, StringComparison.Ordinal);
    }
}
