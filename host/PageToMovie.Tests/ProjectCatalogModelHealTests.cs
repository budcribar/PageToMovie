using System.Text.Json;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public class ProjectCatalogModelHealTests
{
    public ProjectCatalogModelHealTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    static Dictionary<string, JsonElement> Cfg(params (string key, object value)[] pairs)
    {
        var obj = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
            obj[k] = JsonSerializer.SerializeToElement(v);
        return obj;
    }

    [Fact]
    public void Apply_rewrites_disabled_video_review_to_catalog_default()
    {
        var cfg = Cfg(
            ("quality_model_name", "gemini-2.5-flash"),
            ("quality_provider", "gemini"),
            ("video_review_model_name", "gemini-2.5-flash"),
            ("planning_model_name", "grok-4.6"),
            ("model_selections", new Dictionary<string, string>
            {
                ["video-review"] = "gemini-2.5-flash",
                ["chat"] = "grok-4.6",
            }));

        Assert.True(ProjectCatalogModelHeal.Apply(cfg));

        var expected = SupportedModelCatalog.DefaultModelIdForCapability("video-review");
        Assert.Equal("gemini-3.7-flash", expected);
        Assert.Equal(expected, cfg["quality_model_name"].GetString());
        Assert.Equal(expected, cfg["video_review_model_name"].GetString());
        Assert.Equal(expected, cfg["model_selections"].GetProperty("video-review").GetString());
        Assert.Equal("gemini", cfg["quality_provider"].GetString());
        Assert.Equal("grok-4.6", cfg["planning_model_name"].GetString());
        Assert.Equal("grok-4.6", cfg["model_selections"].GetProperty("chat").GetString());
        Assert.Equal(expected, ProjectModelSelection.RequireVideoReview(cfg));
    }

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    public void Apply_rewrites_disabled_prior_image_ids_to_catalog_default(string stored)
    {
        var cfg = Cfg(
            ("image_model_name", stored),
            ("model_selections", new Dictionary<string, string> { ["image"] = stored }),
            ("model_name", "grok-imagine-video"));

        Assert.True(ProjectCatalogModelHeal.Apply(cfg));
        var expected = SupportedModelCatalog.DefaultModelIdForCapability("image");
        Assert.Equal("grok-imagine-image-2.0", expected);
        Assert.Equal(expected, cfg["image_model_name"].GetString());
        Assert.Equal(expected, cfg["model_selections"].GetProperty("image").GetString());
        Assert.Equal("grok-imagine-video", cfg["model_name"].GetString());
        Assert.Equal(expected, ProjectModelSelection.RequireImage(cfg));
    }

    [Fact]
    public void Apply_leaves_enabled_stored_id_alone()
    {
        var cfg = Cfg(
            ("quality_model_name", "grok-4.5"),
            ("quality_provider", "grok"),
            ("planning_model_name", "grok-4.6"),
            ("model_name", "grok-imagine-video"));

        Assert.False(ProjectCatalogModelHeal.Apply(cfg));
        Assert.Equal("grok-4.5", cfg["quality_model_name"].GetString());
        Assert.Equal("grok-4.6", cfg["planning_model_name"].GetString());
        Assert.Equal("grok-imagine-video", cfg["model_name"].GetString());
    }

    [Fact]
    public void Apply_does_not_invent_a_model_when_slot_is_empty()
    {
        var cfg = Cfg(("planning_model_name", "grok-4.6"));
        Assert.False(ProjectCatalogModelHeal.Apply(cfg));
        Assert.False(cfg.ContainsKey("quality_model_name"));
    }

    [Fact]
    public async Task GetConfigAsync_persists_healed_video_review_default()
    {
        var store = TestProjects.CreateStore("heal-vr-", out var root, "Demo");
        try
        {
            var path = Path.Combine(root, "projects", "Demo", "pipeline_config.json");
            await File.WriteAllTextAsync(path, """
                {
                  "quality_model_name": "gemini-2.5-flash",
                  "quality_provider": "gemini",
                  "video_review_model_name": "gemini-2.5-flash",
                  "planning_model_name": "grok-4.6",
                  "model_selections": { "video-review": "gemini-2.5-flash", "chat": "grok-4.6" }
                }
                """);

            var cfg = await store.GetConfigAsync("Demo");
            var expected = SupportedModelCatalog.DefaultModelIdForCapability("video-review");
            Assert.Equal("gemini-3.7-flash", expected);
            Assert.Equal(expected, cfg["quality_model_name"].GetString());
            Assert.Equal(expected, cfg["video_review_model_name"].GetString());
            Assert.Equal(expected, cfg["model_selections"].GetProperty("video-review").GetString());
            Assert.Equal("grok-4.6", cfg["planning_model_name"].GetString());
            Assert.Equal(expected, ProjectModelSelection.RequireVideoReview(cfg));

            using var disk = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(expected, disk.RootElement.GetProperty("quality_model_name").GetString());
            Assert.Equal(expected, disk.RootElement.GetProperty("video_review_model_name").GetString());
            Assert.Equal(expected, disk.RootElement.GetProperty("model_selections").GetProperty("video-review").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task GetConfigAsync_leaves_enabled_quality_model_on_disk()
    {
        var store = TestProjects.CreateStore("heal-keep-", out var root, "Demo");
        try
        {
            var path = Path.Combine(root, "projects", "Demo", "pipeline_config.json");
            await File.WriteAllTextAsync(path, """
                {
                  "quality_model_name": "grok-4.5",
                  "planning_model_name": "grok-4.6"
                }
                """);

            var cfg = await store.GetConfigAsync("Demo");
            Assert.Equal("grok-4.5", cfg["quality_model_name"].GetString());
            using var disk = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("grok-4.5", disk.RootElement.GetProperty("quality_model_name").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ActivateAsync_heals_disabled_video_review_on_open()
    {
        var store = TestProjects.CreateStore("heal-open-", out var root, "Demo");
        try
        {
            var path = Path.Combine(root, "projects", "Demo", "pipeline_config.json");
            await File.WriteAllTextAsync(path, """
                { "quality_model_name": "gemini-2.5-flash" }
                """);

            await store.ActivateAsync("Demo");
            var expected = SupportedModelCatalog.DefaultModelIdForCapability("video-review");
            using var disk = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(expected, disk.RootElement.GetProperty("quality_model_name").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
