using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The picture is the identity. Once a character's reference image rides with the shot, prose
/// describing their face can only agree redundantly or disagree — and a model weighing sentences
/// against pixels will sometimes pick the sentences. Annette's seeds said "dark brown-black hair,
/// dark brown eyes, sun-warmed tan skin" over a photograph of auburn hair and blue eyes. Where the
/// words came from makes no difference: a description matching the photo is no more use than one
/// contradicting it, and it still competes. What matters is whether the picture is there.
///
/// visual_lock is exempt, and that is not a hedge. It holds the one curated trait that must not
/// drift — the subtle thing a model gets wrong while looking straight at the reference. Cutting it
/// is a mistake already made and measured here: Tell-Tale Heart's Old Man lost his filmy eye and
/// rendered with a clear one, refs attached.
/// </summary>
public class InventedLookYieldsToImageTests
{
    private const string LookProse = "dark brown-black hair, dark brown eyes, sun-warmed tan skin";
    private const string LockProse = "Never dark-haired, never dark-eyed, never tanned";

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
                Description = $"Woman in her fifties, {LookProse}.",
                VisualLock = LockProse,
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

    [Theory]
    [InlineData(LookProvenanceTokens.Invented)]
    [InlineData(LookProvenanceTokens.Sourced)]
    [InlineData(LookProvenanceTokens.Inferred)]
    [InlineData("")]
    public void An_attached_picture_retires_the_words_describing_the_same_face(string provenance)
    {
        var prompt = BuildPrompt(provenance, attachReference: true);

        Assert.Contains("<IMAGE_1>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(LookProse, prompt, StringComparison.OrdinalIgnoreCase);
        // visual_lock is the hand-curated must-not-drift trait, not a restatement of the photo,
        // and it earns its place beside one — see the Old Man's filmy eye.
        Assert.Contains(LockProse, prompt, StringComparison.OrdinalIgnoreCase);
        // The character is still in the shot, and still told to match their reference — it is the
        // adjectives that left, not them.
        Assert.Contains("Character_Annette", prompt, StringComparison.Ordinal);
        Assert.Contains("Match appearance of reference", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LookProvenanceTokens.Invented)]
    [InlineData(LookProvenanceTokens.Sourced)]
    [InlineData("")]
    public void With_no_picture_the_words_are_the_only_identity_and_stay(string provenance)
    {
        // Also the video-extend case: the provider's extensions endpoint cannot carry reference
        // images, so a shot built that way has nothing but this prose to go on.
        var prompt = BuildPrompt(provenance, attachReference: false);

        Assert.DoesNotContain("<IMAGE_1>", prompt, StringComparison.Ordinal);
        Assert.Contains(LookProse, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LockProse, prompt, StringComparison.OrdinalIgnoreCase);
    }
}
