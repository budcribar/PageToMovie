using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Covers the parts of the new provider clients that don't require a live API call: response
/// parsing (given a sample payload, does it extract the right text/image/video-uri) and the
/// routing decision the multi-provider dispatchers make (via SupportedModelCatalog, which is
/// what their private Resolve() methods call — this exercises the same decision without
/// needing to construct a full HttpClient-backed client graph).
/// </summary>
[Collection("catalog-serial")]
public class MultiProviderClientTests
{
    // ── Anthropic response parsing ──────────────────────────────────────────

    [Fact]
    public void Anthropic_extracts_single_text_block()
    {
        using var doc = JsonDocument.Parse("""
            { "content": [{ "type": "text", "text": "hello world" }] }
            """);
        var text = AnthropicChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("hello world", text);
    }

    [Fact]
    public void Anthropic_joins_multiple_text_blocks_and_skips_non_text()
    {
        using var doc = JsonDocument.Parse("""
            { "content": [
                { "type": "text", "text": "first" },
                { "type": "tool_use", "id": "x", "name": "y", "input": {} },
                { "type": "text", "text": "second" }
            ] }
            """);
        var text = AnthropicChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("first\nsecond", text);
    }

    // ── Gemini chat response parsing ────────────────────────────────────────

    [Fact]
    public void Gemini_extracts_text_from_first_candidate()
    {
        using var doc = JsonDocument.Parse("""
            { "candidates": [
                { "content": { "role": "model", "parts": [{ "text": "hello" }, { "text": " world" }] } }
            ] }
            """);
        var text = GeminiChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("hello\n world", text);
    }

    [Fact]
    public void Gemini_falls_back_to_raw_json_when_shape_is_unrecognized()
    {
        using var doc = JsonDocument.Parse("""{ "unexpected": "shape" }""");
        var text = GeminiChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Contains("unexpected", text);
    }

    // ── Gemini image response parsing ───────────────────────────────────────

    [Fact]
    public void Gemini_image_extracts_inline_data_camelCase()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var json = $$"""
            { "candidates": [
                { "content": { "parts": [
                    { "inlineData": { "mimeType": "image/png", "data": "{{b64}}" } }
                ] } }
            ] }
            """;
        var bytes = GeminiImageClient.ExtractInlineImage(json);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }

    [Fact]
    public void Gemini_image_extracts_inline_data_snake_case_fallback()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9, 9 });
        var json = $$"""
            { "candidates": [
                { "content": { "parts": [
                    { "inline_data": { "mime_type": "image/png", "data": "{{b64}}" } }
                ] } }
            ] }
            """;
        var bytes = GeminiImageClient.ExtractInlineImage(json);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 9, 9 }, bytes);
    }

    [Fact]
    public void Gemini_image_returns_null_for_text_only_response()
    {
        var json = """{ "candidates": [{ "content": { "parts": [{ "text": "no image" }] } }] }""";
        Assert.Null(GeminiImageClient.ExtractInlineImage(json));
    }

    // ── Gemini video (Veo) operation response parsing ───────────────────────

    [Fact]
    public void Veo_extracts_uri_from_generateVideoResponse_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "generateVideoResponse": { "generatedSamples": [
                { "video": { "uri": "https://example.com/a.mp4" } }
            ] } } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/a.mp4", uri);
    }

    [Fact]
    public void Veo_extracts_uri_from_videos_array_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "videos": [{ "uri": "https://example.com/b.mp4" }] } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/b.mp4", uri);
    }

    [Fact]
    public void Veo_extracts_uri_from_single_video_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "video": { "uri": "https://example.com/c.mp4" } } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/c.mp4", uri);
    }

    [Fact]
    public void Veo_returns_null_when_not_done_or_shape_unrecognized()
    {
        using var notDone = JsonDocument.Parse("""{ "done": false }""");
        Assert.Null(GeminiVideoClient.ExtractVideoUri(notDone.RootElement));

        using var unknownShape = JsonDocument.Parse("""{ "done": true, "response": { "surprise": 1 } }""");
        Assert.Null(GeminiVideoClient.ExtractVideoUri(unknownShape.RootElement));
    }

    // ── Dispatcher routing decisions (via the same catalog call Resolve() makes) ──

    [Theory]
    [InlineData("claude-sonnet-5", ModelProviderFamily.Anthropic)]
    [InlineData("gemini-3.7-flash", ModelProviderFamily.Google)]
    [InlineData("grok-4.6", ModelProviderFamily.Xai)]
    [InlineData("grok-4.5", ModelProviderFamily.Xai)]
    public void Chat_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Chat).Provider;
        Assert.Equal(expected, provider);
    }

    [Fact]
    public void Chat_routing_unknown_model_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveOrDefault("some-unknown-future-model", ModelCapability.Chat));
    }

    [Theory]
    [InlineData("gemini-3-pro-image", ModelProviderFamily.Google)]
    [InlineData("grok-imagine-image-2.0", ModelProviderFamily.Xai)]
    public void Image_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Image).Provider;
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData("veo-3.1", ModelProviderFamily.Google)]
    [InlineData("grok-imagine-video", ModelProviderFamily.Xai)]
    public void Video_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).Provider;
        Assert.Equal(expected, provider);
    }

    // ── Video dispatcher: model id → catalog providerId → IVideoClient map ──

    [Fact]
    public void Video_routes_by_catalog_provider_id_not_family_default()
    {
        var xai = CatalogXaiVideo();
        var other = CatalogNonXaiVideo();
        var xaiClient = new StubVideoClient();
        var otherClient = new StubVideoClient();
        var facade = new MultiProviderVideoClient(new Dictionary<string, IVideoClient>
        {
            [xai.ProviderId] = xaiClient,
            [other.ProviderId] = otherClient,
        });

        Assert.Same(xaiClient, facade.ResolveClientForModel(xai.Id));
        Assert.Same(otherClient, facade.ResolveClientForModel(other.Id));
        Assert.NotSame(xaiClient, facade.ResolveClientForModel(other.Id));
    }

    [Fact]
    public void Video_unregistered_catalog_provider_throws_instead_of_defaulting()
    {
        var xai = CatalogXaiVideo();
        var other = CatalogNonXaiVideo();
        var xaiClient = new StubVideoClient();
        var facade = new MultiProviderVideoClient(new Dictionary<string, IVideoClient>
        {
            [xai.ProviderId] = xaiClient,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => facade.ResolveClientForModel(other.Id));
        Assert.Contains(SupportedModelCatalog.NormalizeProviderId(other.ProviderId), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(xaiClient, facade.ResolveClientForModel(xai.Id));
    }

    [Fact]
    public async Task Video_submit_tags_with_catalog_provider_id_and_poll_strips_it()
    {
        var xai = CatalogXaiVideo();
        var stub = new StubVideoClient { SubmitId = "req_abc123" };
        var facade = new MultiProviderVideoClient(new Dictionary<string, IVideoClient>
        {
            [xai.ProviderId] = stub,
        });

        var tagged = await facade.SubmitGenerationAsync(
            "p", 4, "720p", xai.Id, CancellationToken.None);
        var providerId = SupportedModelCatalog.NormalizeProviderId(xai.ProviderId);
        Assert.Equal(providerId + ":req_abc123", tagged);

        await facade.PollForVideoUrlAsync(tagged, null, CancellationToken.None);
        Assert.Equal("req_abc123", stub.LastPolledId);
    }

    [Fact]
    public void Video_tagged_request_id_splits_only_when_prefix_is_a_catalog_provider()
    {
        var xai = CatalogXaiVideo();
        var other = CatalogNonXaiVideo();
        var known = new[] { xai.ProviderId, other.ProviderId };

        Assert.True(MultiProviderVideoClient.TrySplitTaggedRequestId(
            SupportedModelCatalog.NormalizeProviderId(other.ProviderId) + ":models/veo-3.1/operations/abc123",
            known, out var provider, out var raw));
        Assert.Equal(SupportedModelCatalog.NormalizeProviderId(other.ProviderId), provider);
        Assert.Equal("models/veo-3.1/operations/abc123", raw);

        Assert.False(MultiProviderVideoClient.TrySplitTaggedRequestId(
            "req_legacy_no_tag", known, out _, out _));
        Assert.False(MultiProviderVideoClient.TrySplitTaggedRequestId(
            "not-a-provider:req", known, out _, out _));
    }

    [Fact]
    public async Task Video_download_uses_catalog_model_not_url_host()
    {
        var xai = CatalogXaiVideo();
        var other = CatalogNonXaiVideo();
        var xaiClient = new StubVideoClient();
        var otherClient = new StubVideoClient();
        var facade = new MultiProviderVideoClient(new Dictionary<string, IVideoClient>
        {
            [xai.ProviderId] = xaiClient,
            [other.ProviderId] = otherClient,
        });

        await facade.DownloadToFileAsync("https://cdn.example.com/unsigned.mp4", Path.GetTempFileName(), other.Id, CancellationToken.None);
        Assert.Equal(1, otherClient.DownloadCalls);
        Assert.Equal(0, xaiClient.DownloadCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Video_missing_model_id_throws(string? model)
    {
        var facade = FacadeWithXaiOnly();
        var ex = Assert.Throws<InvalidOperationException>(() => facade.ResolveClientForModel(model));
        Assert.Contains("model is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Video_unknown_model_id_throws()
    {
        var facade = FacadeWithXaiOnly();
        var ex = Assert.Throws<InvalidOperationException>(
            () => facade.ResolveClientForModel("not-a-catalog-video-model"));
        Assert.Contains("not in the models catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Video_disabled_catalog_row_throws()
    {
        var disabled = SupportedModelCatalog.ForCapability(ModelCapability.Video, enabledOnly: false)
            .FirstOrDefault(m => !m.Enabled);
        Assert.NotNull(disabled);
        var facade = FacadeWithXaiOnly();
        var ex = Assert.Throws<InvalidOperationException>(() => facade.ResolveClientForModel(disabled.Id));
        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Video_download_without_model_throws()
    {
        var facade = FacadeWithXaiOnly();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.DownloadToFileAsync("https://cdn.example.com/unsigned.mp4", Path.GetTempFileName(), CancellationToken.None));
        Assert.Contains("model is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Video_poll_without_catalog_provider_tag_throws()
    {
        var facade = FacadeWithXaiOnly();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.PollForVideoUrlAsync("req_legacy_no_tag", null, CancellationToken.None));
        Assert.Contains("catalog provider tag", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private MultiProviderVideoClient FacadeWithXaiOnly() =>
        new(new Dictionary<string, IVideoClient>
        {
            [CatalogXaiVideo().ProviderId] = new StubVideoClient(),
        });

    private static SupportedModelEntry CatalogXaiVideo()
    {
        var api = SupportedModelCatalog.XaiApiBase;
        var hit = SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => m.Enabled
                && !string.IsNullOrWhiteSpace(m.ApiBase)
                && string.Equals(m.ApiBase.TrimEnd('/'), api.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return hit;
    }

    private static SupportedModelEntry CatalogNonXaiVideo()
    {
        var xai = CatalogXaiVideo();
        return SupportedModelCatalog.ForCapability(ModelCapability.Video)
            .First(m => m.Enabled
                && !string.Equals(
                    SupportedModelCatalog.NormalizeProviderId(m.ProviderId),
                    SupportedModelCatalog.NormalizeProviderId(xai.ProviderId),
                    StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubVideoClient : IVideoClient
    {
        public bool IsConfigured { get; set; } = true;
        public string SubmitId { get; set; } = "req_stub";
        public string LastPolledId { get; private set; } = "";
        public int DownloadCalls { get; private set; }

        public Task<string> SubmitGenerationAsync(
            string prompt, int durationSeconds, string resolution, string model, CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null, string? startFrameImagePath = null,
            string? continueFromVideoPath = null, string? aspectRatio = null, string? extendSourceFileId = null)
            => Task.FromResult(SubmitId);

        public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
        {
            LastPolledId = requestId;
            return Task.FromResult("https://example.test/clip.mp4");
        }

        public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
        {
            DownloadCalls++;
            return Task.CompletedTask;
        }

        public StoredVideoFileRef TryGetStoredFileReference(string requestId) => StoredVideoFileRef.Empty;
    }
}
