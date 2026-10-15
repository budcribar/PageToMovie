using System.Net;
using System.Text;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// xAI batch speech-to-text against a mocked HTTP handler, plus catalog dispatch.
/// The transcription capability default stays ElevenLabs.
/// </summary>
[Collection("catalog-serial")]
public class XaiSpeechToTextClientTests
{
    public XaiSpeechToTextClientTests() => SupportedModelCatalog.ReloadCatalog();

    [Fact]
    public void Catalog_default_stays_elevenlabs_and_xai_row_has_official_batch_price()
    {
        var defId = SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Transcription);
        var def = SupportedModelCatalog.Find(defId, ModelCapability.Transcription);
        Assert.NotNull(def);
        Assert.Equal(ModelProviderFamily.ElevenLabs, def!.Provider);
        Assert.Equal("speech-to-text", def.EndpointPath);

        var xai = SupportedModelCatalog.ForCapability(ModelCapability.Transcription)
            .Single(m => m.Provider == ModelProviderFamily.Xai);
        Assert.Equal("stt", xai.EndpointPath);
        Assert.Equal(SupportedModelCatalog.XaiApiBase, xai.ApiBase.TrimEnd('/'));
        Assert.Equal(0.10, xai.CostPerHourUsd);
        Assert.Equal("2026-10-14", xai.LastVerifiedAt);
        Assert.Equal("2026-10-14", xai.PricingLastReviewedAt);
        Assert.Contains("0.10", xai.PricingNotes, StringComparison.Ordinal);
        Assert.Contains("docs.x.ai/developers/models/speech-to-text", xai.PricingNotes, StringComparison.Ordinal);
        Assert.NotEqual(def.Id, xai.Id);
    }

    [Fact]
    public async Task Transcribe_posts_model_then_file_and_maps_word_timestamps()
    {
        var xai = XaiModel();
        var handler = new CaptureHandler(_ => JsonResponse(
            """{"text":"hello there","language":"en","duration":1.2,"words":[{"text":"hello","start":0.10,"end":0.40},{"text":"there","start":0.40,"end":0.80}]}"""));
        var client = NewXaiClient(handler);

        ScribeResult result;
        using (ApiKeyScope.Push(new Dictionary<string, string?> { ["grok"] = "xai-test-key" }))
        {
            result = await client.TranscribeAsync(WavBytes(), "segment.wav", "en", xai.Id);
        }

        Assert.True(result.Ok, result.Error);
        Assert.Equal("hello there", result.Text);
        Assert.Equal("en", result.LanguageCode);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("hello", result.Words[0].Text);
        Assert.Equal(0.10, result.Words[0].Start);
        Assert.Equal(0.40, result.Words[0].End);
        Assert.Equal("word", result.Words[0].Type);

        Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/stt", handler.Uri, StringComparison.Ordinal);
        Assert.Equal("Bearer xai-test-key", handler.Authorization);
        var modelAt = handler.Body.IndexOf("name=model", StringComparison.Ordinal);
        var fileAt = handler.Body.IndexOf("name=file", StringComparison.Ordinal);
        Assert.True(modelAt >= 0 && fileAt > modelAt, handler.Body);
        Assert.Contains(xai.Id, handler.Body, StringComparison.Ordinal);
        Assert.Contains("name=language", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=diarize", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_without_key_does_not_call_xai()
    {
        var xai = XaiModel();
        var handler = new CaptureHandler(_ => JsonResponse("{}"));
        var client = NewXaiClient(handler);
        var previous = Environment.GetEnvironmentVariable(SupportedModelCatalog.XaiApiKeyEnv);
        Environment.SetEnvironmentVariable(SupportedModelCatalog.XaiApiKeyEnv, null);
        try
        {
            using (ApiKeyScope.Push(new Dictionary<string, string?>()))
            {
                var result = await client.TranscribeAsync(WavBytes(), "segment.wav", modelId: xai.Id);
                Assert.False(result.Ok);
                Assert.Contains("no API key", result.Error, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SupportedModelCatalog.XaiApiKeyEnv, previous);
        }

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Transcribe_http_error_returns_failure()
    {
        var xai = XaiModel();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad audio\"}"),
        });
        var client = NewXaiClient(handler);
        using (ApiKeyScope.Push(new Dictionary<string, string?> { ["grok"] = "xai-test-key" }))
        {
            var result = await client.TranscribeAsync(WavBytes(), "segment.wav", modelId: xai.Id);
            Assert.False(result.Ok);
            Assert.Contains("400", result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Router_sends_selected_provider_and_leaves_default_on_elevenlabs()
    {
        var xai = XaiModel();
        var xaiHandler = new CaptureHandler(_ => JsonResponse(
            """{"text":"from xai","language":"en","words":[{"text":"from","start":0,"end":0.2}]}"""));
        var elevenHandler = new CaptureHandler(_ => JsonResponse(
            """{"text":"from scribe","language_code":"en","words":[{"text":"from","start":0,"end":0.2,"type":"word"}]}"""));
        var router = new MultiProviderTranscriptionClient(NewElevenClient(elevenHandler), NewXaiClient(xaiHandler));

        using (ApiKeyScope.Push(new Dictionary<string, string?>
        {
            ["grok"] = "xai-test-key",
            ["elevenlabs"] = "el-test-key",
        }))
        {
            var selected = await router.TranscribeAsync(WavBytes(), "segment.wav", modelId: xai.Id);
            Assert.True(selected.Ok, selected.Error);
            Assert.Equal("from xai", selected.Text);
            Assert.Equal(1, xaiHandler.Calls);
            Assert.Equal(0, elevenHandler.Calls);

            var fallback = await router.TranscribeAsync(WavBytes(), "segment.wav");
            Assert.True(fallback.Ok, fallback.Error);
            Assert.Equal("from scribe", fallback.Text);
            Assert.Equal(1, xaiHandler.Calls);
            Assert.Equal(1, elevenHandler.Calls);
            Assert.Contains("name=model_id", elevenHandler.Body, StringComparison.Ordinal);
            Assert.Contains("timestamps_granularity", elevenHandler.Body, StringComparison.Ordinal);
        }
    }

    private static SupportedModelEntry XaiModel() =>
        SupportedModelCatalog.ForCapability(ModelCapability.Transcription)
            .Single(m => m.Provider == ModelProviderFamily.Xai);

    private static XaiSpeechToTextClient NewXaiClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(SupportedModelCatalog.XaiApiBase + "/") },
            NullLogger<XaiSpeechToTextClient>.Instance);

    private static ElevenLabsScribeClient NewElevenClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase + "/") },
            NullLogger<ElevenLabsScribeClient>.Instance);

    private static byte[] WavBytes() => Encoding.ASCII.GetBytes(new string('a', 256));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public int Calls { get; private set; }
        public string Body { get; private set; } = "";
        public string? Authorization { get; private set; }
        public string? Uri { get; private set; }

        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return _reply(request);
        }
    }
}
