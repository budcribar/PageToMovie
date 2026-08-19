using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Mary19 (2026-08-19): scene playback repeated lines. Video-extend clips' provider copy is the
/// COMBINED video (previous clip + new footage); after "server keeps no MP4" every consumer that
/// streamed source_url got that combined file. These pin the contract: the sidecar records the
/// lead-in, sidecar lookup is by pattern (take-named files), and a combined copy is never handed
/// out as standalone.
/// </summary>
public sealed class ClipProviderSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptm-provsrc-" + Guid.NewGuid().ToString("N"));

    public ClipProviderSourceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Sidecar_records_lead_in_and_is_found_by_pattern_not_exact_name()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_root, "projects", "P");
        Directory.CreateDirectory(projectDir);

        var path = await service.WriteSidecarAsync(projectDir, 1, 2, "prompt", "", "grok-imagine-video", "480p", 5.0, "", 0,
            sourceUrl: "https://vidgen.example/combined.mp4", sourceProvider: "grok", providerLeadInSeconds: 4.96);

        Assert.EndsWith("scene_01_clip_02_take_01.clip.json", path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(4.96, doc.RootElement.GetProperty(ClipProviderSource.LeadInProperty).GetDouble(), 3);

        var videoDir = Path.Combine(projectDir, "assets", "video");
        var src = ClipProviderSource.ReadForClip(videoDir, 1, 2);
        Assert.NotNull(src);
        Assert.True(src!.IsCombined);
        Assert.Equal(4.96, src.LeadInSeconds, 3);

        // The mp4 the app looks for is scene_01_clip_02.mp4 — the sidecar next to it is take-named.
        var mp4Path = Path.Combine(videoDir, "scene_01_clip_02.mp4");
        Assert.NotNull(ClipProviderSource.ReadForMp4(mp4Path));
        Assert.True(FilmJobService.SidecarHasProviderSource(mp4Path));
        Assert.False(File.Exists(Path.ChangeExtension(mp4Path, null) + ".clip.json"), "exact-name lookup would have found nothing");
    }

    [Fact]
    public async Task Fresh_clip_sidecar_has_no_lead_in()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_root, "projects", "P");
        Directory.CreateDirectory(projectDir);
        var path = await service.WriteSidecarAsync(projectDir, 1, 1, "prompt", "", "grok-imagine-video", "480p", 8.0, "", 0,
            sourceUrl: "https://vidgen.example/fresh.mp4", sourceProvider: "grok");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(doc.RootElement.TryGetProperty(ClipProviderSource.LeadInProperty, out _));
        var src = ClipProviderSource.Read(path)!;
        Assert.False(src.IsCombined);
        Assert.True(src.HasProviderCopy);
    }

    [Fact]
    public async Task Materialize_marks_a_combined_copy_it_could_not_trim_instead_of_passing_it_off_as_standalone()
    {
        // The downloader writes fake bytes (not a real mp4), so ffmpeg — if even present — cannot
        // trim: the result must still say how much head belongs to the previous clip.
        var combined = new ClipProviderSource("https://vidgen.example/combined.mp4", null, 5.0, 4);
        var mat = await ClipProviderSource.TryMaterializeAsync(combined, CancellationToken.None,
            (url, dest, ct) => File.WriteAllBytesAsync(dest, new byte[4096], ct));
        try
        {
            Assert.NotNull(mat);
            Assert.False(mat!.IsStandalone);
            Assert.Equal(5.0, mat.LeadInSecondsRemaining);
            Assert.True(File.Exists(mat.Path));
        }
        finally { ClipProviderSource.TryDelete(mat?.Path); }

        var fresh = new ClipProviderSource("https://vidgen.example/fresh.mp4", null, 0, 8);
        var mat2 = await ClipProviderSource.TryMaterializeAsync(fresh, CancellationToken.None,
            (url, dest, ct) => File.WriteAllBytesAsync(dest, new byte[4096], ct));
        try
        {
            Assert.NotNull(mat2);
            Assert.True(mat2!.IsStandalone);
        }
        finally { ClipProviderSource.TryDelete(mat2?.Path); }

        Assert.Null(await ClipProviderSource.TryMaterializeAsync(new ClipProviderSource(null, "file_x", 0, 5), CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
