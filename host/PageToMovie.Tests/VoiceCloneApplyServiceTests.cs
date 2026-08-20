using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.VoiceApply;
using Xunit;

namespace PageToMovie.Tests;

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
        using var _ = ClearVoiceKeys();
        var p = await _store.CreateProjectAsync("tthv7", title: "Tell-Tale Heart V7");
        await _store.SaveConfigAsync(
            p.Id, JsonSerializer.SerializeToElement(new { voice_model_name = "eleven_voice_clone" }));

        var sample = MockToneWav.Sine(2.5, 195);
        var result = await _apply.ApplyFromSampleAsync(
            p.Id,
            "Character_Narrator",
            sampleOverride: sample,
            sampleFileName: "voice_clone_sample.wav",
            previewText: "True! nervous — very, very dreadfully nervous I had been and am.");

        Assert.False(result.Ok);
        Assert.Contains("No working API key", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("elevenlabs", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fal", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_store.GetVoiceCloneSamplePath(p.Id, "Character_Narrator")));
    }

    [Fact]
    public async Task ApplyFromSample_fal_model_without_key_fails_without_auto_fallback()
    {
        using var _ = ClearVoiceKeys();
        var p = await _store.CreateProjectAsync("buster", title: "Buster");
        await _store.SaveConfigAsync(
            p.Id, JsonSerializer.SerializeToElement(new { voice_model_name = "fal-ai/minimax/voice-clone" }));

        var sample = MockToneWav.Sine(2.0, 180);
        var result = await _apply.ApplyFromSampleAsync(
            p.Id,
            "Character_Narrator",
            sampleOverride: sample,
            sampleFileName: "voice_clone_sample.wav");

        Assert.False(result.Ok);
        Assert.Contains("No working API key", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fal", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eleven", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        // Sample still saved for a later retry after the user fixes key/model.
        Assert.True(File.Exists(_store.GetVoiceCloneSamplePath(p.Id, "Character_Narrator")));
    }

    private static IDisposable ClearVoiceKeys()
    {
        string[] names = ["ElevenLabs_API_KEY", "ELEVENLABS_API_KEY", "FAL_API_KEY", "FAL_KEY"];
        var prev = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        foreach (var n in names)
            Environment.SetEnvironmentVariable(n, null);
        return new RestoreEnv(prev);
    }

    private sealed class RestoreEnv(Dictionary<string, string?> prev) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (k, v) in prev)
                Environment.SetEnvironmentVariable(k, v);
        }
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
