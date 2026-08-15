using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Fal.ai lip-sync (fal-ai/sync-lipsync) and voice-clone narration (fal-ai/minimax/voice-clone +
/// fal-ai/minimax/speech-02-hd) catalog wiring + local plumbing. No network calls — see
/// LiveApi/README.md for the paid-call opt-in convention; nothing here spends real money.
/// </summary>
// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class LipSyncVoiceCloneTests
{
    public LipSyncVoiceCloneTests() => SupportedModelCatalog.ReloadCatalog();

    [Fact]
    public void LipSync_empty_model_throws_instead_of_inventing_default()
    {
        Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveOrDefault(null, ModelCapability.LipSync));
        var m = SupportedModelCatalog.Find("fal-ai/sync-lipsync/v2/pro", ModelCapability.LipSync);
        Assert.NotNull(m);
        Assert.Equal("fal-ai/sync-lipsync/v2/pro", m!.Id);
        Assert.Equal(ModelProviderFamily.Fal, m.Provider);
        Assert.Equal("fal", m.ProviderId);
        Assert.Contains(SupportedModelCatalog.FalApiKeyEnv, m.RequiredEnvKeys);
        Assert.Equal(5.0, m.CostPerMinuteUsd);
    }

    [Fact]
    public void LipSync_v3_entry_exists_with_published_per_minute_price()
    {
        var m = SupportedModelCatalog.Find("fal-ai/sync-lipsync/v3", ModelCapability.LipSync);
        Assert.NotNull(m);
        Assert.True(m!.Enabled);
        Assert.Equal(8.0, m.CostPerMinuteUsd);
    }

    [Fact]
    public void LipSync_models_are_not_mixed_into_the_general_video_capability()
    {
        // Lip-sync takes video+audio, not a text prompt — it must never show up as a pickable
        // text-to-video model.
        var video = SupportedModelCatalog.ForCapability(ModelCapability.Video, enabledOnly: false);
        Assert.DoesNotContain(video, e => e.Id.Contains("sync-lipsync", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Voice_clone_step_model_is_flagged_and_priced_per_clone()
    {
        var m = SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice);
        Assert.NotNull(m);
        Assert.True(m!.IsVoiceCloneStep);
        Assert.Equal(1.5, m.CostPerCloneUsd);
        Assert.Null(m.CostPerThousandCharsUsd);
    }

    [Fact]
    public void Voice_speak_step_model_is_not_flagged_as_clone_and_priced_per_1000_chars()
    {
        var m = SupportedModelCatalog.Find("fal-ai/minimax/speech-02-hd", ModelCapability.Voice);
        Assert.NotNull(m);
        Assert.False(m!.IsVoiceCloneStep);
        Assert.Equal(0.1, m.CostPerThousandCharsUsd);
        Assert.Equal(5000, m.MaxPromptLength);
        Assert.Null(m.CostPerCloneUsd);
    }

    [Fact]
    public void Existing_elevenlabs_voice_clone_entry_is_now_flagged_as_clone_shaped()
    {
        // Was previously unconsumed/unflagged (nothing distinguished it from the TTS entry) —
        // confirms the reclassification didn't silently drop the entry.
        var m = SupportedModelCatalog.Find("eleven_voice_clone", ModelCapability.Voice);
        Assert.NotNull(m);
        Assert.True(m!.IsVoiceCloneStep);

        var tts = SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice);
        Assert.NotNull(tts);
        Assert.False(tts!.IsVoiceCloneStep);
    }

    [Fact]
    public void Voice_capability_has_exactly_one_clone_shaped_and_at_least_one_speak_shaped_fal_entry()
    {
        var falVoice = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .Where(e => e.Provider == ModelProviderFamily.Fal).ToList();
        Assert.Single(falVoice, e => e.IsVoiceCloneStep);
        Assert.Contains(falVoice, e => !e.IsVoiceCloneStep);
    }

    [Fact]
    public void ToDto_and_FromDto_round_trip_new_fields()
    {
        var entry = SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)!;
        var dto = SupportedModelCatalog.ToDto(entry);
        Assert.True(dto.IsVoiceCloneStep);
        Assert.Equal(1.5, dto.CostPerCloneUsd);

        var roundTripped = SupportedModelCatalog.FromDto(dto);
        Assert.True(roundTripped.IsVoiceCloneStep);
        Assert.Equal(1.5, roundTripped.CostPerCloneUsd);
        Assert.Equal(ModelCapability.Voice, roundTripped.Capability);

        var lipSyncEntry = SupportedModelCatalog.Find("fal-ai/sync-lipsync/v2/pro", ModelCapability.LipSync)!;
        var lipSyncDto = SupportedModelCatalog.ToDto(lipSyncEntry);
        Assert.Equal(5.0, lipSyncDto.CostPerMinuteUsd);
        var lipSyncRoundTripped = SupportedModelCatalog.FromDto(lipSyncDto);
        Assert.Equal(ModelCapability.LipSync, lipSyncRoundTripped.Capability);
        Assert.Equal(5.0, lipSyncRoundTripped.CostPerMinuteUsd);
    }

    [Fact]
    public void CostCategories_buckets_lip_sync_kind_under_video_spend()
    {
        Assert.Equal(CostCategories.Video, CostCategories.Resolve("lip_sync", mode: null));
    }

    [Fact]
    public void CostCategories_buckets_voice_clone_and_tts_kinds_under_voice_spend()
    {
        Assert.Equal(CostCategories.Voice, CostCategories.Resolve("voice_clone", mode: null));
        Assert.Equal(CostCategories.Voice, CostCategories.Resolve("tts", mode: null));
    }

    [Fact]
    public void Clients_report_not_configured_without_a_fal_key()
    {
        // No env var / ApiKeyScope set in this test process — both clients should report
        // unconfigured rather than throwing, matching FalAudioClient/FalVideoClient's own
        // IsConfigured contract.
        using var http1 = new HttpClient();
        using var http2 = new HttpClient();
        var lipSync = new FalLipSyncClient(http1, Microsoft.Extensions.Logging.Abstractions.NullLogger<FalLipSyncClient>.Instance);
        var voiceClone = new FalVoiceCloneClient(http2, Microsoft.Extensions.Logging.Abstractions.NullLogger<FalVoiceCloneClient>.Instance);

        var hadKey = Environment.GetEnvironmentVariable("FAL_API_KEY");
        var hadKeyFallback = Environment.GetEnvironmentVariable("FAL_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FAL_API_KEY", null);
            Environment.SetEnvironmentVariable("FAL_KEY", null);
            Assert.False(lipSync.IsConfigured);
            Assert.False(voiceClone.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAL_API_KEY", hadKey);
            Environment.SetEnvironmentVariable("FAL_KEY", hadKeyFallback);
        }
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("narration.wav", "audio/wav")]
    [InlineData("narration.mp3", "audio/mpeg")]
    [InlineData("narration.unknownext", null)]
    public void LipSync_ContentType_resolution_matches_extension(string fileName, string? expected)
    {
        Assert.Equal(expected, FalLipSyncClient.ResolveContentType(fileName));
    }

    [Theory]
    [InlineData("sample.wav", "audio/wav")]
    [InlineData("sample.mp3", "audio/mpeg")]
    [InlineData("sample.m4a", "audio/mp4")]
    [InlineData("sample.unknownext", "audio/mpeg")] // safe default, never null (always sent as a data URI)
    public void VoiceClone_audio_content_type_resolution_matches_extension(string fileName, string expected)
    {
        Assert.Equal(expected, FalVoiceCloneClient.ResolveAudioContentType(fileName));
    }
}

/// <summary>
/// ProjectStore's voice_clone_provider_id storage (reuses the existing per-character seed file —
/// no new storage mechanism — see GetVoiceCloneSamplePath for the sibling reference-audio slot).
/// </summary>
public sealed class VoiceCloneProviderIdStorageTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "VoiceCloneGate";

    public VoiceCloneProviderIdStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-voiceclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void GetVoiceCloneProviderId_is_null_before_any_clone()
    {
        Assert.Null(_store.GetVoiceCloneProviderId(ProjectId, "Character_Narrator"));
    }

    [Fact]
    public void UpdateCharacterSeedText_round_trips_voice_clone_provider_id()
    {
        _store.UpdateCharacterSeedText(ProjectId, "Character_Narrator", voiceCloneProviderId: "voice_abc123");
        Assert.Equal("voice_abc123", _store.GetVoiceCloneProviderId(ProjectId, "Character_Narrator"));
    }

    [Fact]
    public void Narrator_is_just_a_regular_character_key_no_special_casing()
    {
        // Reusing the same per-character storage for a narration pseudo-character works because
        // UpdateCharacterSeedText creates a seed entry on demand — "Narrator" needs no special
        // handling anywhere in ProjectStore.
        _store.UpdateCharacterSeedText(ProjectId, "Narrator", voiceCloneProviderId: "voice_narrator_1");
        Assert.Equal("voice_narrator_1", _store.GetVoiceCloneProviderId(ProjectId, "Narrator"));
        Assert.Null(_store.GetVoiceCloneProviderId(ProjectId, "Character_SomeoneElse"));
    }

    [Fact]
    public void Empty_string_clears_the_stored_provider_id()
    {
        _store.UpdateCharacterSeedText(ProjectId, "Character_Narrator", voiceCloneProviderId: "voice_abc123");
        Assert.Equal("voice_abc123", _store.GetVoiceCloneProviderId(ProjectId, "Character_Narrator"));

        _store.UpdateCharacterSeedText(ProjectId, "Character_Narrator", voiceCloneProviderId: "");
        Assert.Null(_store.GetVoiceCloneProviderId(ProjectId, "Character_Narrator"));
    }

    [Fact]
    public void DeleteVoiceCloneSample_also_clears_the_stale_provider_id()
    {
        _store.UpdateCharacterSeedText(ProjectId, "Character_Narrator", voiceCloneProviderId: "voice_abc123");

        // No actual sample file on disk here, but DeleteVoiceCloneSample only clears the provider
        // id when it removed at least one file — so create a dummy sample first.
        var dir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters", "Character_Narrator");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "voice_clone_sample.wav"), new byte[16]);

        var removed = _store.DeleteVoiceCloneSample(ProjectId, "Character_Narrator");
        Assert.True(removed);
        Assert.Null(_store.GetVoiceCloneProviderId(ProjectId, "Character_Narrator"));
    }
}
