using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Generate / extend: one voice owner per speaker. Sample + slim Voice, or full VoiceLock.
/// No house Honor VOICE LOCK restatement. Catalog gate only — no invented model ids.
/// </summary>
[Collection("catalog-serial")]
public sealed class VoiceLockPromptTests
{
    private static string VideoModelWithReferenceAudios()
    {
        var row = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .FirstOrDefault(m => !m.Virtual && m.SupportsReferenceAudios && m.PresetVoices is { Count: > 0 });
        Assert.NotNull(row);
        return row!.Id;
    }

    private static string FirstPresetId(string modelId)
    {
        var row = SupportedModelCatalog.Find(modelId, ModelCapability.Video);
        Assert.NotNull(row);
        Assert.NotEmpty(row!.PresetVoices!);
        return row.PresetVoices![0].Id;
    }

    [Fact]
    public void Build_no_preset_emits_one_VoiceLock_and_no_Voice_for_the_speaker()
    {
        using var doc = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. HALL.",
              "characters_on_screen": ["Character_Eve"],
              "audio_payload": {
                "speaker": "Character_Eve",
                "dialogue": "We begin.",
                "delivery": "spoken_on_camera"
              }
            }
            """);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = new()
            {
                Key = "Character_Eve",
                DisplayName = "Eve",
                VoiceProfile = "Adult female, 30s, bright soprano; measured pace; Irish accent",
            },
        };

        var built = ClipVideoPromptBuilder.Build(doc.RootElement, Path.GetTempPath(), profiles);

        Assert.Equal(1, VoiceTagWriter.CountVoiceLocks(built.Prompt));
        Assert.Equal(0, VoiceTagWriter.CountVoiceTags(built.Prompt));
        Assert.Contains("<VoiceLock>", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("Adult female, 30s, bright soprano", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Honor VOICE LOCK", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(built.ReferenceAudioVoiceIds);
    }

    [Fact]
    public void Build_attached_preset_omits_VoiceLock_and_slims_Voice()
    {
        var modelId = VideoModelWithReferenceAudios();
        var preset = FirstPresetId(modelId);
        using var doc = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. HALL.",
              "characters_on_screen": ["Character_Eve"],
              "audio_payload": {
                "speaker": "Character_Eve",
                "dialogue": "We begin.",
                "delivery": "spoken_on_camera"
              }
            }
            """);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = new()
            {
                Key = "Character_Eve",
                DisplayName = "Eve",
                ImagineVoiceId = preset,
                VoiceProfile = "Adult female, 30s, bright soprano; measured pace; Irish accent",
            },
        };

        var built = ClipVideoPromptBuilder.Build(
            doc.RootElement, Path.GetTempPath(), profiles, videoModel: modelId);

        Assert.Equal(new[] { preset }, built.ReferenceAudioVoiceIds);
        Assert.Contains("<AUDIO_0>", built.Prompt, StringComparison.Ordinal);
        Assert.Equal(0, VoiceTagWriter.CountVoiceLocks(built.Prompt));
        Assert.DoesNotContain("<VoiceLock>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pace", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accent", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soprano", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Adult female", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Honor VOICE LOCK", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_extend_skips_reference_audios_and_keeps_one_VoiceLock()
    {
        var modelId = VideoModelWithReferenceAudios();
        var preset = FirstPresetId(modelId);
        using var doc = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. HALL.",
              "characters_on_screen": ["Character_Eve"],
              "audio_payload": {
                "speaker": "Character_Eve",
                "dialogue": "We continue.",
                "delivery": "spoken_on_camera"
              }
            }
            """);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = new()
            {
                Key = "Character_Eve",
                DisplayName = "Eve",
                ImagineVoiceId = preset,
                VoiceProfile = "Adult female, 30s, bright soprano; measured pace",
            },
        };

        var hop = ClipVideoPromptBuilder.Build(
            doc.RootElement, Path.GetTempPath(), profiles,
            videoModel: modelId,
            previousClipExtendFileId: "file-prev");

        Assert.Empty(hop.ReferenceAudioVoiceIds);
        Assert.DoesNotContain("<AUDIO_0>", hop.Prompt, StringComparison.Ordinal);
        Assert.Equal(1, VoiceTagWriter.CountVoiceLocks(hop.Prompt));
        Assert.Equal(0, VoiceTagWriter.CountVoiceTags(hop.Prompt));
        Assert.Contains("<VoiceLock>", hop.Prompt, StringComparison.Ordinal);
        Assert.Contains("Adult female, 30s, bright soprano", hop.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void House_Honor_VOICE_LOCK_is_absent_from_retired_clip_gen_rules()
    {
        var rules = ClipVideoPromptBuilder.TryLoadClipGenRules();
        Assert.False(string.IsNullOrWhiteSpace(rules));
        Assert.DoesNotContain("Honor VOICE LOCK", rules!, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(ClipVideoPromptBuilder.PromptBodyFromClipGenRules(rules)));
    }
}
