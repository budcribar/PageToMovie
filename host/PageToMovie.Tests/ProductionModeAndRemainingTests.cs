using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A5 remaining estimate strip fields + G3/G4 production mode (draft plates optional).
/// </summary>
public sealed class ProductionModeAndRemainingTests
{
    [Theory]
    [InlineData(null, ProductionModes.Full)]
    [InlineData("", ProductionModes.Full)]
    [InlineData("full", ProductionModes.Full)]
    [InlineData("FULL", ProductionModes.Full)]
    [InlineData("draft", ProductionModes.Draft)]
    [InlineData("budget", ProductionModes.Draft)]
    [InlineData("cheap", ProductionModes.Draft)]
    public void G4_ProductionModes_Normalize(string? raw, string expected)
    {
        Assert.Equal(expected, ProductionModes.Normalize(raw));
        Assert.Equal(expected == ProductionModes.Draft, ProductionModes.IsDraft(raw));
    }

    [Fact]
    public async Task A5_Remaining_basis_and_label_when_media_on_disk()
    {
        var store = TestProjects.CreateStore("rem_a5_", out var root);
        try
        {
            const string projectId = "Demo";
            var dir = store.GetProjectDir(projectId);
            await File.WriteAllTextAsync(Path.Combine(dir, "blueprint.clips.grok.json"), """
                {
                  "movie_title": "Remaining Fixture",
                  "scenes": [
                    {
                      "scene_number": 1,
                      "setting": "INT. LAB - DAY",
                      "veo_clips": [
                        { "clip_number": 1, "duration_seconds": 5, "visual_prompt": "lab" },
                        { "clip_number": 2, "duration_seconds": 5, "visual_prompt": "lab end" }
                      ]
                    }
                  ]
                }
                """);
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                ["resolution"] = "480p",
                ["blueprint_file"] = "blueprint.clips.grok.json",
            }));

            var videoDir = Path.Combine(dir, "assets", "video");
            Directory.CreateDirectory(videoDir);
            // IndexOnDiskClips ignores files under 1KB
            var pad = new byte[2048];
            await File.WriteAllBytesAsync(Path.Combine(videoDir, "scene_01_clip_01.mp4"), pad);

            var costs = new CostReportService(store);
            var report = await costs.GetReportAsync(projectId);

            Assert.Equal(2, report.Summary.ClipsTotal);
            Assert.True(report.Summary.ClipsOnDisk >= 1, "expected at least one on-disk clip");
            Assert.Equal("remaining", report.EstimateBasis);
            Assert.Equal("remaining", report.ClipSource);
            Assert.Equal("best", report.EstimateConfidence);
            Assert.False(string.IsNullOrWhiteSpace(report.RemainingLabel));
            Assert.Contains("Spent", report.RemainingLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("remaining", report.RemainingLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("clip", report.RemainingLabel, StringComparison.OrdinalIgnoreCase);
            Assert.True(report.Summary.RemainingFirstPassUsd >= 0);
            Assert.True(report.Summary.ClipsMissing >= 1);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task G3_Draft_mode_allows_cast_without_locked_plates()
    {
        var store = TestProjects.CreateStore("draft_g3_", out var root);
        try
        {
            const string projectId = "Demo";
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                [ProductionModes.ConfigKey] = ProductionModes.Draft,
            }));

            // Speaking on-screen character with voice but no locked plate
            var dir = await store.GetProjectDirAsync(projectId);
            var source = Path.Combine(dir, "source");
            Directory.CreateDirectory(source);
            var seeds = """
                {
                  "schema_version": "cast_seeds.v1",
                  "character_seed_tokens": {
                    "Character_Hero": {
                      "display_name": "Hero",
                      "voice_profile": "warm baritone"
                    }
                  }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(source, "cast_seeds.json"), seeds);

            Assert.True(store.IsDraftProductionMode(projectId));

            var status = store.ReadCastStatus(projectId);
            Assert.True(status.Total >= 1, "cast seed should load");
            // Voice present, plates missing — draft should be ready
            Assert.True(status.ReadyForShots, "draft missing: " + string.Join("; ", status.Missing));
            Assert.Empty(store.GetCastNotReadyForVideo(projectId));

            // Full mode should still require locked plate
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                [ProductionModes.ConfigKey] = ProductionModes.Full,
            }));
            Assert.False(store.IsDraftProductionMode(projectId));
            var fullStatus = store.ReadCastStatus(projectId);
            Assert.False(fullStatus.ReadyForShots);
            Assert.NotEmpty(store.GetCastNotReadyForVideo(projectId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task G4_CostReport_surfaces_production_mode()
    {
        var store = TestProjects.CreateStore("mode_g4_", out var root);
        try
        {
            const string projectId = "Demo";
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                [ProductionModes.ConfigKey] = ProductionModes.Draft,
            }));

            var fountain = """
                Title: Mode Fixture
                Author: Unit Test

                INT. ROOM - DAY

                HERO
                Hello.
                """;
            var save = ScreenplayService.SaveDraft(store, projectId, fountain);
            Assert.True(save.Ok);
            ScreenplayService.SignOff(store, projectId);

            var costs = new CostReportService(store);
            var report = await costs.GetReportAsync(projectId);
            Assert.Equal(ProductionModes.Draft, report.ProductionMode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
