using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.VoiceApply;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// xAI Custom Voices + TTS: catalog rows and mocked HTTP. No live api.x.ai calls.
/// </summary>
[Collection("catalog-serial")]
public class XaiVoiceCloneClientTests
{
    public XaiVoiceCloneClientTests() => SupportedModelCatalog.ReloadCatalog();

    [Fact]
    public void Voice_capability_default_stays_on_the_existing_clone_model()
    {
        Assert.Equal("eleven_voice_clone", SupportedModelCatalog.DefaultModelIdForCapability("voice"));
    }

    [Fact]
    public void Xai_voice_rows_are_provisional_english_clone_and_speak_steps()
    {
        var rows = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .Where(e => e.Provider == ModelProviderFamily.Xai)
            .ToList();
        var clone = Assert.Single(rows, e => e.IsVoiceCloneStep);
        var speak = Assert.Single(rows, e => !e.IsVoiceCloneStep);

        Assert.Equal("xai-custom-voices", clone.Id);
        Assert.Equal("custom-voices", clone.EndpointPath);
        Assert.Null(clone.CostPerCloneUsd);
        Assert.Null(clone.CostPerThousandCharsUsd);
        Assert.Equal(60000, clone.MaxPromptLength);
        Assert.Contains("English", clone.DisplayName, StringComparison.Ordinal);
        Assert.Contains("provisional", clone.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unofficial", clone.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Enterprise", clone.Notes, StringComparison.Ordinal);

        Assert.Equal("xai-tts", speak.Id);
        Assert.Equal("tts", speak.EndpointPath);
        Assert.Equal(0.015, speak.CostPerThousandCharsUsd!.Value, 6);
        Assert.Equal(60000, speak.MaxPromptLength);
        Assert.False(speak.IsVoiceCloneStep);
        Assert.Contains("English", speak.DisplayName, StringComparison.Ordinal);
        Assert.Contains("provisional", speak.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unofficial", speak.Notes, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ModelProviderFamily.Xai, clone.Provider);
        Assert.Equal(ModelProviderFamily.Xai, speak.Provider);
        Assert.Equal(SupportedModelCatalog.XaiApiBase, clone.ApiBase.TrimEnd('/'));
        Assert.Equal(SupportedModelCatalog.XaiApiBase, speak.ApiBase.TrimEnd('/'));
        Assert.Equal(clone.ProviderId, speak.ProviderId);
        Assert.Contains(clone.RequiredEnvKeys, k => !string.IsNullOrWhiteSpace(k));
    }

    [Theory]
    [InlineData("sample.wav", "audio/wav")]
    [InlineData("sample.mp3", "audio/mpeg")]
    [InlineData("sample.flac", "audio/flac")]
    [InlineData("sample.ogg", "audio/ogg")]
    [InlineData("sample.opus", "audio/opus")]
    [InlineData("sample.m4a", "audio/mp4")]
    [InlineData("sample.aac", "audio/aac")]
    [InlineData("sample.mkv", "video/x-matroska")]
    [InlineData("sample.unknownext", "application/octet-stream")]
    public void Audio_content_type_matches_extension(string fileName, string expected)
    {
        Assert.Equal(expected, XaiVoiceCloneClient.ResolveAudioContentType(fileName));
    }

    [Fact]
    public void Client_reports_not_configured_without_an_xai_key()
    {
        using var held = HoldXaiKey(null);
        using var http = new HttpClient();
        var client = NewClient(http);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public async Task Clone_posts_english_multipart_and_returns_voice_id()
    {
        using var held = HoldXaiKey("xai-test-key");
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"voice_id":"nlbqfwie","name":"sample"}""");
        using var http = ClientHttp(handler);
        var client = NewClient(http);
        var sample = WriteSample("voice_clone_sample.wav");
        try
        {
            var voiceId = await client.CloneVoiceAsync(sample, XaiClone().Id);
            Assert.Equal("nlbqfwie", voiceId);
            Assert.Equal(1, handler.Calls);
            Assert.EndsWith("/custom-voices", handler.Path, StringComparison.Ordinal);
            Assert.Equal("Bearer xai-test-key", handler.Auth);
            Assert.Contains("name=language", handler.RequestBody, StringComparison.Ordinal);
            Assert.Contains("name=file", handler.RequestBody, StringComparison.Ordinal);
            Assert.Contains("\r\n" + XaiVoiceCloneClient.EnglishLanguage + "\r\n", handler.RequestBody, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(sample);
        }
    }

    [Fact]
    public async Task Clone_403_names_the_enterprise_gate()
    {
        using var held = HoldXaiKey("xai-test-key");
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"error":"enterprise"}""");
        using var http = ClientHttp(handler);
        var client = NewClient(http);
        var sample = WriteSample("ref.wav");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.CloneVoiceAsync(sample, XaiClone().Id));
            Assert.Contains("Enterprise", ex.Message, StringComparison.Ordinal);
            Assert.Contains("United States", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Illinois", ex.Message, StringComparison.Ordinal);
            Assert.Contains(XaiVoiceCloneClient.EnterpriseCustomVoiceError, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(sample);
        }
    }

    [Fact]
    public async Task Speak_posts_text_voice_and_english_and_returns_audio_bytes()
    {
        using var held = HoldXaiKey("xai-test-key");
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new RecordingHandler(HttpStatusCode.OK, audio, "audio/mpeg");
        using var http = ClientHttp(handler);
        var client = NewClient(http);

        var located = await client.SynthesizeSpeechAsync("Hello [laugh]", "nlbqfwie", XaiSpeak().Id);

        Assert.True(VoiceSpeechPayload.TryDecodeDataUri(located, out var bytes, out var mime));
        Assert.Equal(audio, bytes);
        Assert.Equal("audio/mpeg", mime);
        Assert.EndsWith("/tts", handler.Path, StringComparison.Ordinal);
        Assert.Equal("Bearer xai-test-key", handler.Auth);
        using var doc = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("Hello [laugh]", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("nlbqfwie", doc.RootElement.GetProperty("voice_id").GetString());
        Assert.Equal(XaiVoiceCloneClient.EnglishLanguage, doc.RootElement.GetProperty("language").GetString());
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Speak_rejects_text_over_the_catalog_character_cap_without_calling_xai()
    {
        using var held = HoldXaiKey("xai-test-key");
        var handler = new RecordingHandler(HttpStatusCode.OK, "nope");
        using var http = ClientHttp(handler);
        var client = NewClient(http);
        var cap = XaiSpeak().MaxPromptLength!.Value;
        var text = new string('a', cap + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeSpeechAsync(text, "nlbqfwie", XaiSpeak().Id));

        Assert.Contains(cap.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Router_sends_xai_models_to_xai_and_fal_models_to_fal()
    {
        using var held = HoldXaiKey("xai-test-key");
        using var falHeld = HoldEnv("FAL_API_KEY", "fal-test-key");
        var xaiHandler = new RecordingHandler(HttpStatusCode.Created, """{"voice_id":"nlbqfwie"}""");
        var falHandler = new RecordingHandler(HttpStatusCode.BadRequest, "nope");
        using var xaiHttp = ClientHttp(xaiHandler);
        using var falHttp = new HttpClient(falHandler) { BaseAddress = new Uri(FalVoiceCloneClient.ApiBase.TrimEnd('/') + "/") };
        var router = new RoutingVoiceCloneClient(
            new FalVoiceCloneClient(falHttp, NullLogger<FalVoiceCloneClient>.Instance),
            NewClient(xaiHttp));
        var sample = WriteSample("route.wav");
        try
        {
            var voiceId = await router.CloneVoiceAsync(sample, XaiClone().Id);
            Assert.Equal("nlbqfwie", voiceId);
            Assert.Equal(1, xaiHandler.Calls);
            Assert.Equal(0, falHandler.Calls);

            await router.CloneVoiceAsync(sample, "fal-ai/minimax/voice-clone");
            Assert.Equal(1, xaiHandler.Calls);
            Assert.Equal(1, falHandler.Calls);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                router.CloneVoiceAsync(sample, "eleven_voice_clone"));
            Assert.Equal(1, xaiHandler.Calls);
            Assert.Equal(1, falHandler.Calls);
        }
        finally
        {
            TryDelete(sample);
        }
    }

    [Fact]
    public void Router_ready_follows_the_selected_provider_key()
    {
        using var http = new HttpClient();
        using var falHttp = new HttpClient();
        var router = new RoutingVoiceCloneClient(
            new FalVoiceCloneClient(falHttp, NullLogger<FalVoiceCloneClient>.Instance),
            NewClient(http));
        using var xai = HoldXaiKey(null);
        using var fal = HoldEnv("FAL_API_KEY", null);
        using var falAlias = HoldEnv("FAL_KEY", null);

        Assert.False(router.IsReady(XaiClone()));
        Assert.False(router.IsReady(SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)));

        using (ApiKeyScope.Push("only-xai"))
        {
            Assert.True(router.IsReady(XaiClone()));
            Assert.False(router.IsReady(SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)));
        }
    }

    [Fact]
    public async Task Strategy_surfaces_enterprise_error_and_does_not_fall_through()
    {
        var storeRoot = NewStore(out var store, out var previews);
        try
        {
            var xai = new ScriptedCloneClient
            {
                Configured = true,
                CloneError = new InvalidOperationException(XaiVoiceCloneClient.EnterpriseCustomVoiceError),
            };
            var elevenCalls = 0;
            var strategies = new IVoiceApplyStrategy[]
            {
                new FalVoiceApplyStrategy(new ScriptedCloneClient { Configured = true }, previews, new ThrowingFactory(), NullLogger<FalVoiceApplyStrategy>.Instance),
                new XaiVoiceApplyStrategy(xai, previews, new ThrowingFactory(), NullLogger<XaiVoiceApplyStrategy>.Instance),
                new ElevenLabsVoiceApplyStrategy(new CountingVoiceClient(() => elevenCalls++), previews, NullLogger<ElevenLabsVoiceApplyStrategy>.Instance),
            };
            var apply = new VoiceCloneApplyService(
                store, previews, strategies, new CountingVoiceClient(() => elevenCalls++), NullLogger<VoiceCloneApplyService>.Instance);
            var project = await store.CreateProjectAsync("xai-en", title: "English");
            await store.SaveConfigAsync(project.Id, JsonSerializer.SerializeToElement(new { voice_model_name = XaiClone().Id }));

            var result = await apply.ApplyFromSampleAsync(
                project.Id, "Character_Narrator",
                sampleOverride: [1, 2, 3, 4],
                sampleFileName: "voice_clone_sample.wav");

            Assert.False(result.Ok);
            Assert.Contains("Enterprise", result.Error, StringComparison.Ordinal);
            Assert.Equal(0, xai.SpeakCalls);
            Assert.Equal(0, elevenCalls);
        }
        finally
        {
            TryDeleteDir(storeRoot);
        }
    }

    [Fact]
    public async Task Strategy_can_handle_only_xai_and_saves_an_english_clone()
    {
        var root = NewStore(out var store, out var previews);
        var xaiStrategy = new XaiVoiceApplyStrategy(
            new ScriptedCloneClient { Configured = false },
            previews,
            new ThrowingFactory(),
            NullLogger<XaiVoiceApplyStrategy>.Instance);
        Assert.True(xaiStrategy.CanHandle(XaiClone()));
        Assert.True(xaiStrategy.CanHandle(XaiSpeak()));
        Assert.False(xaiStrategy.CanHandle(null));
        Assert.False(xaiStrategy.CanHandle(SupportedModelCatalog.Find("fal-ai/minimax/voice-clone", ModelCapability.Voice)));
        Assert.False(xaiStrategy.CanHandle(SupportedModelCatalog.Find("eleven_voice_clone", ModelCapability.Voice)));

        try
        {
            var audio = VoiceSpeechPayload.ToDataUri(new byte[] { 9, 8, 7 }, "audio/mpeg");
            var client = new ScriptedCloneClient
            {
                Configured = true,
                CloneVoiceId = "nlbqfwie",
                SpeakAudioUrl = audio,
            };
            var strategy = new XaiVoiceApplyStrategy(
                client, previews, new ThrowingFactory(), NullLogger<XaiVoiceApplyStrategy>.Instance);
            var project = await store.CreateProjectAsync("xai-ok", title: "English ok");
            var sample = await store.SaveVoiceCloneSampleAsync(
                project.Id, "Character_Narrator", new MemoryStream([1, 2, 3, 4]), "voice_clone_sample.wav");

            var result = await strategy.ApplyAsync(new VoiceApplyContext
            {
                ProjectId = project.Id,
                CharKey = "Character_Narrator",
                DisplayName = "Narrator",
                SamplePath = sample,
                CloneModel = XaiClone(),
                SpeakModel = XaiSpeak(),
            });

            Assert.True(result.Ok, result.Error);
            Assert.Equal(XaiClone().ProviderId, result.ProviderId);
            Assert.Equal("nlbqfwie", result.ProviderVoiceId);
            Assert.Equal(XaiClone().Id, result.ModelId);
            Assert.Contains("English", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, client.SpeakCalls);
            Assert.Equal(XaiSpeak().Id, client.LastSpeakModel);
            Assert.NotNull(previews.GetTtsPreviewPath(project.Id, "Character_Narrator"));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static SupportedModelEntry XaiClone() =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .Single(m => m.Provider == ModelProviderFamily.Xai && m.IsVoiceCloneStep);

    private static SupportedModelEntry XaiSpeak() =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .Single(m => m.Provider == ModelProviderFamily.Xai && !m.IsVoiceCloneStep);

    private static XaiVoiceCloneClient NewClient(HttpClient http) =>
        new(http, NullLogger<XaiVoiceCloneClient>.Instance);

    private static HttpClient ClientHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri(SupportedModelCatalog.XaiApiBase.TrimEnd('/') + "/") };

    private static string WriteSample(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-voice-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
        return path;
    }

    private static string NewStore(out ProjectStore store, out VoicePreviewStore previews)
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-voice-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        store = new ProjectStore(Microsoft.Extensions.Options.Options.Create(
            new PageToMovie.Core.Options.PageToMovieOptions { WorkspaceRoot = root }));
        previews = new VoicePreviewStore(store);
        return root;
    }

    private static KeyHold HoldXaiKey(string? value)
    {
        var entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .First(m => m.Provider == ModelProviderFamily.Xai);
        var holds = entry.RequiredEnvKeys.Select(k => HoldEnv(k, value)).ToArray();
        var scope = ApiKeyScope.Push(value);
        return new KeyHold([scope, .. holds]);
    }

    private static KeyHold HoldEnv(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new KeyHold([new EnvRestore(name, previous)]);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* temp */ }
    }

    private sealed class KeyHold : IDisposable
    {
        private readonly IDisposable[] _items;
        public KeyHold(IDisposable[] items) => _items = items;
        public void Dispose()
        {
            foreach (var item in _items)
                item.Dispose();
        }
    }

    private sealed class EnvRestore : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;
        public EnvRestore(string name, string? previous)
        {
            _name = name;
            _previous = previous;
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private readonly string _contentType;

        public RecordingHandler(HttpStatusCode status, string body, string contentType = "application/json")
            : this(status, Encoding.UTF8.GetBytes(body), contentType)
        {
        }

        public RecordingHandler(HttpStatusCode status, byte[] body, string contentType)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
        }

        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? Auth { get; private set; }
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri?.AbsolutePath;
            Auth = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var response = new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            return response;
        }
    }

    private sealed class ScriptedCloneClient : IVoiceCloneClient
    {
        public bool Configured { get; init; }
        public string? CloneVoiceId { get; init; }
        public Exception? CloneError { get; init; }
        public string? SpeakAudioUrl { get; init; }
        public int SpeakCalls { get; private set; }
        public string? LastSpeakModel { get; private set; }
        public bool IsConfigured => Configured;

        public Task<string?> CloneVoiceAsync(string sampleAudioPath, string? model = null, CancellationToken ct = default)
        {
            if (CloneError is not null)
                throw CloneError;
            return Task.FromResult(CloneVoiceId);
        }

        public Task<string?> SynthesizeSpeechAsync(string text, string voiceId, string? model = null, CancellationToken ct = default)
        {
            SpeakCalls++;
            LastSpeakModel = model;
            return Task.FromResult(SpeakAudioUrl);
        }
    }

    private sealed class CountingVoiceClient : IVoiceClient
    {
        private readonly Action _onClone;
        public CountingVoiceClient(Action onClone) => _onClone = onClone;
        public string ProviderId => "elevenlabs";
        public bool IsConfigured => true;
        public Task<VoiceCloneResult> CreateCloneAsync(string displayName, byte[] sampleAudio, string sampleFileName, CancellationToken ct = default)
        {
            _onClone();
            return Task.FromResult(new VoiceCloneResult { Ok = true, ProviderVoiceId = "el", UsedMock = true });
        }
        public Task<VoiceTtsResult> TextToSpeechAsync(string providerVoiceId, string text, string? modelId = null, CancellationToken ct = default) =>
            Task.FromResult(new VoiceTtsResult { Ok = true, AudioBytes = [1], FileExtension = ".mp3", UsedMock = true });
        public Task<IReadOnlyList<VoiceCatalogEntry>> ListVoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceCatalogEntry>>([]);
    }

    private sealed class ThrowingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP download is not used for a data URI preview.");
    }
}
