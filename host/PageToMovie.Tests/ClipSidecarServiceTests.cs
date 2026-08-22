using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ClipSidecarServiceTests : IDisposable
{
    private readonly string _tempWorkspace;

    public ClipSidecarServiceTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "ptm-sidecar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempWorkspace)) Directory.Delete(_tempWorkspace, recursive: true); }
        catch { /* ignore */ }
    }

    [Fact]
    public async Task WriteSidecarAsync_creates_valid_json_sidecar()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "TestMovie");
        Directory.CreateDirectory(projectDir);

        var sidecarPath = await service.WriteSidecarAsync(
            projectDir,
            scene: 1,
            clip: 2,
            prompt: "A dark room with glowing candles",
            scriptText: "THE CONFESSOR stares into the shadows.",
            model: "grok-imagine-video",
            resolution: "480p",
            durationSeconds: 6.0,
            sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            sizeBytes: 1024_000);

        Assert.True(File.Exists(sidecarPath));
        Assert.Contains("scene_01_clip_02", sidecarPath);
        Assert.EndsWith(".clip.json", sidecarPath);

        var text = await File.ReadAllTextAsync(sidecarPath);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal("clip_sidecar.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("TestMovie", root.GetProperty("project_id").GetString());
        Assert.Equal(1, root.GetProperty("scene").GetInt32());
        Assert.Equal(2, root.GetProperty("clip").GetInt32());
        Assert.Equal("THE CONFESSOR stares into the shadows.", root.GetProperty("script_text").GetString());
        Assert.Equal("A dark room with glowing candles", root.GetProperty("visual_prompt").GetString());
        Assert.Equal("grok-imagine-video", root.GetProperty("model").GetString());
        Assert.Equal("480p", root.GetProperty("resolution").GetString());
        Assert.Equal(6.0, root.GetProperty("duration_seconds").GetDouble());
        Assert.Equal(1024_000, root.GetProperty("size_bytes").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("created_at_utc").GetString()));
    }

    [Fact]
    public async Task EnsureAllSidecarsExistAsync_backfills_missing_sidecars_for_mp4s()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "TestMovie");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        // Dummy MP4 file without sidecar
        var mp4Path = Path.Combine(videoDir, "scene_02_clip_03.mp4");
        await File.WriteAllBytesAsync(mp4Path, new byte[2048]);

        var count = await service.EnsureAllSidecarsExistAsync(projectDir);
        Assert.Equal(1, count);

        var sidecarPath = Directory.EnumerateFiles(videoDir, "*.clip.json").FirstOrDefault();
        Assert.NotNull(sidecarPath);
        Assert.True(File.Exists(sidecarPath!));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("scene").GetInt32());
        Assert.Equal(3, root.GetProperty("clip").GetInt32());
        Assert.Equal(2048, root.GetProperty("size_bytes").GetInt64());
    }

    [Fact]
    public async Task ConvertProjectClipsToNewFormatAsync_renames_clips_and_writes_take_sidecars()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "TellTaleTest");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        // Create legacy named MP4 file
        var legacyMp4 = Path.Combine(videoDir, "scene_12.mp4");
        await File.WriteAllBytesAsync(legacyMp4, new byte[1024]);

        var count = await service.ConvertProjectClipsToNewFormatAsync(projectDir);
        Assert.True(count >= 1);

        var files = Directory.GetFiles(videoDir, "*.clip.json");
        Assert.NotEmpty(files);

        var sidecarText = await File.ReadAllTextAsync(files[0]);
        using var doc = JsonDocument.Parse(sidecarText);
        var root = doc.RootElement;

        Assert.Equal(12, root.GetProperty("scene").GetInt32());
        Assert.Equal(1, root.GetProperty("take").GetInt32());
        Assert.Contains("scene_12_clip_01_take_01", files[0]);
        Assert.DoesNotContain("2026", Path.GetFileName(files[0]));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_12_clip_01_take_01.mp4")));
    }

    [Fact]
    public async Task ConvertProjectClipsToNewFormatAsync_does_not_timestamp_already_converted_takes()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_tempWorkspace, "projects", "StableTakes");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var take01 = Path.Combine(videoDir, "scene_03_clip_07_take_01.clip.json");
        await File.WriteAllTextAsync(take01, """{"scene":3,"clip":7,"take":1,"source_url":"https://vid.example/a"}""");
        var leftover = Path.Combine(videoDir, "scene_03_clip_07_take_01_20260820_172934.clip.json");
        await File.WriteAllTextAsync(leftover, """{"scene":3,"clip":7,"take":1}""");

        var count = await service.ConvertProjectClipsToNewFormatAsync(projectDir);
        Assert.True(count >= 1);

        Assert.True(File.Exists(take01), "existing take_01 must not be clobbered");
        Assert.False(File.Exists(leftover), "timestamped leftover must be migrated");
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_03_clip_07_take_02.clip.json")));
        Assert.Empty(Directory.GetFiles(videoDir, "*_20260820_*"));
    }

    [Fact]
    public async Task WriteSidecarAsync_after_leftover_alias_becomes_take_02()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_tempWorkspace, "projects", "AliasThenRegen");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var alias = Path.Combine(videoDir, "scene_03_clip_07.clip.json");
        await File.WriteAllTextAsync(alias, """{"scene":3,"clip":7,"visual_prompt":"original prompt"}""");

        Assert.True(ClipSidecarService.EnsureLegacyCanonicalHasTakeSidecar(videoDir, 3, 7));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_03_clip_07_take_01.clip.json")));
        Assert.Equal(2, ClipSidecarService.NextTakeNumber(videoDir, 3, 7));

        var written = await service.WriteSidecarAsync(
            projectDir, 3, 7, "new prompt", "", "test-model", "480p", 4, "", 0);

        Assert.EndsWith("scene_03_clip_07_take_02.clip.json", written);
        using var orig = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(videoDir, "scene_03_clip_07_take_01.clip.json")));
        Assert.Equal("original prompt", orig.RootElement.GetProperty("visual_prompt").GetString());
        using var neu = JsonDocument.Parse(await File.ReadAllTextAsync(written));
        Assert.Equal(2, neu.RootElement.GetProperty("take").GetInt32());
        Assert.Equal("new prompt", neu.RootElement.GetProperty("visual_prompt").GetString());
    }

    [Fact]
    public async Task NextTakeNumber_ignores_timestamped_leftover_stub_sidecars()
    {
        var videoDir = Path.Combine(_tempWorkspace, "stub-takes");
        Directory.CreateDirectory(videoDir);
        File.WriteAllText(
            Path.Combine(videoDir, "scene_02_clip_01_take_01_20260821_120000.clip.json"),
            """{"take":1,"visual_prompt":"stub"}""");

        Assert.Equal(1, ClipSidecarService.NextTakeNumber(videoDir, 2, 1));

        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_tempWorkspace, "projects", "StubThenRender");
        Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));
        File.Copy(
            Path.Combine(videoDir, "scene_02_clip_01_take_01_20260821_120000.clip.json"),
            Path.Combine(projectDir, "assets", "video", "scene_02_clip_01_take_01_20260821_120000.clip.json"));
        var take = await service.PersistGeneratedTakeAsync(projectDir, 2, 1, "real-card-bytes------------"u8.ToArray());
        Assert.Equal(1, take);
        Assert.True(File.Exists(Path.Combine(projectDir, "assets", "video", "scene_02_clip_01_take_01.mp4")));
    }

    [Fact]
    public async Task Two_generated_takes_then_promote_first_makes_it_current()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_tempWorkspace, "projects", "CreditsTakes");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var take1Bytes = "credits-take-one-bytes---------------"u8.ToArray();
        var take2Bytes = "credits-take-two-bytes---------------"u8.ToArray();
        Assert.Equal(1, await service.PersistGeneratedTakeAsync(projectDir, 2, 1, take1Bytes, new PersistGeneratedTakeOptions { Prompt = "card 1" }));
        Assert.Equal(2, await service.PersistGeneratedTakeAsync(projectDir, 2, 1, take2Bytes, new PersistGeneratedTakeOptions { Prompt = "card 2" }));

        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4")));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4")));
        Assert.Equal(take1Bytes, File.ReadAllBytes(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4")));
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01.mp4")));
        Assert.Equal(3, ClipSidecarService.NextTakeNumber(videoDir, 2, 1));
        Assert.Equal(2, ClipSidecarService.ReadCurrentTake(videoDir, 2, 1));
        Assert.Equal(
            Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4"),
            ClipSidecarService.CurrentTakePath(videoDir, 2, 1));

        var promoted = await projects.PromoteClipVersionAsync("CreditsTakes", 2, 1, "scene_02_clip_01_take_01.mp4");
        Assert.True(promoted);
        Assert.Equal(1, ClipSidecarService.ReadCurrentTake(videoDir, 2, 1));
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01.mp4")));
        Assert.Equal(
            Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4"),
            ClipSidecarService.CurrentTakePath(videoDir, 2, 1));
        Assert.Equal(take2Bytes, File.ReadAllBytes(Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4")));
    }

    [Fact]
    public async Task PersistGeneratedTake_does_not_create_or_refresh_a_bare_alias()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var service = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_tempWorkspace, "projects", "NoAlias");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);
        var leftover = Path.Combine(videoDir, "scene_01_clip_01.mp4");
        var leftoverBytes = "stale-leftover-alias-bytes--------"u8.ToArray();
        await File.WriteAllBytesAsync(leftover, leftoverBytes);

        var takeBytes = "fresh-take-two-bytes--------------"u8.ToArray();
        Assert.Equal(1, await service.PersistGeneratedTakeAsync(projectDir, 1, 1, takeBytes));

        Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4")));
        Assert.Equal(leftoverBytes, await File.ReadAllBytesAsync(leftover));
        Assert.Equal(1, ClipSidecarService.ReadCurrentTake(videoDir, 1, 1));
        Assert.Equal(
            Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4"),
            ClipSidecarService.CurrentTakePath(videoDir, 1, 1));
    }

    [Fact]
    public void ResolveClipVideoPath_ignores_leftover_alias_when_pointer_and_take_exist()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var projectDir = Path.Combine(_tempWorkspace, "projects", "ResolveIgnore");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_04_clip_01.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_04_clip_01_take_03.mp4"), new byte[2048]);
        ClipSidecarService.WriteCurrentTake(videoDir, 4, 1, 3);

        var resolved = projects.ResolveClipVideoPath("ResolveIgnore", 4, 1);
        Assert.Equal(Path.Combine(videoDir, "scene_04_clip_01_take_03.mp4"), resolved);
    }

    [Fact]
    public void CurrentTakePath_ignores_leftover_alias_when_pointer_and_take_exist()
    {
        var videoDir = Path.Combine(_tempWorkspace, "projects", "IgnoreAlias", "assets", "video");
        Directory.CreateDirectory(videoDir);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_03_clip_04.mp4"), "stale-alias----------------"u8.ToArray());
        File.WriteAllBytes(Path.Combine(videoDir, "scene_03_clip_04_take_02.mp4"), "current-take---------------"u8.ToArray());
        ClipSidecarService.WriteCurrentTake(videoDir, 3, 4, 2);

        var current = ClipSidecarService.CurrentTakePath(videoDir, 3, 4);
        Assert.Equal(Path.Combine(videoDir, "scene_03_clip_04_take_02.mp4"), current);
        Assert.NotEqual(Path.Combine(videoDir, "scene_03_clip_04.mp4"), current);
    }
}
