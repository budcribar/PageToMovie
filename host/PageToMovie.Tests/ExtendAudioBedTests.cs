using System.Text.Json;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A continuation is generated from the previous clip's video, which already carries that clip's
/// audio bed — the model continues it on its own. Asking for the bed again makes it re-generate the
/// effects prominently, and they land on top of the line's opening word.
/// </summary>
/// <remarks>
/// Measured against the provider's extend endpoint on Mary19 S02C02, with repeated runs rather than
/// single trials: with the layers present the narrator dropped the line's first word in every run;
/// with only Score and Foley removed and everything else identical, the word survived in every run.
/// Same defect as the duplicated Sound block, one layer down — that removed the second request for
/// these effects, this removes the last one on a path where the video already supplies them.
/// </remarks>
public sealed class ExtendAudioBedTests
{
    private const string Line = "It made the children laugh and play to see a lamb at school.";

    private static ClipVideoPromptBuilder.PromptBuildResult Build(string? extendFileId)
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt = "<Action>THE CHILDREN twist and point.</Action>",
            characters_on_screen = new[] { "Character_Narrator" },
            audio_payload = new
            {
                speaker = "Character_Narrator",
                delivery = "voiceover_internal",
                dialogue = Line,
                sfx = "clap, lamb bleats, laughter",
                ambient = "schoolroom room tone",
                score_layer = "playful bouncing flute melody",
            },
        })).RootElement.Clone();
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator", DisplayName = "Narrator",
                Description = "warm voice", VoiceOnly = true,
            },
        };
        return ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles, previousClipExtendFileId: extendFileId);
    }

    [Fact]
    public void A_continuation_asks_for_no_music_or_foley()
    {
        var built = Build(extendFileId: "file_abc123");

        Assert.Equal("video-extend", built.Mode);
        Assert.DoesNotContain("<Foley>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<Score>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lamb bleats", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // The spoken line and the voice are untouched — only the competing layers are gone.
        Assert.Contains(Line, built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("off-camera voiceover", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A fresh clip has no predecessor to inherit a bed from, so it still asks for one.</summary>
    [Fact]
    public void A_fresh_clip_still_gets_its_bed()
    {
        var built = Build(extendFileId: null);

        Assert.Equal("fresh", built.Mode);
        Assert.Contains("<Foley>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lamb bleats", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }
}
