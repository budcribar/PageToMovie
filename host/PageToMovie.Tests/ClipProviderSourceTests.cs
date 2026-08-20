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
    public async Task Materialize_combined_copy_keeps_lead_in_instead_of_trimming()
    {
        // Combined provider copies stay combined. The API host does not spawn ffmpeg;
        // consumers offset by LeadInSecondsRemaining (browser slices via ProviderLeadInSeconds).
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

    [Fact]
    public async Task Materialize_file_id_only_downloads_via_file_id_and_keeps_combined_lead_in()
    {
        var src = new ClipProviderSource(null, "file_combined", 5.0, 10);
        var mat = await ClipProviderSource.TryMaterializeAsync(
            src,
            CancellationToken.None,
            downloadFileId: (id, dest, ct) =>
            {
                Assert.Equal("file_combined", id);
                return File.WriteAllBytesAsync(dest, new byte[4096], ct);
            });
        try
        {
            Assert.NotNull(mat);
            Assert.False(mat!.IsStandalone);
            Assert.Equal(5.0, mat.LeadInSecondsRemaining);
            Assert.True(File.Exists(mat.Path));
            Assert.True(new FileInfo(mat.Path).Length >= 1024);
        }
        finally { ClipProviderSource.TryDelete(mat?.Path); }
    }

    [Fact]
    public async Task Materialize_url_404_falls_back_to_file_id()
    {
        var src = new ClipProviderSource("https://vidgen.example/expired.mp4", "file_live", 0, 8);
        var urlHits = 0;
        var fileHits = 0;
        var mat = await ClipProviderSource.TryMaterializeAsync(
            src,
            CancellationToken.None,
            download: (_, _, _) =>
            {
                urlHits++;
                throw new HttpRequestException("404");
            },
            downloadFileId: (id, dest, ct) =>
            {
                fileHits++;
                Assert.Equal("file_live", id);
                return File.WriteAllBytesAsync(dest, new byte[4096], ct);
            });
        try
        {
            Assert.NotNull(mat);
            Assert.True(mat!.IsStandalone);
            Assert.Equal(1, urlHits);
            Assert.Equal(1, fileHits);
        }
        finally { ClipProviderSource.TryDelete(mat?.Path); }
    }

    [Fact]
    public async Task Materialize_url_success_does_not_call_file_id()
    {
        var src = new ClipProviderSource("https://vidgen.example/fresh.mp4", "file_unused", 0, 8);
        var fileHits = 0;
        var mat = await ClipProviderSource.TryMaterializeAsync(
            src,
            CancellationToken.None,
            download: (_, dest, ct) => File.WriteAllBytesAsync(dest, new byte[4096], ct),
            downloadFileId: (_, _, _) =>
            {
                fileHits++;
                return Task.CompletedTask;
            });
        try
        {
            Assert.NotNull(mat);
            Assert.Equal(0, fileHits);
        }
        finally { ClipProviderSource.TryDelete(mat?.Path); }
    }

    [Fact]
    public async Task TryOpenAsync_empty_url_uses_file_id_only()
    {
        var opened = await ClipProviderSource.TryOpenAsync(
            "",
            "file_only",
            (_, _) => Task.FromResult<string?>("from-url"),
            (id, _) => Task.FromResult<string?>(id),
            CancellationToken.None);
        Assert.Equal("file_only", opened);
    }

    [Fact]
    public async Task TryOpenAsync_url_null_result_falls_back_to_file_id()
    {
        var opened = await ClipProviderSource.TryOpenAsync(
            "https://vidgen.example/dead.mp4",
            "file_fallback",
            (_, _) => Task.FromResult<string?>(null),
            (id, _) => Task.FromResult<string?>(id),
            CancellationToken.None);
        Assert.Equal("file_fallback", opened);
    }

    [Fact]
    public async Task TryOpenAsync_url_success_skips_file_id()
    {
        var fileHits = 0;
        var opened = await ClipProviderSource.TryOpenAsync(
            "https://vidgen.example/ok.mp4",
            "file_unused",
            (url, _) => Task.FromResult<string?>(url),
            (_, _) =>
            {
                fileHits++;
                return Task.FromResult<string?>("nope");
            },
            CancellationToken.None);
        Assert.Equal("https://vidgen.example/ok.mp4", opened);
        Assert.Equal(0, fileHits);
    }

    [Fact]
    public void ReadForClip_skips_newer_sidecar_that_omits_provider_pointer()
    {
        var videoDir = Path.Combine(_root, "assets", "video");
        Directory.CreateDirectory(videoDir);
        var older = Path.Combine(videoDir, "scene_01_clip_02_take_01.clip.json");
        var newer = Path.Combine(videoDir, "scene_01_clip_02_take_02.clip.json");
        File.WriteAllText(older, """{"scene":1,"clip":2,"source_url":"https://vidgen.example/take1.mp4","source_file_id":"file_live"}""");
        File.WriteAllText(newer, """{"scene":1,"clip":2,"schema_version":"clip_sidecar.v1"}""");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var src = ClipProviderSource.ReadForClip(videoDir, 1, 2);
        Assert.NotNull(src);
        Assert.True(src!.HasProviderCopy);
        Assert.Equal("https://vidgen.example/take1.mp4", src.SourceUrl);
        Assert.Equal("file_live", src.SourceFileId);

        var fromMp4 = ClipProviderSource.ReadForMp4(Path.Combine(videoDir, "scene_01_clip_02.mp4"));
        Assert.NotNull(fromMp4);
        Assert.True(fromMp4!.HasProviderCopy);
    }

    [Fact]
    public void Engine_does_not_expose_NativeFfmpeg()
    {
        Assert.Null(typeof(ClipProviderSource).Assembly.GetType("PageToMovie.Engine.NativeFfmpeg"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

/// <summary>Takes are the sidecars: each generation writes a new take_NN sidecar; the list shows
/// provider-hosted takes with no server or client media (Mary19: "Takes (1)" after every regen).</summary>
public sealed class ClipTakesFromSidecarsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptm-takes-" + Guid.NewGuid().ToString("N"));
    public ClipTakesFromSidecarsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Each_generation_writes_a_new_numbered_sidecar_and_all_takes_are_listed()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_root, "projects", "P");
        Directory.CreateDirectory(Path.Combine(projectDir, "source"));
        File.WriteAllText(Path.Combine(projectDir, "project.json"), "{}");

        var s1 = await service.WriteSidecarAsync(projectDir, 2, 5, "p1", "", "grok-imagine-video", "480p", 4, "", 0, sourceUrl: "https://vidgen.example/a.mp4", sourceProvider: "grok");
        var s2 = await service.WriteSidecarAsync(projectDir, 2, 5, "p2", "", "grok-imagine-video", "480p", 4, "", 0, sourceUrl: "https://vidgen.example/b.mp4", sourceProvider: "grok");
        Assert.EndsWith("_take_01.clip.json", s1);
        Assert.EndsWith("_take_02.clip.json", s2);
        Assert.True(File.Exists(s1), "the previous take's sidecar must survive");

        var takes = await projects.GetClipVersionsAsync("P", 2, 5);
        Assert.Equal(2, takes.Count);
        var current = Assert.Single(takes, t => t.IsCurrent);
        Assert.Equal(2, current.Take);
        Assert.Equal("https://vidgen.example/b.mp4", current.SourceUrl);
        Assert.Contains(takes, t => t.Take == 1 && t.SourceUrl == "https://vidgen.example/a.mp4");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
