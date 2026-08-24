using PageToMovie.Core.Models;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Fakes;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Catalog role bundles: a virtual Video id names real generate/extend siblings
/// and is never sent on the wire as <c>model</c>.
/// </summary>
[Collection("catalog-serial")]
public class VideoModelRolesTests : IDisposable
{
    public const string VirtualId = "imagine-video-1.5-extend";
    public const string GenerateId = "grok-imagine-video-1.5";
    public const string ExtendId = "grok-imagine-video";

    public VideoModelRolesTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    public void Dispose()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void Catalog_loads_virtual_row_as_pointer_bundle()
    {
        var entry = SupportedModelCatalog.Find(VirtualId, ModelCapability.Video);
        Assert.NotNull(entry);
        Assert.True(entry!.Enabled);
        Assert.True(entry.Virtual);
        Assert.False(entry.SupportsVideoContinue);
        Assert.NotNull(entry.Roles);
        Assert.Equal(GenerateId, entry.Roles![SupportedModelCatalog.VideoRoleGenerate]);
        Assert.Equal(ExtendId, entry.Roles[SupportedModelCatalog.VideoRoleExtend]);
        Assert.Contains("never sent", entry.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.MinClipDurationSeconds);
        Assert.Null(entry.MaxExtensionSeconds);
        Assert.Null(entry.VideoExtendCostPerSecond);
        Assert.Null(entry.VideoCostPerSecondByResolution);
    }

    [Fact]
    public void Virtual_defaults_are_false_and_empty()
    {
        Assert.False(new SupportedModelEntry
        {
            Id = "unset-virtual",
            DisplayName = "Unset",
            Capability = ModelCapability.Video,
            Provider = ModelProviderFamily.Xai,
            ApiBase = SupportedModelCatalog.XaiApiBase,
            EndpointPath = "videos/generations",
            RequiredEnvKeys = Array.Empty<string>(),
        }.Virtual);
        Assert.Null(new SupportedModelEntry
        {
            Id = "unset-roles",
            DisplayName = "Unset",
            Capability = ModelCapability.Video,
            Provider = ModelProviderFamily.Xai,
            ApiBase = SupportedModelCatalog.XaiApiBase,
            EndpointPath = "videos/generations",
            RequiredEnvKeys = Array.Empty<string>(),
        }.Roles);
        Assert.False(new SupportedModelDto().Virtual);
        Assert.Null(new SupportedModelDto().Roles);
    }

    [Fact]
    public void Virtual_row_round_trips_through_ToDto_and_FromDto()
    {
        var entry = SupportedModelCatalog.Find(VirtualId, ModelCapability.Video);
        Assert.NotNull(entry);
        var dto = SupportedModelCatalog.ToDto(entry!);
        Assert.True(dto.Virtual);
        Assert.Equal(GenerateId, dto.Roles![SupportedModelCatalog.VideoRoleGenerate]);
        Assert.Equal(ExtendId, dto.Roles[SupportedModelCatalog.VideoRoleExtend]);

        var back = SupportedModelCatalog.FromDto(dto);
        Assert.True(back.Virtual);
        Assert.Equal(GenerateId, back.Roles![SupportedModelCatalog.VideoRoleGenerate]);
        Assert.Equal(ExtendId, back.Roles[SupportedModelCatalog.VideoRoleExtend]);
    }

    [Fact]
    public void Virtual_row_is_a_selectable_video_model()
    {
        var list = SupportedModelCatalog.ForCapability(ModelCapability.Video);
        Assert.Contains(list, e => e.Id == VirtualId);
        Assert.Equal(ExtendId, SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Video));
    }

    [Fact]
    public void Extra_role_keys_are_legal_and_ignored()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(CatalogJson(
            virtualRoles: """
              "generate": "gen-video",
              "extend": "ext-video",
              "voice": "missing-voice-id"
            """)));
        var roles = SupportedModelCatalog.ResolveVideoRoles("bundle-video");
        Assert.Equal("gen-video", roles.Generate.Id);
        Assert.Equal("ext-video", roles.Extend!.Id);
        Assert.Equal("ext-video", roles.WireModelId(isExtendHop: true));
    }

    [Fact]
    public void ResolveVideoRoles_virtual_happy_path()
    {
        var roles = SupportedModelCatalog.ResolveVideoRoles(VirtualId);
        Assert.Equal(VirtualId, roles.Selected.Id);
        Assert.True(roles.Selected.Virtual);
        Assert.Equal(GenerateId, roles.Generate.Id);
        Assert.False(roles.Generate.Virtual);
        Assert.False(roles.Generate.SupportsVideoContinue);
        Assert.Equal(ExtendId, roles.Extend!.Id);
        Assert.False(roles.Extend.Virtual);
        Assert.True(roles.Extend.SupportsVideoContinue);
        Assert.True(roles.CanExtend);
        Assert.Equal(GenerateId, roles.WireModelId(isExtendHop: false));
        Assert.Equal(ExtendId, roles.WireModelId(isExtendHop: true));
        Assert.NotEqual(VirtualId, roles.WireModelId(false));
        Assert.NotEqual(VirtualId, roles.WireModelId(true));
    }

    [Fact]
    public void ResolveVideoRoles_raw_1_5_does_not_continue()
    {
        var roles = SupportedModelCatalog.ResolveVideoRoles(GenerateId);
        Assert.Equal(GenerateId, roles.Selected.Id);
        Assert.Equal(GenerateId, roles.Generate.Id);
        Assert.Null(roles.Extend);
        Assert.False(roles.CanExtend);
        Assert.Equal(GenerateId, roles.WireModelId(isExtendHop: false));
        var ex = Assert.Throws<InvalidOperationException>(() => roles.WireModelId(isExtendHop: true));
        Assert.Contains("no extend role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVideoRoles_raw_imagine_video_continues_as_itself()
    {
        var roles = SupportedModelCatalog.ResolveVideoRoles(ExtendId);
        Assert.Equal(ExtendId, roles.Selected.Id);
        Assert.Equal(ExtendId, roles.Generate.Id);
        Assert.Equal(ExtendId, roles.Extend!.Id);
        Assert.True(roles.CanExtend);
        Assert.Equal(ExtendId, roles.WireModelId(false));
        Assert.Equal(ExtendId, roles.WireModelId(true));
    }

    [Fact]
    public void ResolveVideoRoles_missing_sibling_fails()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(CatalogJson(
            virtualRoles: """
              "generate": "missing-generate",
              "extend": "ext-video"
            """)));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveVideoRoles("bundle-video"));
        Assert.Contains("missing-generate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVideoRoles_disabled_sibling_fails()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(CatalogJson(
            virtualRoles: """
              "generate": "disabled-video",
              "extend": "ext-video"
            """)));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveVideoRoles("bundle-video"));
        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVideoRoles_extend_sibling_without_continue_fails()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(CatalogJson(
            virtualRoles: """
              "generate": "gen-video",
              "extend": "gen-video"
            """)));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveVideoRoles("bundle-video"));
        Assert.Contains("does not support video continue", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVideoRoles_nested_virtual_fails()
    {
        Assert.True(SupportedModelCatalog.TryLoadFromJson(CatalogJson(
            extraModels: """
            ,{
              "id": "inner-bundle",
              "displayName": "Inner",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "virtual": true,
              "supportsVideoContinue": false,
              "roles": { "generate": "gen-video", "extend": "ext-video" }
            }
            """,
            virtualRoles: """
              "generate": "inner-bundle",
              "extend": "ext-video"
            """)));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SupportedModelCatalog.ResolveVideoRoles("bundle-video"));
        Assert.Contains("itself virtual", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clip_duration_uses_generate_caps_then_extend_caps()
    {
        var (min, max, abs) = ClipDurationEstimator.ResolveBoundsForModel(VirtualId);
        var gen = ClipDurationEstimator.ResolveBoundsForModel(GenerateId);
        Assert.Equal(gen.MinSeconds, min);
        Assert.Equal(gen.MaxSeconds, max);
        Assert.Equal(gen.AbsMaxSeconds, abs);

        var fresh = ClipDurationEstimator.ResolveActualDurationForModel(VirtualId, 12);
        Assert.Equal(12, fresh);

        var hop = ClipDurationEstimator.ResolveActualDurationForModel(VirtualId, 12, isExtensionMode: true);
        Assert.Equal(10, hop);

        Assert.Equal(10, ClipDurationEstimator.ResolveExtensionMaxForModel(VirtualId, fallbackMax: 15));
        Assert.Equal(0, ClipDurationEstimator.ResolveExtensionMaxForModel(GenerateId, fallbackMax: 15));
        Assert.Equal(10, ClipDurationEstimator.ResolveExtensionMaxForModel(ExtendId, fallbackMax: 15));
    }

    [Fact]
    public void Raw_1_5_still_rejects_extension_mode()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ClipDurationEstimator.ResolveActualDurationForModel(GenerateId, 8, isExtensionMode: true));
        Assert.Contains("does not support video continue", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cost_extend_rate_comes_from_extend_sibling()
    {
        var extend = SupportedModelCatalog.Find(ExtendId, ModelCapability.Video);
        Assert.NotNull(extend?.VideoExtendCostPerSecond);

        var rates = CostReportService.RatesFromModels(VirtualId, "grok-imagine-image-quality");
        Assert.Equal(extend!.VideoExtendCostPerSecond, Assert.IsType<double>(rates["video_input_per_sec"]));
        Assert.Equal("model_catalog", rates["video_input_per_sec_source"]);
    }

    [Fact]
    public async Task Submit_uses_generate_then_extend_sibling_never_virtual_id()
    {
        var spy = new RecordingVideoClient();
        var client = new MultiProviderVideoClient(
            new Dictionary<string, IVideoClient>(StringComparer.OrdinalIgnoreCase)
            {
                ["grok"] = spy,
            });

        var roles = SupportedModelCatalog.ResolveVideoRoles(VirtualId);
        var generateId = roles.WireModelId(isExtendHop: false);
        var extendId = roles.WireModelId(isExtendHop: true);
        Assert.Equal(GenerateId, generateId);
        Assert.Equal(ExtendId, extendId);

        await client.SubmitGenerationAsync(
            "fresh", 6, "1080p", generateId, CancellationToken.None);
        Assert.Equal(GenerateId, spy.LastModel);
        Assert.DoesNotContain(VirtualId, spy.Models);

        await client.SubmitGenerationAsync(
            "hop", 6, "720p", extendId, CancellationToken.None,
            continueFromVideoPath: "/tmp/prev.mp4");
        Assert.Equal(ExtendId, spy.LastModel);
        Assert.DoesNotContain(VirtualId, spy.Models);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SubmitGenerationAsync(
                "bundle", 6, "1080p", VirtualId, CancellationToken.None));
        Assert.Contains("role bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(VirtualId, spy.Models);
    }

    [Fact]
    public void Fake_client_rejects_virtual_id_and_1_5_continue()
    {
        var virtualEx = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                VirtualId, durationSeconds: 6, referenceImagePaths: null, continueFromVideoPath: null));
        Assert.Contains("role bundle", virtualEx.Message, StringComparison.OrdinalIgnoreCase);

        FakeGrokVideoClient.ValidateAgainstCatalog(
            GenerateId, durationSeconds: 8, referenceImagePaths: null, continueFromVideoPath: null);

        var noContinue = Assert.Throws<InvalidOperationException>(() =>
            FakeGrokVideoClient.ValidateAgainstCatalog(
                GenerateId, durationSeconds: 8, referenceImagePaths: null, continueFromVideoPath: "/tmp/prev.mp4"));
        Assert.Contains("does not support video continue", noContinue.Message, StringComparison.OrdinalIgnoreCase);

        FakeGrokVideoClient.ValidateAgainstCatalog(
            ExtendId, durationSeconds: 8, referenceImagePaths: null, continueFromVideoPath: "/tmp/prev.mp4");
    }

    private static string CatalogJson(string virtualRoles, string extraModels = "") =>
        $$"""
        {
          "models": [
            {
              "id": "gen-video",
              "displayName": "Gen",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "supportsVideoContinue": false
            },
            {
              "id": "ext-video",
              "displayName": "Ext",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "supportsVideoContinue": true,
              "maxExtensionSeconds": 10
            },
            {
              "id": "disabled-video",
              "displayName": "Disabled",
              "capability": "Video",
              "provider": "Xai",
              "enabled": false,
              "supportsVideoContinue": false
            },
            {
              "id": "bundle-video",
              "displayName": "Bundle",
              "capability": "Video",
              "provider": "Xai",
              "enabled": true,
              "virtual": true,
              "supportsVideoContinue": false,
              "roles": { {{virtualRoles}} },
              "notes": "test bundle"
            }{{extraModels}}
          ]
        }
        """;

    private sealed class RecordingVideoClient : IVideoClient
    {
        public bool IsConfigured => true;
        public string CatalogProviderId => "grok";
        public List<string> Models { get; } = new();
        public string? LastModel => Models.Count == 0 ? null : Models[^1];

        public Task<string> SubmitGenerationAsync(
            string prompt,
            int durationSeconds,
            string resolution,
            string model,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            string? startFrameImagePath = null,
            string? continueFromVideoPath = null,
            string? aspectRatio = null,
            string? extendSourceFileId = null)
        {
            Models.Add(model);
            return Task.FromResult("rec-" + Models.Count);
        }

        public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct) =>
            Task.FromResult("https://example.test/v.mp4");

        public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) => Task.CompletedTask;

        public StoredVideoFileRef TryGetStoredFileReference(string requestId) => default;
    }
}
