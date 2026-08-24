using System.Text.Json;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]
public class ProjectCatalogModelHealTests : IDisposable
{
    public ProjectCatalogModelHealTests()
    {
        // Real catalog — ReloadCatalog() follows PageToMovie_USE_FAKES where several
        // retired ids are still enabled.
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

    static Dictionary<string, JsonElement> Cfg(params (string key, object value)[] pairs)
    {
        var obj = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
            obj[k] = JsonSerializer.SerializeToElement(v);
        return obj;
    }

    [Fact]
    public void Apply_throws_for_disabled_video_review_and_does_not_rewrite()
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

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalogModelHeal.Apply(cfg));
        Assert.Contains("video-review", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gemini-2.5-flash", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog default is not applied", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("gemini-2.5-flash", cfg["quality_model_name"].GetString());
        Assert.Equal("gemini-2.5-flash", cfg["video_review_model_name"].GetString());
        Assert.Equal("gemini-2.5-flash", cfg["model_selections"].GetProperty("video-review").GetString());
        Assert.Equal("gemini", cfg["quality_provider"].GetString());
        Assert.Equal("grok-4.6", cfg["planning_model_name"].GetString());

        var requireEx = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireVideoReview(cfg));
        Assert.Contains("gemini-2.5-flash", requireEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    public void Apply_throws_for_disabled_image_ids_and_does_not_rewrite(string stored)
    {
        var cfg = Cfg(
            ("image_model_name", stored),
            ("model_selections", new Dictionary<string, string> { ["image"] = stored }),
            ("model_name", "grok-imagine-video"));

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalogModelHeal.Apply(cfg));
        Assert.Contains("image", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(stored, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stored, cfg["image_model_name"].GetString());
        Assert.Equal(stored, cfg["model_selections"].GetProperty("image").GetString());
        Assert.Equal("grok-imagine-video", cfg["model_name"].GetString());

        var requireEx = Assert.Throws<InvalidOperationException>(
            () => ProjectModelSelection.RequireImage(cfg));
        Assert.Contains(stored, requireEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_throws_for_empty_required_video_slot()
    {
        var cfg = Cfg(("model_name", "  "));
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalogModelHeal.Apply(cfg));
        Assert.Contains("video", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no model selected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("  ", cfg["model_name"].GetString());
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
    public void Apply_does_not_invent_a_model_when_slot_is_absent()
    {
        var cfg = Cfg(("planning_model_name", "grok-4.6"));
        Assert.False(ProjectCatalogModelHeal.Apply(cfg));
        Assert.False(cfg.ContainsKey("quality_model_name"));
        Assert.False(cfg.ContainsKey("image_model_name"));
    }

    [Fact]
    public void Apply_leaves_optional_none_unset()
    {
        var cfg = Cfg(("audio_model_name", "none"), ("voice_model_name", ""));
        Assert.False(ProjectCatalogModelHeal.Apply(cfg));
        Assert.Equal("none", cfg["audio_model_name"].GetString());
        Assert.Equal("", cfg["voice_model_name"].GetString());
    }

    [Fact]
    public void Apply_throws_for_unknown_optional_audio_and_does_not_write_none()
    {
        var cfg = Cfg(("audio_model_name", "not-a-real-audio-model"));
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalogModelHeal.Apply(cfg));
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-real-audio-model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("not-a-real-audio-model", cfg["audio_model_name"].GetString());
    }

    [Fact]
    public async Task GetConfigAsync_does_not_persist_a_replacement_for_disabled_video_review()
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
            Assert.Equal("gemini-2.5-flash", cfg["quality_model_name"].GetString());
            Assert.Equal("gemini-2.5-flash", cfg["video_review_model_name"].GetString());
            Assert.Equal("gemini-2.5-flash", cfg["model_selections"].GetProperty("video-review").GetString());
            Assert.Equal("grok-4.6", cfg["planning_model_name"].GetString());

            var requireEx = Assert.Throws<InvalidOperationException>(
                () => ProjectModelSelection.RequireVideoReview(cfg));
            Assert.Contains("gemini-2.5-flash", requireEx.Message, StringComparison.OrdinalIgnoreCase);

            using var disk = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("gemini-2.5-flash", disk.RootElement.GetProperty("quality_model_name").GetString());
            Assert.Equal("gemini-2.5-flash", disk.RootElement.GetProperty("video_review_model_name").GetString());
            Assert.Equal(
                "gemini-2.5-flash",
                disk.RootElement.GetProperty("model_selections").GetProperty("video-review").GetString());
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
    public async Task ActivateAsync_does_not_rewrite_disabled_video_review()
    {
        var store = TestProjects.CreateStore("heal-open-", out var root, "Demo");
        try
        {
            var path = Path.Combine(root, "projects", "Demo", "pipeline_config.json");
            await File.WriteAllTextAsync(path, """
                { "quality_model_name": "gemini-2.5-flash" }
                """);

            await store.ActivateAsync("Demo");
            using var disk = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("gemini-2.5-flash", disk.RootElement.GetProperty("quality_model_name").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
