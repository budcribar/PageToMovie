using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Wardrobe SSoT: identity list + beat put_on/remove + classifier delta. Never replace.
/// </summary>
public sealed class WardrobeMergeTests
{
    [Fact]
    public void ApplyAiWardrobeOverrides_merges_outerwear_and_keeps_identity()
    {
        var wardrobe = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new List<string> { "pale pinafore", "rose ribbon" },
        };

        Stage2PlannerService.ApplyAiWardrobeOverrides(
            wardrobe,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Mary"] = "tweed walking coat",
            });

        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("pale pinafore", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("rose ribbon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("tweed walking coat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wardrobe["Character_Mary"], s =>
            s.Contains("trench", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("pinafore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyAiWardrobeOverrides_nightwear_adds_without_deleting_identity()
    {
        var wardrobe = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new List<string> { "pale pinafore", "rose ribbon" },
        };

        Stage2PlannerService.ApplyAiWardrobeOverrides(
            wardrobe,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Mary"] = "loose white cotton nightshirt",
            });

        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("pale pinafore", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("rose ribbon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("nightshirt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyAiWardrobeOverrides_does_not_duplicate_identity_items_restated_by_ai()
    {
        var wardrobe = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new List<string> { "pale pinafore", "rose ribbon" },
        };

        Stage2PlannerService.ApplyAiWardrobeOverrides(
            wardrobe,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Mary"] = "pale pinafore, rose ribbon, wool walking coat",
            });

        var pinaforeHits = wardrobe["Character_Mary"]
            .Count(s => s.Contains("pinafore", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, pinaforeHits);
        Assert.Contains(wardrobe["Character_Mary"], s => s.Contains("wool walking coat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanScene_ext_day_keeps_pinafore_when_classifier_suggests_coat()
    {
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "Mary",
                ["wardrobe_always"] = new List<object?> { "pale pinafore", "rose ribbon" },
            },
        };
        var beat = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["visual_event"] = "MARY walks the lamb along the lane.",
            ["dialogue"] = "And everywhere that Mary went.",
            ["speaker"] = "Character_Mary",
            ["delivery"] = "spoken_on_camera",
            ["primary_subject"] = "Character_Mary",
            ["characters_on_screen"] = new List<object?> { "Character_Mary" },
        };
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 3,
            ["setting"] = "EXT. COUNTRY LANE - DAY",
            ["story_beats"] = new List<object?> { beat },
            ["characters_on_screen"] = new List<object?> { "Character_Mary" },
        };

        var planned = Stage2PlannerService.PlanScene(
            scene,
            seeds,
            styleLock: null,
            aiWardrobe: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Mary"] = "tweed walking coat",
            });
        Assert.NotNull(planned);
        var clips = Assert.IsType<List<object?>>(planned!["veo_clips"]);
        var clip = Assert.IsType<Dictionary<string, object?>>(clips[0]);
        var vp = clip["visual_prompt"]?.ToString() ?? "";

        Assert.Contains("<Wardrobe>", vp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pale pinafore", vp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rose ribbon", vp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tweed walking coat", vp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanScene_beat_remove_can_drop_identity_item()
    {
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "Mary",
                ["wardrobe_always"] = new List<object?> { "pale pinafore", "rose ribbon" },
            },
        };
        var beat = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["visual_event"] = "MARY hangs her pinafore on the peg and sits on the bed.",
            ["primary_subject"] = "Character_Mary",
            ["characters_on_screen"] = new List<object?> { "Character_Mary" },
            ["wardrobe_put_on"] = new List<object?> { "plain white nightshirt" },
            ["wardrobe_remove"] = new List<object?> { "pale pinafore" },
        };
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 4,
            ["setting"] = "INT. BEDCHAMBER - NIGHT",
            ["story_beats"] = new List<object?> { beat },
            ["characters_on_screen"] = new List<object?> { "Character_Mary" },
        };

        var planned = Stage2PlannerService.PlanScene(scene, seeds, styleLock: null);
        Assert.NotNull(planned);
        var clips = Assert.IsType<List<object?>>(planned!["veo_clips"]);
        var clip = Assert.IsType<Dictionary<string, object?>>(clips[0]);
        var vp = clip["visual_prompt"]?.ToString() ?? "";

        Assert.DoesNotContain("pale pinafore", vp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rose ribbon", vp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nightshirt", vp, StringComparison.OrdinalIgnoreCase);
    }
}
