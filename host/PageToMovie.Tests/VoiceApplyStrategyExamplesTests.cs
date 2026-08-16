using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.VoiceApply;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Example unit tests for the voice-apply strategy pattern.
/// Uses hand-rolled fakes (no Moq) so the tests document the contracts clearly.
/// </summary>
[Collection("catalog-serial")]
public class VoiceApplyStrategyExamplesTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly VoicePreviewStore _previews;

    public VoiceApplyStrategyExamplesTests()
    {
        SupportedModelCatalog.ReloadCatalog();
        _root = Path.Combine(Path.GetTempPath(), "ptm-voice-strat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        _previews = new VoicePreviewStore(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // ── CanHandle selection examples ─────────────────────────────────────

    [Theory]
    [InlineData("eleven_voice_clone", "elevenlabs")]
    [InlineData("eleven_multilingual_v2", "elevenlabs")] // TTS id still maps to Eleven strategy via provider
    [InlineData("fal-ai/minimax/voice-clone", "fal")]
    [InlineData("fal-ai/minimax/speech-02-hd", "fal")]
    public void CanHandle_matches_catalog_provider(string modelId, string expectedProvider)
    {
        var entry = SupportedModelCatalog.Find(modelId, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(modelId);
        Assert.NotNull(entry);

        var fal = new FalVoiceApplyStrategy(
            new FakeVoiceCloneClient(configured: true),
            _previews,
            new SimpleHttpFactory(),
            NullLogger<FalVoiceApplyStrategy>.Instance);
        var eleven = new ElevenLabsVoiceApplyStrategy(
            new FakeVoiceClient(configured: true),
            _previews,
            NullLogger<ElevenLabsVoiceApplyStrategy>.Instance);

        IVoiceApplyStrategy[] strategies = [fal, eleven];
        var picked = strategies.First(s => s.CanHandle(entry));
        Assert.Equal(expectedProvider, picked.ProviderId);
    }

    [Fact]
    public void CanHandle_null_model_claims_neither_provider()
    {
        var fal = new FalVoiceApplyStrategy(
            new FakeVoiceCloneClient(configured: true),
            _previews,
            new SimpleHttpFactory(),
            NullLogger<FalVoiceApplyStrategy>.Instance);
        var eleven = new ElevenLabsVoiceApplyStrategy(
            new FakeVoiceClient(configured: true),
            _previews,
            NullLogger<ElevenLabsVoiceApplyStrategy>.Instance);

        // No invent — Settings must select a clone model first.
        Assert.False(fal.CanHandle(null));
        Assert.False(eleven.CanHandle(null));
    }

    // ── ElevenLabs strategy ──────────────────────────────────────────────

    [Fact]
    public async Task ElevenLabs_strategy_clones_and_writes_preview_and_seed()
    {
        var project = await _store.CreateProjectAsync("el-demo", title: "EL Demo");
        var samplePath = await WriteSampleAsync(project.Id, "Character_Narrator");

        var client = new FakeVoiceClient(configured: true)
        {
            CloneResult = new VoiceCloneResult
            {
                Ok = true,
                ProviderVoiceId = "el_voice_abc",
                UsedMock = false,
            },
            TtsResult = new VoiceTtsResult
            {
                Ok = true,
                AudioBytes = MockToneWav.Sine(0.5, 220),
                ContentType = "audio/wav",
                FileExtension = ".wav",
                UsedMock = false,
            },
        };

        var strategy = new ElevenLabsVoiceApplyStrategy(
            client, _previews, NullLogger<ElevenLabsVoiceApplyStrategy>.Instance);

        var result = await strategy.ApplyAsync(new VoiceApplyContext
        {
            ProjectId = project.Id,
            CharKey = "Character_Narrator",
            DisplayName = "Narrator",
            SamplePath = samplePath,
            CloneModel = SupportedModelCatalog.Find("eleven_voice_clone", ModelCapability.Voice),
            SpeakModel = SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice),
            PreviewText = "Hello from the test.",
            VoiceLabel = "Test clone",
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("elevenlabs", result.ProviderId);
        Assert.Equal("el_voice_abc", result.ProviderVoiceId);
        Assert.False(result.UsedMock);
        Assert.Equal("Test clone", result.VoiceLabel);
        Assert.NotNull(result.PreviewUrl);

        // Seed dual-written for Fal speak interop
        Assert.Equal("el_voice_abc", _store.GetVoiceCloneProviderId(project.Id, "Character_Narrator"));
        Assert.True(File.Exists(_previews.GetTtsPreviewPath(project.Id, "Character_Narrator")));

        // Client was invoked once each
        Assert.Equal(1, client.CloneCalls);
        Assert.Equal(1, client.TtsCalls);
        Assert.Equal("Hello from the test.", client.LastTtsText);
    }

    [Fact]
    public async Task ElevenLabs_strategy_surfaces_clone_failure()
    {
        var project = await _store.CreateProjectAsync("el-fail", title: "EL Fail");
        var samplePath = await WriteSampleAsync(project.Id, "Character_Narrator");

        var client = new FakeVoiceClient(configured: true)
        {
            CloneResult = new VoiceCloneResult { Ok = false, Error = "quota exceeded" },
        };
        var strategy = new ElevenLabsVoiceApplyStrategy(
            client, _previews, NullLogger<ElevenLabsVoiceApplyStrategy>.Instance);

        var result = await strategy.ApplyAsync(new VoiceApplyContext
        {
            ProjectId = project.Id,
            CharKey = "Character_Narrator",
            DisplayName = "Narrator",
            SamplePath = samplePath,
        });

        Assert.False(result.Ok);
        Assert.Contains("quota", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.TtsCalls); // no TTS after failed clone
    }

    // ── Fal strategy ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fal_strategy_requires_configuration()
    {
        var project = await _store.CreateProjectAsync("fal-off", title: "Fal Off");
        var samplePath = await WriteSampleAsync(project.Id, "Character_Narrator");

        var strategy = new FalVoiceApplyStrategy(
            new FakeVoiceCloneClient(configured: false),
            _previews,
            new SimpleHttpFactory(),
            NullLogger<FalVoiceApplyStrategy>.Instance);

        var result = await strategy.ApplyAsync(new VoiceApplyContext
        {
            ProjectId = project.Id,
            CharKey = "Character_Narrator",
            DisplayName = "Narrator",
            SamplePath = samplePath,
            CloneModel = SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice),
        });

        Assert.False(result.Ok);
        Assert.Contains("FAL_API_KEY", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fal_strategy_clones_and_persists_provider_id()
    {
        var project = await _store.CreateProjectAsync("fal-demo", title: "Fal Demo");
        var samplePath = await WriteSampleAsync(project.Id, "Character_Narrator");

        var client = new FakeVoiceCloneClient(configured: true)
        {
            CloneVoiceId = "minimax_custom_99",
            // No audio URL → preview optional; clone seed still required
            SpeakAudioUrl = null,
        };

        var strategy = new FalVoiceApplyStrategy(
            client, _previews, new SimpleHttpFactory(), NullLogger<FalVoiceApplyStrategy>.Instance);

        var result = await strategy.ApplyAsync(new VoiceApplyContext
        {
            ProjectId = project.Id,
            CharKey = "Character_Narrator",
            DisplayName = "Narrator",
            SamplePath = samplePath,
            CloneModel = SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice),
            SpeakModel = SupportedModelCatalog.Find("fal-ai/minimax/speech-02-hd", ModelCapability.Voice),
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("fal", result.ProviderId);
        Assert.Equal("minimax_custom_99", result.ProviderVoiceId);
        Assert.Equal("fal-ai/minimax/voice-clone", result.ModelId);
        Assert.Equal("minimax_custom_99", _store.GetVoiceCloneProviderId(project.Id, "Character_Narrator"));
        Assert.Equal(1, client.CloneCalls);
        Assert.Equal(1, client.SpeakCalls);
    }

    // ── Orchestrator (VoiceCloneApplyService) examples ───────────────────

    [Fact]
    public async Task Orchestrator_selects_fal_strategy_when_model_is_fal()
    {
        var project = await _store.CreateProjectAsync("orch-fal", title: "Orch Fal");
        await _store.SaveConfigAsync(
            project.Id,
            JsonSerializer.SerializeToElement(new { voice_model_name = "fal-ai/minimax/voice-clone" }));

        var falClient = new FakeVoiceCloneClient(configured: true) { CloneVoiceId = "via_fal" };
        var elClient = new FakeVoiceClient(configured: true)
        {
            CloneResult = new VoiceCloneResult { Ok = true, ProviderVoiceId = "via_el", UsedMock = true },
        };

        var apply = BuildOrchestrator(elClient, falClient);
        var sample = MockToneWav.Sine(1.0, 200);
        var result = await apply.ApplyFromSampleAsync(
            project.Id, "Character_Narrator",
            sampleOverride: sample,
            sampleFileName: "voice_clone_sample.wav");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("fal", result.ProviderId);
        Assert.Equal("via_fal", result.ProviderVoiceId);
        Assert.Equal(1, falClient.CloneCalls);
        Assert.Equal(0, elClient.CloneCalls); // Eleven not used
    }

    [Fact]
    public async Task Orchestrator_does_not_auto_fallback_when_fal_unconfigured()
    {
        var project = await _store.CreateProjectAsync("orch-fb", title: "Orch No Fallback");
        await _store.SaveConfigAsync(
            project.Id,
            JsonSerializer.SerializeToElement(new { voice_model_name = "fal-ai/minimax/voice-clone" }));

        var falClient = new FakeVoiceCloneClient(configured: false);
        var elClient = new FakeVoiceClient(configured: true)
        {
            CloneResult = new VoiceCloneResult
            {
                Ok = true,
                ProviderVoiceId = "mock_fallback",
                UsedMock = true,
            },
            TtsResult = new VoiceTtsResult
            {
                Ok = true,
                AudioBytes = MockToneWav.Sine(0.3, 180),
                FileExtension = ".wav",
                UsedMock = true,
            },
        };

        var apply = BuildOrchestrator(elClient, falClient);
        var result = await apply.ApplyFromSampleAsync(
            project.Id, "Character_Narrator",
            sampleOverride: MockToneWav.Sine(1.0, 190),
            sampleFileName: "voice_clone_sample.wav");

        Assert.False(result.Ok);
        Assert.Contains("No working API key", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not switch providers", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, falClient.CloneCalls);
        Assert.Equal(0, elClient.CloneCalls); // must not silently use ElevenLabs
    }

    // ── VoicePreviewStore example ────────────────────────────────────────

    [Fact]
    public async Task PreviewStore_write_then_get_round_trips()
    {
        var project = await _store.CreateProjectAsync("preview", title: "Preview");
        var bytes = MockToneWav.Sine(0.4, 250);
        var (rel, url) = await _previews.WriteAsync(
            project.Id, "Character_Narrator", bytes, ".wav");

        Assert.Contains("voice_preview_tts.wav", rel, StringComparison.Ordinal);
        Assert.Contains("tts-preview", url, StringComparison.Ordinal);

        var path = _previews.GetTtsPreviewPath(project.Id, "Character_Narrator");
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes.Length, new FileInfo(path!).Length);
    }

    // ── helpers / fakes ──────────────────────────────────────────────────

    private VoiceCloneApplyService BuildOrchestrator(IVoiceClient eleven, IVoiceCloneClient fal)
    {
        IVoiceApplyStrategy[] strategies =
        [
            new FalVoiceApplyStrategy(
                fal, _previews, new SimpleHttpFactory(), NullLogger<FalVoiceApplyStrategy>.Instance),
            new ElevenLabsVoiceApplyStrategy(
                eleven, _previews, NullLogger<ElevenLabsVoiceApplyStrategy>.Instance),
        ];
        return new VoiceCloneApplyService(
            _store, _previews, strategies, eleven, NullLogger<VoiceCloneApplyService>.Instance);
    }

    private async Task<string> WriteSampleAsync(string projectId, string charKey)
    {
        var wav = MockToneWav.Sine(1.5, 200);
        await using var ms = new MemoryStream(wav);
        return await _store.SaveVoiceCloneSampleAsync(projectId, charKey, ms, "voice_clone_sample.wav");
    }

    /// <summary>Minimal <see cref="IVoiceClient"/> fake for strategy unit tests.</summary>
    private sealed class FakeVoiceClient : IVoiceClient
    {
        public FakeVoiceClient(bool configured) => IsConfigured = configured;

        public bool IsConfigured { get; }
        public string ProviderId => "elevenlabs";
        public int CloneCalls { get; private set; }
        public int TtsCalls { get; private set; }
        public string? LastTtsText { get; private set; }

        public VoiceCloneResult CloneResult { get; set; } = new() { Ok = false, Error = "not set" };
        public VoiceTtsResult TtsResult { get; set; } = new() { Ok = false, Error = "not set" };

        public Task<VoiceCloneResult> CreateCloneAsync(
            string displayName, byte[] sampleAudio, string sampleFileName, CancellationToken ct = default)
        {
            CloneCalls++;
            return Task.FromResult(CloneResult);
        }

        public Task<VoiceTtsResult> TextToSpeechAsync(
            string providerVoiceId, string text, string? modelId = null, CancellationToken ct = default)
        {
            TtsCalls++;
            LastTtsText = text;
            return Task.FromResult(TtsResult);
        }

        public Task<IReadOnlyList<VoiceCatalogEntry>> ListVoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceCatalogEntry>>(Array.Empty<VoiceCatalogEntry>());
    }

    /// <summary>Minimal <see cref="IVoiceCloneClient"/> fake for Fal strategy unit tests.</summary>
    private sealed class FakeVoiceCloneClient : IVoiceCloneClient
    {
        public FakeVoiceCloneClient(bool configured) => IsConfigured = configured;

        public bool IsConfigured { get; }
        public int CloneCalls { get; private set; }
        public int SpeakCalls { get; private set; }
        public string? CloneVoiceId { get; set; }
        public string? SpeakAudioUrl { get; set; }

        public Task<string?> CloneVoiceAsync(
            string sampleAudioPath, string? model = null, CancellationToken ct = default)
        {
            CloneCalls++;
            return Task.FromResult(CloneVoiceId);
        }

        public Task<string?> SynthesizeSpeechAsync(
            string text, string voiceId, string? model = null, CancellationToken ct = default)
        {
            SpeakCalls++;
            return Task.FromResult(SpeakAudioUrl);
        }
    }

    private sealed class SimpleHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

public class ElevenLabsErrorFormattingTests
{
    [Fact]
    public void FormatCloneError_missing_instant_clone_permission_is_actionable()
    {
        var body =
            "{\"detail\":{\"type\":\"authentication_error\",\"code\":\"unauthorized\"," +
            "\"message\":\"The API key you used is missing the permission create_instant_voice_clone to execute this operation.\"," +
            "\"status\":\"missing_permissions\",\"request_id\":\"abc\"}}";
        var msg = ElevenLabsVoiceClient.FormatCloneError(401, body);
        Assert.Contains("Instant Voice Clone", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request_id", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choose another voice model", msg, StringComparison.OrdinalIgnoreCase);
    }
}
