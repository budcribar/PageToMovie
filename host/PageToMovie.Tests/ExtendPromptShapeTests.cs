using System.Text.Json;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// An extend is generated from the previous clip's video, so the setting, lighting, framing, optics
/// and grade are visibly present in the input. Sending them again does not confirm them — it asks
/// the model to establish a shot, and it does, from scratch: the subject is restaged and the
/// continuation breaks.
/// </summary>
/// <remarks>
/// Measured against the provider's extend endpoint on Mary19 S02C02, repeated runs rather than
/// single trials. With these blocks present the animal was thrown back to the front of the room;
/// with them removed and the source video unchanged it stayed where the previous clip left it.
/// Removing the continuity block as well made it slightly worse, so that one stays.
/// </remarks>
public sealed class ExtendPromptShapeTests
{
    private const string Visual =
        "<StyleLock>stylized picture-book CG</StyleLock> " +
        "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
        "<Action>THE CHILDREN twist in their seats and point.</Action> " +
        "<Lighting>Soft warm daylight through tall windows.</Lighting> " +
        "<Camera>Medium desk-row shot, 35mm, slow push in.</Camera> " +
        "<Performance>Acting intensity 7/10: beaming smiles</Performance> " +
        "<Optics>f/2.0 shallow depth of field</Optics> " +
        "<Grade>Kodak Vision3 250D, honeyed amber</Grade>";

    private static ClipVideoPromptBuilder.PromptBuildResult Build(string? extendFileId)
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt = Visual,
            characters_on_screen = new[] { "Character_The_Children" },
            audio_payload = new
            {
                speaker = "Character_Narrator",
                delivery = "voiceover_internal",
                dialogue = "It made the children laugh and play to see a lamb at school.",
            },
        })).RootElement.Clone();
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_The_Children"] = new() { Key = "Character_The_Children", DisplayName = "Children", Description = "a small group" },
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator", Description = "warm voice", VoiceOnly = true },
        };
        return ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles, previousClipExtendFileId: extendFileId);
    }

    [Theory]
    [InlineData(PromptFieldTags.Setting)]
    [InlineData(PromptFieldTags.Lighting)]
    [InlineData(PromptFieldTags.Camera)]
    [InlineData(PromptFieldTags.Optics)]
    [InlineData(PromptFieldTags.Grade)]
    public void A_continuation_drops_what_the_source_video_shows(string tag)
    {
        var built = Build(extendFileId: "file_abc123");

        Assert.Equal("video-extend", built.Mode);
        Assert.DoesNotContain($"<{tag}>", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Continuity keeps the art medium — blanking StyleLock let later hops flip watercolor to CG.</summary>
    [Fact]
    public void A_continuation_keeps_the_style_lock()
    {
        var built = Build(extendFileId: "file_abc123");

        Assert.Contains("stylized picture-book CG", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Same art medium and renderer", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What describes the NEW footage stays — that is the only thing the model must invent.</summary>
    [Fact]
    public void A_continuation_keeps_what_describes_the_new_footage()
    {
        var built = Build(extendFileId: "file_abc123");

        Assert.Contains("twist in their seats", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"<{PromptFieldTags.Performance}>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("laugh and play", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // The continuity block stays: removing it measured slightly worse, not better.
        Assert.Contains("<Continuity>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<Negative>", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A fresh clip has no source video, so it must be told the whole look.</summary>
    [Theory]
    [InlineData(PromptFieldTags.Setting)]
    [InlineData(PromptFieldTags.Lighting)]
    [InlineData(PromptFieldTags.Camera)]
    [InlineData(PromptFieldTags.Optics)]
    [InlineData(PromptFieldTags.Grade)]
    public void A_fresh_clip_keeps_the_whole_look(string tag)
    {
        var built = Build(extendFileId: null);

        Assert.Equal("fresh", built.Mode);
        Assert.Contains($"<{tag}>", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fresh_clip_keeps_its_style_lock()
    {
        var built = Build(extendFileId: null);

        Assert.Contains("stylized picture-book CG", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }
}
