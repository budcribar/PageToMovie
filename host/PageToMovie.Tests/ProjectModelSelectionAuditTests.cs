using System.Text.Json;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public class ProjectModelSelectionAuditTests : IDisposable
{
    public ProjectModelSelectionAuditTests()
    {
        // Real embedded catalog — not ReloadCatalog(), which follows PageToMovie_USE_FAKES
        // and would load models_catalog.fake.json (those ids are still enabled there).
        using var stream = typeof(SupportedModelCatalog).Assembly
            .GetManifestResourceStream("PageToMovie.Core.config.models_catalog.json")
            ?? throw new InvalidOperationException("Real models catalog resource missing.");
        using var reader = new StreamReader(stream);
        Assert.True(SupportedModelCatalog.TryLoadFromJson(reader.ReadToEnd()));
    }

    public void Dispose()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public async Task List_marks_disabled_stored_ids_needsUpdate_and_does_not_heal()
    {
        var store = TestProjects.CreateStore("audit-stale-", out var root, "Stale");
        try
        {
            var flash = SupportedModelCatalog.Find("gemini-2.5-flash", ModelCapability.Chat);
            var quality = SupportedModelCatalog.Find("grok-imagine-image-quality", ModelCapability.Image);
            Assert.False(flash?.Enabled);
            Assert.False(quality?.Enabled);

            WriteProject(root, "Stale", "Stale Film", """
                {
                  "quality_model_name": "gemini-2.5-flash",
                  "image_model_name": "grok-imagine-image-quality",
                  "planning_model_name": "grok-4.6",
                  "model_selections": {
                    "image": "grok-imagine-image-quality",
                    "video-review": "gemini-2.5-flash"
                  }
                }
                """);

            var rows = await store.ListProjectModelSelectionsAsync();
            var stale = Assert.Single(rows, r => r.Id == "Stale");
            Assert.True(stale.NeedsUpdate);
            Assert.Null(stale.Error);
            Assert.Equal("gemini-2.5-flash", stale.QualityModelName?.Id);
            Assert.False(stale.QualityModelName?.Enabled);
            Assert.True(stale.QualityModelName?.Deprecated);
            Assert.Equal("grok-imagine-image-quality", stale.ImageModelName?.Id);
            Assert.False(stale.ImageModelName?.Enabled);
            Assert.Equal("grok-4.6", stale.PlanningModelName?.Id);
            Assert.True(stale.PlanningModelName?.Enabled);

            using var disk = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "projects", "Stale", "pipeline_config.json")));
            Assert.Equal("gemini-2.5-flash", disk.RootElement.GetProperty("quality_model_name").GetString());
            Assert.Equal("grok-imagine-image-quality", disk.RootElement.GetProperty("image_model_name").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task List_current_defaults_do_not_need_update()
    {
        var store = TestProjects.CreateStore("audit-ok-", out var root, "Current");
        try
        {
            var video = SupportedModelCatalog.DefaultModelIdForCapability("video");
            var image = SupportedModelCatalog.DefaultModelIdForCapability("image");
            var chat = SupportedModelCatalog.DefaultModelIdForCapability("chat");
            var vision = SupportedModelCatalog.DefaultModelIdForCapability("vision");
            var review = SupportedModelCatalog.DefaultModelIdForCapability("video-review");
            Assert.False(string.IsNullOrWhiteSpace(video));
            Assert.False(string.IsNullOrWhiteSpace(image));
            Assert.False(string.IsNullOrWhiteSpace(chat));
            Assert.False(string.IsNullOrWhiteSpace(vision));
            Assert.False(string.IsNullOrWhiteSpace(review));

            WriteProject(root, "Current", "Current Film", $$"""
                {
                  "model_name": "{{video}}",
                  "image_model_name": "{{image}}",
                  "planning_model_name": "{{chat}}",
                  "vision_model_name": "{{vision}}",
                  "quality_model_name": "{{review}}"
                }
                """);

            var rows = await store.ListProjectModelSelectionsAsync();
            var current = Assert.Single(rows, r => r.Id == "Current");
            Assert.False(current.NeedsUpdate);
            Assert.Null(current.Error);
            Assert.Equal(image, current.ImageModelName?.Id);
            Assert.Equal(review, current.QualityModelName?.Id);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task List_missing_config_and_unreadable_config_are_clean_rows()
    {
        var store = TestProjects.CreateStore("audit-miss-", out var root, "NoConfig");
        try
        {
            WriteProject(root, "NoConfig", "No Config", configJson: null);
            WriteProject(root, "BadJson", "Bad Json", "{ not-json");

            var rows = await store.ListProjectModelSelectionsAsync();
            var missing = Assert.Single(rows, r => r.Id == "NoConfig");
            Assert.False(missing.NeedsUpdate);
            Assert.Null(missing.Error);
            Assert.Null(missing.QualityModelName?.Id);

            var bad = Assert.Single(rows, r => r.Id == "BadJson");
            Assert.False(string.IsNullOrWhiteSpace(bad.Error));
            Assert.False(bad.NeedsUpdate);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    static void WriteProject(string root, string id, string title, string? configJson)
    {
        var dir = Path.Combine(root, "projects", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), $$"""{"id":"{{id}}","title":"{{title}}"}""");
        if (configJson is not null)
            File.WriteAllText(Path.Combine(dir, "pipeline_config.json"), configJson);
    }
}
