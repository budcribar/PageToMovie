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

    private void WriteSidecar(int scene, int clip, int take, string? sourceUrl, double? leadIn = null)
    {
        var lead = leadIn is { } l ? $",\"provider_lead_in_seconds\":{l:0.0##}" : "";
        var src = sourceUrl is null ? "" : $",\"source_url\":\"{sourceUrl}\"";
        File.WriteAllText(
            Path.Combine(_videoDir, $"scene_{scene:D2}_clip_{clip:D2}_take_{take:D2}.clip.json"),
            $"{{\"scene\":{scene},\"clip\":{clip}{src}{lead}}}");
    }

    [Fact]
    public void Sidecar_only_clip_with_provider_url_yields_a_recovery_entry()
    {
        WriteSidecar(1, 2, 1, "https://vidgen.example/clip2.mp4");

        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, url => "tok-" + url.Length);

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

        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, _ => "tok");

        Assert.Empty(entries);
    }

    [Fact]
    public void Combined_extend_copies_and_urlless_sidecars_are_skipped()
    {
        // Combined extend video: its head repeats the previous clip; the API host never trims.
        WriteSidecar(2, 1, 1, "https://vidgen.example/combined.mp4", leadIn: 4.9);
        // No provider pointer at all (e.g. an imported clip).
        WriteSidecar(2, 2, 1, sourceUrl: null);

        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, _ => "tok");

        Assert.Empty(entries);
    }

    [Fact]
    public void One_entry_per_clip_even_with_multiple_take_sidecars()
    {
        WriteSidecar(3, 1, 1, "https://vidgen.example/take1.mp4");
        WriteSidecar(3, 1, 2, "https://vidgen.example/take2.mp4");

        var entries = MediaEndpoints.CollectProviderRecoveryEntries(_videoDir, url => "tok");

        Assert.Single(entries);
    }

    [Fact]
    public void Missing_video_dir_yields_no_entries()
    {
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(
            Path.Combine(_videoDir, "does-not-exist"), _ => "tok");

        Assert.Empty(entries);
    }
}
