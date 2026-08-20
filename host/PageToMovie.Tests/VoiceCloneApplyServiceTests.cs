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
/// No-key apply path: fail closed, keep the sample, never hop providers or call a live API.
/// Instant-clone permission errors belong in LiveApi tests (they need a real key).
/// </summary>
[Collection("catalog-serial")]
public class VoiceCloneApplyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly VoiceCloneApplyService _apply;

    public VoiceCloneApplyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-voice-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var previews = new VoicePreviewStore(_store);

        // Mock TTS/clone would hide a missing key — this suite asserts the fail-closed path.
        var elHttp = new HttpClient { BaseAddress = new Uri("https://api.elevenlabs.io/v1/") };
        IVoiceClient eleven = new ElevenLabsVoiceClient(
            elHttp, NullLogger<ElevenLabsVoiceClient>.Instance, allowMockFallback: false);

        var falHttp = new HttpClient { BaseAddress = new Uri("https://queue.fal.run/") };
        IVoiceCloneClient fal = new FalVoiceCloneClient(falHttp, NullLogger<FalVoiceCloneClient>.Instance);

        IVoiceApplyStrategy[] strategies =
        [
            new FalVoiceApplyStrategy(
                fal, previews, new SimpleFactory(), NullLogger<FalVoiceApplyStrategy>.Instance),
            new ElevenLabsVoiceApplyStrategy(
                eleven, previews, NullLogger<ElevenLabsVoiceApplyStrategy>.Instance),
        ];

        _apply = new VoiceCloneApplyService(
            _store,
            previews,
            strategies,
            eleven,
            NullLogger<VoiceCloneApplyService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ApplyFromSample_does_not_mock_or_switch_when_eleven_key_is_missing()
    {
        var clone = RequireVoiceCloneModel("elevenlabs");
        if (HasAnyRequiredKey(clone))
            return; // live key — do not hit a paid Instant Voice Clone endpoint from a default unit test

        var p = await _store.CreateProjectAsync("tthv7", title: "Tell-Tale Heart V7");
        await _store.SaveConfigAsync(
            p.Id, JsonSerializer.SerializeToElement(new { voice_model_name = clone.Id }));

        var sample = MockToneWav.Sine(2.5, 195);
        var result = await _apply.ApplyFromSampleAsync(
            p.Id,
            "Character_Narrator",
            sampleOverride: sample,
            sampleFileName: "voice_clone_sample.wav",
            previewText: "True! nervous — very, very dreadfully nervous I had been and am.");

        Assert.False(result.Ok);
        Assert.Contains("No working API key", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not switch providers", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_store.GetVoiceCloneSamplePath(p.Id, "Character_Narrator")));
    }

    [Fact]
    public async Task ApplyFromSample_fal_model_without_key_fails_without_auto_fallback()
    {
        var clone = RequireVoiceCloneModel("fal");
        if (HasAnyRequiredKey(clone) || FalClientLooksConfigured())
            return; // live key — do not enqueue a paid Fal clone from a default unit test

        var p = await _store.CreateProjectAsync("buster", title: "Buster");
        await _store.SaveConfigAsync(
            p.Id, JsonSerializer.SerializeToElement(new { voice_model_name = clone.Id }));

        var sample = MockToneWav.Sine(2.0, 180);
        var result = await _apply.ApplyFromSampleAsync(
            p.Id,
            "Character_Narrator",
            sampleOverride: sample,
            sampleFileName: "voice_clone_sample.wav");

        Assert.False(result.Ok);
        Assert.Contains("No working API key", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not switch providers", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        // Sample still saved for a later retry after the user fixes key/model.
        Assert.True(File.Exists(_store.GetVoiceCloneSamplePath(p.Id, "Character_Narrator")));
    }

    private static SupportedModelEntry RequireVoiceCloneModel(string providerId)
    {
        var entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => m.IsVoiceCloneStep
                                 && m.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        return entry;
    }

    private static bool HasAnyRequiredKey(SupportedModelEntry entry) =>
        entry.RequiredEnvKeys.Any(k => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(k)));

    /// <summary>
    /// Fal's client treats either catalog env name as configured; skip if that would call the network.
    /// </summary>
    private static bool FalClientLooksConfigured()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://queue.fal.run/") };
        var client = new FalVoiceCloneClient(http, NullLogger<FalVoiceCloneClient>.Instance);
        return client.IsConfigured;
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
