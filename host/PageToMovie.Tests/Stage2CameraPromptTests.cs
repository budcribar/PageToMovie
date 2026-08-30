using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class Stage2CameraPromptTests
{
    [Fact]
    public void PlanScene_lifts_back_to_camera_and_does_not_stack_medium_push_in()
    {
        var planned = PlanDialogue(
            visual: "Character faces the window. Camera behind, back to camera.",
            dialogue: "I will not turn around.");
        var vp = ClipVisual(planned, 0);
        var camera = CameraTagWriter.ReadCameraTag(vp);
        var action = CameraTagWriter.ReadActionTag(vp);

        Assert.False(string.IsNullOrWhiteSpace(camera));
        Assert.Contains("camera behind", camera, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("back to camera", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Medium shot", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slow push-in", camera, StringComparison.OrdinalIgnoreCase);

        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.Contains("faces the window", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("camera behind", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35mm", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shallow depth of field", action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanScene_classifier_framing_wins_over_cycle()
    {
        var planned = PlanDialogue(
            visual: "Character sits at the table.",
            dialogue: "True — nervous — very dreadfully nervous.",
            camera: new Dictionary<string, CameraDirective>
            {
                ["b1"] = new CameraDirective(
                    ShotScale.Wide, "24mm lens", "locked tripod",
                    "Wide hold on the empty chair, 24mm lens, static"),
            });
        var camera = CameraTagWriter.ReadCameraTag(ClipVisual(planned, 0));
        Assert.Equal("Wide hold on the empty chair, 24mm lens, static", camera);
        Assert.DoesNotContain("slow push-in", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Extreme close-up", camera, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanScene_same_speaker_reuses_previous_camera_not_ecu_on_wide_beat()
    {
        var scene = Scene(
            Beat("b1", "Wide shot of the empty room. Character stands at the sill.", "First line of the confession."),
            Beat("b2", "Character faces the window.", "Second line of the confession.", ownClip: true));
        var planned = Stage2PlannerService.PlanScene(scene, Seeds(), styleLock: null);
        Assert.NotNull(planned);
        var clips = Clips(planned!);
        Assert.True(clips.Count >= 2, $"expected two clips, got {clips.Count}");

        var cam1 = CameraTagWriter.ReadCameraTag(ClipVisual(planned, 0));
        var cam2 = CameraTagWriter.ReadCameraTag(ClipVisual(planned, 1));
        Assert.False(string.IsNullOrWhiteSpace(cam1));
        Assert.False(string.IsNullOrWhiteSpace(cam2));
        Assert.Contains("Wide", cam1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Extreme close-up", cam2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("85mm", cam2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", cam2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wide", cam2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildVisualPrompt_action_drops_lens_dof_and_shot_size()
    {
        var beat = Beat(
            "b1",
            "Character faces the window. Medium shot, 35mm lens, shallow depth of field, slow push-in.",
            "Hello.");
        var vp = Stage2PlannerService.BuildVisualPrompt(beat, Scene(beat), Seeds(), new Dictionary<string, List<string>>());
        var action = CameraTagWriter.ReadActionTag(vp);
        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.Contains("faces the window", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Medium shot", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35mm", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shallow depth of field", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("push-in", action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanScene_optics_is_fstop_only()
    {
        var planned = PlanDialogue(
            visual: "Character sits.",
            dialogue: "A short line.",
            dof: new Dictionary<string, DepthOfFieldDirective>
            {
                ["b1"] = new DepthOfFieldDirective(
                    "f/1.4 shallow depth of field, creamy soft bokeh",
                    "Midground: eyes",
                    "Static focus"),
            });
        var optics = CameraTagWriter.ReadOpticsTag(ClipVisual(planned, 0));
        Assert.Equal("f/1.4", optics);
        Assert.DoesNotContain("depth of field", optics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bokeh", optics, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> PlanDialogue(
        string visual,
        string dialogue,
        Dictionary<string, CameraDirective>? camera = null,
        Dictionary<string, DepthOfFieldDirective>? dof = null)
    {
        var beat = Beat("b1", visual, dialogue);
        var planned = Stage2PlannerService.PlanScene(
            Scene(beat), Seeds(), styleLock: null, aiCamera: camera, aiDof: dof);
        Assert.NotNull(planned);
        return planned!;
    }

    private static Dictionary<string, object?> Scene(params Dictionary<string, object?>[] beats) => new()
    {
        ["scene_number"] = 1,
        ["setting"] = "INT. ROOM - DAY",
        ["story_beats"] = beats.Cast<object?>().ToList(),
        ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
    };

    private static Dictionary<string, object?> Beat(string id, string visual, string dialogue, bool ownClip = false)
    {
        var b = new Dictionary<string, object?>
        {
            ["beat_id"] = id,
            ["visual_event"] = visual,
            ["dialogue"] = dialogue,
            ["speaker"] = "Character_Narrator",
            ["delivery"] = "spoken_on_camera",
            ["primary_subject"] = "Character_Narrator",
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
        };
        if (ownClip)
            b["own_clip"] = true;
        return b;
    }

    private static Dictionary<string, object?> Seeds() => new()
    {
        ["Character_Narrator"] = new Dictionary<string, object?>
        {
            ["display_name_policy"] = "ok_anytime",
            ["canonical_given_name"] = "Narrator",
        },
    };

    private static List<Dictionary<string, object?>> Clips(Dictionary<string, object?> planned)
    {
        Assert.True(planned.TryGetValue("veo_clips", out var raw));
        var list = Assert.IsType<List<object?>>(raw);
        return list.OfType<Dictionary<string, object?>>().ToList();
    }

    private static string ClipVisual(Dictionary<string, object?> planned, int index)
    {
        var clips = Clips(planned);
        Assert.True(index < clips.Count, $"clip {index} missing, count={clips.Count}");
        return clips[index]["visual_prompt"]?.ToString() ?? "";
    }
}
