using PageToMovie.Core.Models;
using PageToMovie.Fakes;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Fake video client enforces catalog feature flags so model combinations can be tested under UseFakes.
/// </summary>
// See CatalogSerialCollection in SupportedModelCatalogTests.cs.
[Collection("catalog-serial")]
public class FakeVideoCatalogCapabilityTests
{
    public FakeVideoCatalogCapabilityTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void Grok_allows_continue_and_many_refs()
    {
        FakeGrokVideoClient.ValidateAgainstCatalog(
            "grok-imagine-video",
            durationSeconds: 10,
            referenceImagePaths: Enumerable.Range(0, 5).Select(i => $"/tmp/r{i}.png").ToList(),
            continueFromVideoPath: "/tmp/prev.mp4");
    }

    [Fact]
    public void Wan_rejects_continue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "fal-ai/wan-2.1",
                durationSeconds: 5,
                referenceImagePaths: null,
                continueFromVideoPath: "/tmp/prev.mp4"));
        Assert.Contains("does not support video continue", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wan_rejects_too_many_refs()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "fal-ai/wan-2.1",
                durationSeconds: 5,
                referenceImagePaths: new[] { "a.png", "b.png" },
                continueFromVideoPath: null));
        Assert.Contains("at most 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wan_accepts_one_ref_and_in_range_duration()
    {
        FakeGrokVideoClient.ValidateAgainstCatalog(
            "fal-ai/wan-2.1",
            durationSeconds: 5,
            referenceImagePaths: new[] { "a.png" },
            continueFromVideoPath: null);
    }

    [Fact]
    public void Veo_allows_up_to_three_reference_images()
    {
        FakeGrokVideoClient.ValidateAgainstCatalog(
            "veo-3.1",
            durationSeconds: 4,
            referenceImagePaths: new[] { "a.png", "b.png", "c.png" },
            continueFromVideoPath: null);
    }

    [Fact]
    public void Veo_rejects_more_than_three_reference_images()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "veo-3.1",
                durationSeconds: 4,
                referenceImagePaths: new[] { "a.png", "b.png", "c.png", "d.png" },
                continueFromVideoPath: null));
        Assert.Contains("at most 3", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Veo_rejects_disallowed_duration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "veo-3.1",
                durationSeconds: 5,
                referenceImagePaths: null,
                continueFromVideoPath: null));
        Assert.Contains("only allows durations", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Veo_accepts_allowed_durations(int seconds)
    {
        FakeGrokVideoClient.ValidateAgainstCatalog(
            "veo-3.1",
            durationSeconds: seconds,
            referenceImagePaths: null,
            continueFromVideoPath: null);
    }

    [Fact]
    public void Unknown_model_id_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "not-in-catalog-test-id",
                durationSeconds: 99,
                referenceImagePaths: null,
                continueFromVideoPath: null));
        Assert.Contains("not in models_catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_model_id_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                "",
                durationSeconds: 5,
                referenceImagePaths: null,
                continueFromVideoPath: null));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wan_omits_maxExtensionSeconds_when_no_continue()
    {
        var e = SupportedModelCatalog.Find("fal-ai/wan-2.1", ModelCapability.Video);
        Assert.NotNull(e);
        Assert.False(e!.SupportsVideoContinue);
        Assert.Null(e.MaxExtensionSeconds);
    }

    [Fact]
    public void Grok_maxExtensionSeconds_is_positive()
    {
        var e = SupportedModelCatalog.Find("grok-imagine-video", ModelCapability.Video);
        Assert.NotNull(e);
        Assert.True(e!.SupportsVideoContinue);
        Assert.True(e.MaxExtensionSeconds is > 0);
    }

    [Fact]
    public void ResolveActualDuration_extension_mode_throws_when_no_continue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PageToMovie.Engine.ClipDurationEstimator.ResolveActualDurationForModel(
                "fal-ai/wan-2.1", requestedSeconds: 5, isExtensionMode: true));
        Assert.Contains("does not support video continue", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExtensionMax_returns_zero_for_non_continue_model()
    {
        var n = PageToMovie.Engine.ClipDurationEstimator.ResolveExtensionMaxForModel("veo-3.1", fallbackMax: 8);
        Assert.Equal(0, n);
    }
}
