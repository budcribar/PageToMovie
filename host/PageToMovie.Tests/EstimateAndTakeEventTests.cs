using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A1–A3 estimate honesty fields + H1/H2 video take-event telemetry.
/// </summary>
public sealed class EstimateAndTakeEventTests
{
    [Theory]
    [InlineData(null, false, false, VideoTakeKinds.Initial)]
    [InlineData(null, true, false, VideoTakeKinds.UserRegen)]
    [InlineData(VideoTakeKinds.FillHoles, false, false, VideoTakeKinds.FillHoles)]
    [InlineData(VideoTakeKinds.FillHoles, true, false, VideoTakeKinds.UserRegen)] // can't fill what exists
    [InlineData(VideoTakeKinds.StaleRegen, true, false, VideoTakeKinds.StaleRegen)]
    [InlineData(VideoTakeKinds.UserRegen, false, false, VideoTakeKinds.UserRegen)]
    [InlineData(VideoTakeKinds.Initial, true, false, VideoTakeKinds.UserRegen)]
    [InlineData(null, false, true, VideoTakeKinds.QaAuto)]
    [InlineData(VideoTakeKinds.UserRegen, true, true, VideoTakeKinds.QaAuto)]
    public void H2_VideoTakeKinds_Resolve(string? trigger, bool hadVideo, bool qa, string expected)
    {
        Assert.Equal(expected, VideoTakeKinds.Resolve(trigger, hadVideo, qa));
    }

    [Fact]
    public async Task A1_A2_A3_Screenplay_tier_estimate_includes_decision_fields()
    {
        var store = TestProjects.CreateStore("est_a_", out var root);
        try
        {
            const string projectId = "Demo";
            // Config with models so rates resolve
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                ["resolution"] = "480p",
            }));

            // A2: fountain only — no shot plan blueprint
            var fountain = """
                Title: Estimate Fixture
                Author: Unit Test

                INT. LAB - DAY

                A scientist adjusts a dial for a long moment.

                SCIENTIST
                Almost there.

                EXT. ROOF - NIGHT

                Rain lashes the antenna.
                """;
            var save = ScreenplayService.SaveDraft(store, projectId, fountain);
            Assert.True(save.Ok);
            var sign = ScreenplayService.SignOff(store, projectId);
            Assert.True(sign.Ok, sign.Error);

            var costs = new CostReportService(store);
            var report = await costs.GetReportAsync(projectId);

            Assert.Equal("screenplay", report.EstimateBasis);
            Assert.Equal("synthetic_screenplay", report.ClipSource);
            Assert.Equal("rough", report.EstimateConfidence);
            Assert.True(report.Summary.ClipsTotal > 0, "screenplay-derived clips expected");
            Assert.NotNull(report.CostPointUsd);
            Assert.NotNull(report.CostLowUsd);
            Assert.NotNull(report.CostHighUsd);
            Assert.True(report.CostPointUsd > 0);
            Assert.True(report.CostLowUsd <= report.CostPointUsd);
            Assert.True(report.CostHighUsd >= report.CostPointUsd);
            Assert.False(string.IsNullOrWhiteSpace(report.DurationLabel));
            Assert.False(string.IsNullOrWhiteSpace(report.CostLabel));
            Assert.DoesNotContain("Import a book to unlock", report.Notes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A1_Shot_plan_basis_is_blueprint_good_confidence()
    {
        var store = TestProjects.CreateStore("est_sp_", out var root);
        try
        {
            const string projectId = "Demo";
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                ["resolution"] = "480p",
            }));

            var dir = store.GetProjectDir(projectId);
            var bpPath = Path.Combine(dir, "blueprint.clips.grok.json");
            await File.WriteAllTextAsync(bpPath, """
                {
                  "movie_title": "Shot Plan Fixture",
                  "scenes": [
                    {
                      "scene_number": 1,
                      "setting": "INT. ROOM - DAY",
                      "veo_clips": [
                        { "clip_number": 1, "duration_seconds": 8, "visual_prompt": "A room." },
                        { "clip_number": 2, "duration_seconds": 6, "visual_prompt": "Closer." }
                      ]
                    }
                  ]
                }
                """);
            // Point config at blueprint if needed
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                ["resolution"] = "480p",
                ["blueprint_file"] = "blueprint.clips.grok.json",
            }));

            var costs = new CostReportService(store);
            var report = await costs.GetReportAsync(projectId);

            Assert.Equal("shot_plan", report.EstimateBasis);
            Assert.Equal("blueprint", report.ClipSource);
            Assert.Equal("good", report.EstimateConfidence);
            Assert.Equal(2, report.Summary.ClipsTotal);
            Assert.NotNull(report.CostPointUsd);
            Assert.Contains("~$", report.CostLabel);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task H1_RecordVideoGeneration_writes_full_take_event()
    {
        var store = TestProjects.CreateStore("take_h1_", out var root);
        try
        {
            const string projectId = "Demo";
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
                ["planning_model_name"] = "grok-4",
                ["resolution"] = "480p",
            }));

            var costs = new CostReportService(store);

            await costs.RecordVideoGenerationAsync(
                projectId,
                scene: 1,
                clip: 2,
                durationSec: 8,
                resolution: "480p",
                model: "grok-imagine-video",
                hasRefImage: true,
                isExtend: false,
                requestId: "req-1",
                userId: "user-a",
                keyMode: "personal",
                takeKind: VideoTakeKinds.Initial,
                stableBeatId: "beat-abc",
                hadCharRefs: true,
                hadLocRef: false);

            await costs.RecordVideoGenerationAsync(
                projectId,
                scene: 1,
                clip: 2,
                durationSec: 8,
                resolution: "480p",
                model: "grok-imagine-video",
                hasRefImage: true,
                userId: "user-a",
                keyMode: "personal",
                takeKind: VideoTakeKinds.UserRegen,
                stableBeatId: "beat-abc",
                hadCharRefs: true,
                hadLocRef: true);

            var ledger = await costs.GetCostLedgerAsync(projectId);
            var videos = ledger.Where(e => e.Kind == "video" && e.Scene == 1 && e.Clip == 2).ToList();
            Assert.Equal(2, videos.Count);

            var first = videos.OrderBy(e => e.TakeIndex ?? 0).First();
            var second = videos.OrderBy(e => e.TakeIndex ?? 0).Last();

            Assert.Equal(1, first.TakeIndex);
            Assert.Equal(VideoTakeKinds.Initial, first.TakeKind);
            Assert.Equal("user-a", first.UserId);
            Assert.Equal("personal", first.KeyMode);
            Assert.Equal("beat-abc", first.StableBeatId);
            Assert.True(first.HadCharRefs);
            Assert.False(first.HadLocRef);

            Assert.Equal(2, second.TakeIndex);
            Assert.Equal(VideoTakeKinds.UserRegen, second.TakeKind);
            Assert.True(second.HadLocRef);
            Assert.NotNull(second.MinutesSincePrevTake);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task H2_take_kinds_persist_distinct_triggers()
    {
        var store = TestProjects.CreateStore("take_h2_", out var root);
        try
        {
            const string projectId = "Demo";
            await store.SaveConfigAsync(projectId, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["model_name"] = "grok-imagine-video",
                ["image_model_name"] = "grok-imagine-image-quality",
            }));
            var costs = new CostReportService(store);

            var kinds = new[]
            {
                VideoTakeKinds.Initial,
                VideoTakeKinds.FillHoles,
                VideoTakeKinds.StaleRegen,
                VideoTakeKinds.QaAuto,
                VideoTakeKinds.UserRegen,
            };
            for (var i = 0; i < kinds.Length; i++)
            {
                await costs.RecordVideoGenerationAsync(
                    projectId, scene: 3, clip: i + 1, durationSec: 5,
                    resolution: "480p", model: "grok-imagine-video",
                    takeKind: kinds[i], userId: "u1");
            }

            var ledger = await costs.GetCostLedgerAsync(projectId);
            var got = ledger
                .Where(e => e.Scene == 3)
                .OrderBy(e => e.Clip)
                .Select(e => e.TakeKind)
                .ToList();
            Assert.Equal(kinds, got);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
