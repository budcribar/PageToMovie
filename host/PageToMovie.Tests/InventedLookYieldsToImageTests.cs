using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// First render wins. Where the story described nobody, the look the pipeline wrote is a stand-in
/// for a picture that does not exist yet. Once the picture does exist, those words stop being
/// helpful and start being wrong: "dark brown-black hair" is shipped into every shot alongside a
/// photograph of auburn, and the model has to pick a side. A look the author actually wrote is a
/// different thing and keeps riding along — restating it next to the reference costs nothing and
/// guards against drift.
/// </summary>
public class InventedLookYieldsToImageTests
{
    private const string InventedProse = "dark brown-black hair, dark brown eyes, sun-warmed tan skin";
    private const string SourcedProse = "pale filmy left eye, thin white hair";

    private static JsonElement Clip() => JsonDocument.Parse("""
        {
          "clip_number": 1,
          "visual_prompt": "Character_Annette stands in the doorway.",
          "characters_on_screen": ["Character_Annette"],
          "veo_continuation_source": "none"
        }
        """).RootElement;

    private static string BuildPrompt(string provenance, bool attachReference)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "assets", "characters"));
        if (attachReference)
        {
            File.WriteAllBytes(
                Path.Combine(tmp, "assets", "characters", "character_annette_ref.png"),
                new byte[512]);
        }

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Annette"] = new()
            {
                Key = "Character_Annette",
                DisplayName = "Annette",
                Description = $"Woman in her fifties, {(provenance == LookProvenanceTokens.Invented ? InventedProse : SourcedProse)}.",
                VoiceProfile = "warm mid pitch",
                LookProvenance = provenance,
            },
        };

        try
        {
            return ClipVideoPromptBuilder.Build(Clip(), tmp, profiles, maxRefs: 5).Prompt;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void An_invented_look_stands_down_once_its_reference_image_is_attached()
    {
        var prompt = BuildPrompt(LookProvenanceTokens.Invented, attachReference: true);

        Assert.Contains("<IMAGE_1>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(InventedProse, prompt, StringComparison.OrdinalIgnoreCase);
        // The character is still in the shot — it is the invented adjectives that left, not them.
        Assert.Contains("Character_Annette", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_reference_image_the_invented_look_is_all_there_is_and_stays()
    {
        var prompt = BuildPrompt(LookProvenanceTokens.Invented, attachReference: false);

        Assert.DoesNotContain("<IMAGE_1>", prompt, StringComparison.Ordinal);
        Assert.Contains(InventedProse, prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LookProvenanceTokens.Sourced)]
    [InlineData(LookProvenanceTokens.Inferred)]
    [InlineData("")]
    public void A_look_the_source_backs_rides_alongside_its_reference_image(string provenance)
    {
        var prompt = BuildPrompt(provenance, attachReference: true);

        Assert.Contains("<IMAGE_1>", prompt, StringComparison.Ordinal);
        Assert.Contains(SourcedProse, prompt, StringComparison.OrdinalIgnoreCase);
    }
}
