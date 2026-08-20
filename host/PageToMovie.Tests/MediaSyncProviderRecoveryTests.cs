using Microsoft.AspNetCore.Http;
using PageToMovie.Api;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]

/// <summary>
/// Provider-recovery rows in the media sync list (MediaEndpoints.CollectProviderRecoveryEntries):
/// a clip whose bytes are on neither server disk nor the client, but whose sidecar still points at
/// the provider copy, is offered through a proxy ticket so a client sync can self-heal a missed
/// live save. See the Mary19 empty-folder incident (stale-identity hub bug, fixed separately) —
/// these entries are what let an affected project pull its clips back down afterward.
/// </summary>
public class MediaSyncProviderRecoveryTests : IDisposable
{
    private readonly string _videoDir;

    public MediaSyncProviderRecoveryTests()
    {
        _videoDir = Path.Combine(Path.GetTempPath(), "ptm-recovery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_videoDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_videoDir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSidecar(
        int scene, int clip, int take, string? sourceUrl, double? leadIn = null, string? sourceFileId = null,
        string? model = null, string? sourceProvider = null)
    {
        var lead = leadIn is { } l ? $",\"provider_lead_in_seconds\":{l:0.0##}" : "";
        var src = sourceUrl is null ? "" : $",\"source_url\":\"{sourceUrl}\"";
        var fid = sourceFileId is null ? "" : $",\"source_file_id\":\"{sourceFileId}\"";
        var mdl = model is null ? "" : $",\"model\":\"{model}\"";
        var prov = sourceProvider is null ? "" : $",\"source_provider\":\"{sourceProvider}\"";
        File.WriteAllText(
            Path.Combine(_videoDir, $"scene_{scene:D2}_clip_{clip:D2}_take_{take:D2}.clip.json"),
            $"{{\"scene\":{scene},\"clip\":{clip}{src}{fid}{lead}{mdl}{prov}}}");
    }

    private static List<MediaEndpoints.ProviderRecoverySyncEntry> Collect(string videoDir) =>
        MediaEndpoints.CollectProviderRecoveryEntries(videoDir, (_, _) => "tok");

    [Fact]
    public void Sidecar_only_clip_with_provider_url_yields_a_recovery_entry()
    {
        WriteSidecar(1, 2, 1, "https://vidgen.example/clip2.mp4");

        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, (url, _) => "tok-" + (url?.Length ?? 0));

        var e = Assert.Single(entries);
        Assert.Equal("assets/video/scene_01_clip_02.mp4", e.RelativePath);
        Assert.Equal("scene_01_clip_02.mp4", e.FileName);
        Assert.True(e.ProviderRecovery);
        Assert.True(e.IsMp4);
        Assert.Equal(0, e.SizeBytes);
        Assert.Null(e.Sha256);
        Assert.StartsWith("/api/media/proxy/tok-", e.StreamUrl);
    }

    [Fact]
    public void Clip_with_real_mp4_on_server_disk_is_not_offered_for_recovery()
    {
        WriteSidecar(1, 1, 1, "https://vidgen.example/clip1.mp4");
        // Take-suffixed on-disk name still counts as present.
        File.WriteAllBytes(
            Path.Combine(_videoDir, "scene_01_clip_01_take_01_20260820_120000.mp4"),
            new byte[2048]);

        var entries = Collect(_videoDir);

        Assert.Empty(entries);
    }

    [Fact]
    public void Combined_extend_copy_carries_its_lead_in_for_the_client_slice()
    {
        // Combined extend video: its head repeats the previous clip — the entry must carry the
        // sidecar's lead-in so the client slices the new tail out before saving (the API host
        // never trims).
        WriteSidecar(2, 1, 1, "https://vidgen.example/combined.mp4", leadIn: 4.9);

        var entries = Collect(_videoDir);

        var e = Assert.Single(entries);
        Assert.Equal(4.9, e.ProviderLeadInSeconds, 3);
        Assert.True(e.ProviderRecovery);
    }

    [Fact]
    public void Urlless_sidecars_are_skipped_and_plain_clips_carry_no_lead_in()
    {
        // No provider pointer at all (e.g. an imported clip) → not recoverable.
        WriteSidecar(2, 2, 1, sourceUrl: null);
        WriteSidecar(2, 3, 1, "https://vidgen.example/plain.mp4");

        var entries = Collect(_videoDir);

        var e = Assert.Single(entries);
        Assert.Equal("assets/video/scene_02_clip_03.mp4", e.RelativePath);
        Assert.Equal(0, e.ProviderLeadInSeconds, 3);
    }

    [Fact]
    public void One_entry_per_clip_even_with_multiple_take_sidecars()
    {
        WriteSidecar(3, 1, 1, "https://vidgen.example/take1.mp4");
        WriteSidecar(3, 1, 2, "https://vidgen.example/take2.mp4");

        var entries = Collect(_videoDir);

        Assert.Single(entries);
    }

    [Fact]
    public void Missing_video_dir_yields_no_entries()
    {
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(
            Path.Combine(_videoDir, "does-not-exist"), (_, _) => "tok");

        Assert.Empty(entries);
    }

    [Fact]
    public void Combined_C3_carries_C2_sidecar_lead_in_as_predecessor_hop()
    {
        WriteSidecar(1, 2, 1, "https://vidgen.example/c2.mp4", leadIn: 4.9);
        WriteSidecar(1, 3, 1, "https://vidgen.example/c3.mp4", leadIn: 9.8);

        var entries = Collect(_videoDir);

        var c3 = Assert.Single(entries, e => e.RelativePath.EndsWith("clip_03.mp4", StringComparison.Ordinal));
        Assert.Equal(9.8, c3.ProviderLeadInSeconds, 3);
        // Nearest previous first: C2's hop (C1). Client hop-walk peels C1 from the C1+C2 head.
        Assert.Equal(new[] { 4.9 }, c3.PredecessorLeadInSeconds);
    }

    [Fact]
    public void File_id_only_sidecar_with_empty_url_still_yields_a_recovery_entry()
    {
        WriteSidecar(1, 2, 1, sourceUrl: null, sourceFileId: "file_live_handle");

        var seen = new List<(string? Url, string? FileId)>();
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, (url, fileId) =>
        {
            seen.Add((url, fileId));
            return "tok-fid";
        });

        var e = Assert.Single(entries);
        Assert.Equal("assets/video/scene_01_clip_02.mp4", e.RelativePath);
        Assert.True(e.ProviderRecovery);
        Assert.Equal("/api/media/proxy/tok-fid", e.StreamUrl);
        var issued = Assert.Single(seen);
        Assert.True(string.IsNullOrWhiteSpace(issued.Url));
        Assert.Equal("file_live_handle", issued.FileId);
    }

    [Fact]
    public void Expired_url_plus_file_id_still_yields_a_recovery_entry_and_tickets_both()
    {
        WriteSidecar(4, 1, 1, "https://vidgen.example/expired.mp4", sourceFileId: "file_still_there");

        var seen = new List<(string? Url, string? FileId)>();
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, (url, fileId) =>
        {
            seen.Add((url, fileId));
            return "tok-both";
        });

        Assert.Single(entries);
        var issued = Assert.Single(seen);
        Assert.Equal("https://vidgen.example/expired.mp4", issued.Url);
        Assert.Equal("file_still_there", issued.FileId);
    }

    [Fact]
    public void File_id_only_combined_C3_still_carries_lead_in_and_predecessor_hops()
    {
        WriteSidecar(1, 2, 1, sourceUrl: null, leadIn: 4.9, sourceFileId: "file_c2");
        WriteSidecar(1, 3, 1, sourceUrl: "", leadIn: 9.8, sourceFileId: "file_c3");

        var entries = Collect(_videoDir);

        var c3 = Assert.Single(entries, e => e.RelativePath.EndsWith("clip_03.mp4", StringComparison.Ordinal));
        Assert.Equal(9.8, c3.ProviderLeadInSeconds, 3);
        Assert.Equal(new[] { 4.9 }, c3.PredecessorLeadInSeconds);
        Assert.StartsWith("/api/media/proxy/", c3.StreamUrl);
    }

    [Fact]
    public void Ticket_store_keeps_file_id_when_url_is_empty()
    {
        var store = new PageToMovie.Engine.MediaProxyTicketStore();
        var token = store.Issue("", TimeSpan.FromMinutes(5), "file_abc");
        Assert.True(store.TryTake(token, out var url, out var fileId));
        Assert.True(string.IsNullOrEmpty(url));
        Assert.Equal("file_abc", fileId);
    }

    [Fact]
    public void Ticket_store_url_only_leaves_file_id_null()
    {
        var store = new PageToMovie.Engine.MediaProxyTicketStore();
        var token = store.Issue("https://vidgen.example/ok.mp4", TimeSpan.FromMinutes(5));
        Assert.True(store.TryTake(token, out var url, out var fileId));
        Assert.Equal("https://vidgen.example/ok.mp4", url);
        Assert.Null(fileId);
        Assert.Equal(url, store.TryTakeUrl(token));
    }

    [Fact]
    public void Ticket_store_captures_key_user_from_scope_and_explicit_arg()
    {
        var store = new PageToMovie.Engine.MediaProxyTicketStore();
        using (PageToMovie.Engine.Abstractions.UserApiCallScope.Push("budcribar"))
        {
            var scoped = store.Issue("https://vidgen.example/expired.mp4", TimeSpan.FromMinutes(5), "file_live");
            Assert.True(store.TryTake(scoped, out _, out var fileId, out var keyUserId));
            Assert.Equal("file_live", fileId);
            Assert.Equal("budcribar", keyUserId);
        }

        var explicitTok = store.Issue("", TimeSpan.FromMinutes(5), "file_other", "owner-id");
        Assert.True(store.TryTake(explicitTok, out _, out _, out var explicitUser));
        Assert.Equal("owner-id", explicitUser);
    }

    [Fact]
    public async Task StreamProviderCopy_url_404_plus_file_id_streams_via_file_id_opener()
    {
        var urlHits = 0;
        var fileHits = 0;
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://vidgen.example/expired.mp4",
            "file_1ed4c54f-2edd-485b-8d35-5f31c854132a",
            (_, _) =>
            {
                urlHits++;
                return Task.FromResult<IResult?>(null);
            },
            (id, _) =>
            {
                fileHits++;
                Assert.Equal("file_1ed4c54f-2edd-485b-8d35-5f31c854132a", id);
                return Task.FromResult<IResult?>(Results.Bytes(new byte[] { 1, 2, 3, 4 }, "video/mp4"));
            },
            CancellationToken.None);

        Assert.Equal(0, urlHits);
        Assert.Equal(1, fileHits);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.DoesNotContain("File not found", ErrorOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamProviderCopy_hanging_url_plus_file_id_does_not_wait()
    {
        var urlHits = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://vidgen.example/hang.mp4",
            "file_live",
            async (_, ct) =>
            {
                urlHits++;
                await Task.Delay(Timeout.Infinite, ct);
                return (IResult?)null;
            },
            (_, _) => Task.FromResult<IResult?>(Results.Bytes(new byte[] { 1, 2, 3 }, "video/mp4")),
            CancellationToken.None);

        sw.Stop();
        Assert.Equal(0, urlHits);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SeedCatalogApiBaseKey_uses_ProviderIdForApiBase_not_a_hardcoded_slot()
    {
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        ApiPipeline.SeedCatalogApiBaseKey(keys, PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase, "xai-from-catalog");

        var expected = PageToMovie.Core.Models.SupportedModelCatalog.ProviderIdForApiBase(
            PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal("xai-from-catalog", keys[expected]);
        Assert.DoesNotContain(keys.Keys, k =>
            k.Equals("grok", StringComparison.OrdinalIgnoreCase)
            && !k.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SeedCatalogApiBaseKey_empty_api_base_adds_no_slot()
    {
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        ApiPipeline.SeedCatalogApiBaseKey(keys, "", "should-not-land");
        Assert.Empty(keys);
    }

    [Fact]
    public async Task StreamProviderCopy_file_id_500_falls_back_to_source_url()
    {
        var urlHits = 0;
        var fileHits = 0;
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://files.x.ai/p/public.mp4",
            "file_dead",
            (_, _) =>
            {
                urlHits++;
                return Task.FromResult<IResult?>(Results.Bytes(new byte[] { 9, 8, 7, 6 }, "video/mp4"));
            },
            (_, _) =>
            {
                fileHits++;
                throw new InvalidOperationException(
                    "xAI file content HTTP 500: Failed to retrieve file");
            },
            CancellationToken.None);

        Assert.Equal(1, fileHits);
        Assert.Equal(1, urlHits);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.DoesNotContain("502", ErrorOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("Provider file download failed", ErrorOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamProviderCopy_file_id_500_and_url_fail_mentions_both()
    {
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://vidgen.example/expired.mp4",
            "file_dead",
            (_, _) => Task.FromResult<IResult?>(null),
            (_, _) => throw new InvalidOperationException(
                "xAI file content HTTP 500: Failed to retrieve file"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(result));
        var err = ErrorOf(result);
        Assert.Contains("Provider file download failed", err, StringComparison.Ordinal);
        Assert.Contains("500", err, StringComparison.Ordinal);
        Assert.Contains("Failed to retrieve file", err, StringComparison.Ordinal);
        Assert.Contains(ClipProviderSource.SourceUrlAlsoFailedPrefix, err, StringComparison.Ordinal);
        Assert.DoesNotContain("\"File not found\"", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamProviderCopy_file_id_opener_failure_is_visible_not_blank_file_not_found()
    {
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://vidgen.example/expired.mp4",
            "file_dead",
            (_, _) => Task.FromResult<IResult?>(null),
            (_, _) => throw new InvalidOperationException("xAI file content HTTP 401: {\"code\":\"invalid_api_key\"}"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(result));
        var err = ErrorOf(result);
        Assert.Contains("Provider file download failed", err, StringComparison.Ordinal);
        Assert.Contains("401", err, StringComparison.Ordinal);
        Assert.DoesNotContain("\"File not found\"", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamProviderCopy_no_file_id_still_says_file_not_found()
    {
        var result = await MediaEndpoints.StreamProviderCopyAsync(
            "https://vidgen.example/expired.mp4",
            fileId: null,
            (_, _) => Task.FromResult<IResult?>(null),
            (_, _) => Task.FromResult<IResult?>(Results.Bytes(new byte[] { 9 }, "video/mp4")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Contains("File not found", ErrorOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Ticket_store_keeps_model_and_catalog_provider()
    {
        var store = new PageToMovie.Engine.MediaProxyTicketStore();
        var token = store.Issue(
            new PageToMovie.Engine.MediaProxyTicket
            {
                Url = "https://vidgen.example/ok.mp4",
                FileId = "file_live",
                KeyUserId = "owner-id",
                ProjectDir = "/data/projects/demo",
                Scene = 1,
                Clip = 2,
                ModelId = "catalog-video-model",
                ProviderId = "catalog-provider"
            }, TimeSpan.FromMinutes(5));
        Assert.True(store.TryTake(token, out var ticket));
        Assert.Equal("file_live", ticket.FileId);
        Assert.Equal("owner-id", ticket.KeyUserId);
        Assert.Equal("/data/projects/demo", ticket.ProjectDir);
        Assert.Equal(1, ticket.Scene);
        Assert.Equal(2, ticket.Clip);
        Assert.Equal("catalog-video-model", ticket.ModelId);
        Assert.Equal("catalog-provider", ticket.ProviderId);
    }

    [Fact]
    public void CollectRecovery_tickets_sidecar_model_and_catalog_provider()
    {
        var xaiVideo = CatalogXaiVideoModel();
        WriteSidecar(5, 1, 1, "https://vidgen.example/c.mp4", sourceFileId: "file_live",
            model: xaiVideo.Id, sourceProvider: xaiVideo.ProviderId);

        string? seenModel = null;
        string? seenProvider = null;
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(
            _videoDir,
            (url, fileId, _, _, _, modelId, providerId) =>
            {
                seenModel = modelId;
                seenProvider = providerId;
                return "tok-model";
            });

        Assert.Single(entries);
        Assert.Equal(xaiVideo.Id, seenModel);
        Assert.Equal(
            PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(xaiVideo.ProviderId),
            seenProvider);
    }

    [Fact]
    public async Task ResolveTicketVideoKey_uses_catalog_provider_for_non_xai_video_model()
    {
        var other = CatalogNonXaiVideoModel();
        var keys = new RecordingUserKeys();
        keys.Set("budcribar", other.ProviderId, "other-provider-secret");
        keys.Set("budcribar", CatalogXaiVideoModel().ProviderId, "xai-secret-must-not-be-used");

        var key = await MediaEndpoints.ResolveTicketVideoKeyAsync(
            keys, "budcribar", other.Id, null, CancellationToken.None);

        Assert.Equal("other-provider-secret", key);
        var call = Assert.Single(keys.Calls);
        Assert.Equal("budcribar", call.UserId);
        Assert.Equal(
            PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(other.ProviderId),
            call.ProviderId);
        Assert.DoesNotContain(keys.Calls, c => c.UserId is null);
    }

    [Fact]
    public async Task ResolveTicketVideoKey_imagine_style_model_uses_its_catalog_provider()
    {
        var xaiVideo = CatalogXaiVideoModel();
        var keys = new RecordingUserKeys();
        keys.Set("budcribar", xaiVideo.ProviderId, "xai-from-catalog-provider");

        var key = await MediaEndpoints.ResolveTicketVideoKeyAsync(
            keys, "budcribar", xaiVideo.Id, null, CancellationToken.None);

        Assert.Equal("xai-from-catalog-provider", key);
        var call = Assert.Single(keys.Calls);
        Assert.Equal("budcribar", call.UserId);
        Assert.Equal(
            PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(xaiVideo.ProviderId),
            call.ProviderId);
        Assert.DoesNotContain(keys.Calls, c => c.UserId is null);
    }

    [Fact]
    public async Task ResolveTicketVideoKey_never_calls_GetKeyAsync_with_null_user()
    {
        var xaiVideo = CatalogXaiVideoModel();
        var keys = new RecordingUserKeys();
        keys.Set("owner", xaiVideo.ProviderId, "personal");

        var missingUser = await MediaEndpoints.ResolveTicketVideoKeyAsync(
            keys, null, xaiVideo.Id, xaiVideo.ProviderId, CancellationToken.None);
        Assert.Null(missingUser);
        Assert.Empty(keys.Calls);

        var withUser = await MediaEndpoints.ResolveTicketVideoKeyAsync(
            keys, "owner", xaiVideo.Id, null, CancellationToken.None);
        Assert.Equal("personal", withUser);
        Assert.All(keys.Calls, c => Assert.False(string.IsNullOrWhiteSpace(c.UserId)));
    }

    private static PageToMovie.Core.Models.SupportedModelEntry CatalogXaiVideoModel()
    {
        var api = PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase;
        var hit = PageToMovie.Core.Models.SupportedModelCatalog.ForCapability(PageToMovie.Core.Models.ModelCapability.Video)
            .FirstOrDefault(m =>
                m.Enabled
                && !string.IsNullOrWhiteSpace(m.ApiBase)
                && string.Equals(m.ApiBase.TrimEnd('/'), api.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hit);
        return hit;
    }

    private static PageToMovie.Core.Models.SupportedModelEntry CatalogNonXaiVideoModel()
    {
        var xai = CatalogXaiVideoModel();
        var hit = PageToMovie.Core.Models.SupportedModelCatalog.ForCapability(PageToMovie.Core.Models.ModelCapability.Video)
            .FirstOrDefault(m =>
                m.Enabled
                && !string.Equals(
                    PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(m.ProviderId),
                    PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(xai.ProviderId),
                    StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hit);
        return hit;
    }

    private static int? StatusOf(IResult result) =>
        result is IStatusCodeHttpResult s ? s.StatusCode : 200;

    private static string ErrorOf(IResult result)
    {
        if (result is IValueHttpResult { Value: { } v })
            return System.Text.Json.JsonSerializer.Serialize(v);
        return result.GetType().Name;
    }

    private sealed class RecordingUserKeys : PageToMovie.Engine.Abstractions.IUserApiKeyProvider
    {
        private readonly Dictionary<(string User, string Provider), string> _keys = new();
        public List<(string? UserId, string ProviderId)> Calls { get; } = new();

        public void Set(string userId, string providerId, string key) =>
            _keys[(userId, PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(providerId))] = key;

        public Task<string?> GetKeyAsync(string? userId, string providerId, CancellationToken ct = default)
        {
            Calls.Add((userId, providerId));
            if (string.IsNullOrWhiteSpace(userId)) return Task.FromResult<string?>(null);
            var norm = PageToMovie.Core.Models.SupportedModelCatalog.NormalizeProviderId(providerId);
            return Task.FromResult(_keys.TryGetValue((userId, norm), out var k) ? k : null);
        }
    }
}
