using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectMigrationServiceTests : IDisposable
{
    private readonly string _tempWorkspace;

    public ProjectMigrationServiceTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "ptm-migration-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempWorkspace)) Directory.Delete(_tempWorkspace, recursive: true); }
        catch { /* ignore */ }
    }

    [Fact]
    public async Task MigrateIfNeededAsync_upgrades_v0_project_to_current_and_updates_schema_version()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var sidecars = new ClipSidecarService();
        var migration = new ProjectMigrationService(sidecars);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "UnversionedMovie");
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        // Create legacy v0 project.json without schema_version
        var projectJson = Path.Combine(projectDir, "project.json");
        await File.WriteAllTextAsync(projectJson, "{\"id\":\"UnversionedMovie\",\"title\":\"Unversioned Movie\"}");

        // Create legacy MP4 clip
        var legacyMp4 = Path.Combine(videoDir, "scene_01_clip_02.mp4");
        await File.WriteAllBytesAsync(legacyMp4, new byte[512]);

        var migrated = await migration.MigrateIfNeededAsync(projectDir);
        Assert.True(migrated);

        // Check project.json updated to the current schema version
        var text = await File.ReadAllTextAsync(projectJson);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal(ProjectMigrationService.CurrentSchemaVersion, root.GetProperty("schema_version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("migrated_at_utc").GetString()));

        // Check clip sidecar created
        var sidecarsList = Directory.GetFiles(videoDir, "*.clip.json");
        Assert.NotEmpty(sidecarsList);
    }

    [Fact]
    public async Task MigrateIfNeededAsync_is_noop_for_already_current_project()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var sidecars = new ClipSidecarService();
        var migration = new ProjectMigrationService(sidecars);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "VersionedMovie");
        Directory.CreateDirectory(projectDir);

        var projectJson = Path.Combine(projectDir, "project.json");
        await File.WriteAllTextAsync(projectJson,
            $"{{\"id\":\"VersionedMovie\",\"schema_version\":\"{ProjectMigrationService.CurrentSchemaVersion}\"}}");

        var migrated = await migration.MigrateIfNeededAsync(projectDir);
        Assert.False(migrated);
    }

    [Fact]
    public async Task MigrateIfNeededAsync_upgrades_v1_project_visual_prompt_labels_to_tags()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _tempWorkspace }));
        var sidecars = new ClipSidecarService();
        var migration = new ProjectMigrationService(sidecars);

        var projectDir = Path.Combine(_tempWorkspace, "projects", "V1Movie");
        Directory.CreateDirectory(projectDir);

        var projectJson = Path.Combine(projectDir, "project.json");
        await File.WriteAllTextAsync(projectJson, "{\"id\":\"V1Movie\",\"schema_version\":\"v1\"}");

        var blueprintPath = Path.Combine(projectDir, "blueprint.clips.grok.json");
        await File.WriteAllTextAsync(blueprintPath, """
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "veo_clips": [
                    {
                      "clip_number": 1,
                      "visual_prompt": "INT. ROOM. Character_Narrator speaks. Camera directive: Medium shot, 35mm lens. Performance: Calm delivery. Optics: f/2.0 shallow depth. Color grading: Kodak Vision3 500T."
                    }
                  ]
                }
              ]
            }
            """);

        var migrated = await migration.MigrateIfNeededAsync(projectDir);
        Assert.True(migrated);

        var text = await File.ReadAllTextAsync(projectJson);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("v2", doc.RootElement.GetProperty("schema_version").GetString());

        // Parse (not raw-text-scan) the written file — System.Text.Json's default writer HTML-
        // escapes '<'/'>' as </> in the on-disk bytes, which is transparent to any JSON
        // reader (including ClipVideoPromptBuilder reading visual_prompt back via GetString()) but
        // would make a raw-text Assert.Contains for "<Camera>" fail despite the migration having
        // worked correctly.
        using var blueprintDoc = JsonDocument.Parse(await File.ReadAllTextAsync(blueprintPath));
        var migratedPrompt = blueprintDoc.RootElement
            .GetProperty("scenes")[0].GetProperty("veo_clips")[0].GetProperty("visual_prompt").GetString();
        Assert.Contains("<Camera>Medium shot, 35mm lens.</Camera>", migratedPrompt);
        Assert.Contains("<Performance>Calm delivery.</Performance>", migratedPrompt);
        Assert.Contains("<Optics>f/2.0 shallow depth.</Optics>", migratedPrompt);
        // Color grading: is deliberately left as plain text (see MigrateVisualPromptLabelText).
        Assert.Contains("Color grading: Kodak Vision3 500T.", migratedPrompt);
        Assert.DoesNotContain("Camera directive:", migratedPrompt);
        Assert.DoesNotContain("Performance:", migratedPrompt);
        Assert.DoesNotContain("Optics: f", migratedPrompt);
    }
}
