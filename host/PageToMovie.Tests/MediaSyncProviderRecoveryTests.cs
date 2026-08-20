using PageToMovie.Api;
using Xunit;

namespace PageToMovie.Tests;

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

    private void WriteSidecar(int scene, int clip, int take, string? sourceUrl, double? leadIn = null, string? sourceFileId = null)
    {
        var lead = leadIn is { } l ? $",\"provider_lead_in_seconds\":{l:0.0##}" : "";
        var src = sourceUrl is null ? "" : $",\"source_url\":\"{sourceUrl}\"";
        var fid = sourceFileId is null ? "" : $",\"source_file_id\":\"{sourceFileId}\"";
        File.WriteAllText(
            Path.Combine(_videoDir, $"scene_{scene:D2}_clip_{clip:D2}_take_{take:D2}.clip.json"),
            $"{{\"scene\":{scene},\"clip\":{clip}{src}{fid}{lead}}}");
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
}
