using PageToMovie.Engine;
using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CameraTagWriterTests
{
    [Theory]
    [InlineData("camera behind, back to camera", true)]
    [InlineData("Camera behind the speaker. Back to camera.", true)]
    [InlineData("Wide establishing shot of the lane.", true)]
    [InlineData("MCU push-in on the speaker.", true)]
    [InlineData("Over-the-shoulder shot toward the door.", true)]
    [InlineData("35mm lens, slow push-in.", true)]
    [InlineData("f/1.4 shallow depth of field", true)]
    [InlineData("Character faces the window.", false)]
    [InlineData("Hands rest on the table.", false)]
    [InlineData("A long silence fills the room.", false)]
    public void HasCameraLanguage_detects_orders_not_body_blocking(string action, bool expected) =>
        Assert.Equal(expected, CameraTagWriter.HasCameraLanguage(action));

    [Fact]
    public void Lift_from_back_to_camera_does_not_invent_medium_push_in()
    {
        Assert.True(CameraTagWriter.TryLiftFromAction(
            "Character faces the window. Camera behind, back to camera.", out var camera));
        Assert.Contains("camera behind", camera, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("back to camera", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Medium shot", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35mm", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("push-in", camera, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripFromAction_keeps_body_blocking_drops_camera_orders()
    {
        var stripped = CameraTagWriter.StripFromAction(
            "Character faces the window. Camera behind, back to camera. 35mm lens, shallow depth of field.");
        Assert.Contains("faces the window", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("camera behind", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("back to camera", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35mm", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shallow depth of field", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lens", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Framing_prose_that_reaches_into_Optics_is_replaced_by_the_directives_own_fields()
    {
        var row = new CameraDirective(
            ShotScale.Medium,
            "85mm portrait lens",
            "slow dolly push-in",
            "Medium shot, 85mm f/1.4 lens, shallow depth of field, creamy bokeh");

        var camera = CameraTagWriter.Resolve(
            row, actionAndBlocking: null, previousCameraTag: null,
            sameSpeakerRun: false, hasSpeech: true, onScreenCastCount: 1);

        // Composed from shot_scale / lens_spec / camera_movement — no prose surgery.
        Assert.Equal("Medium shot, 85mm portrait lens, slow dolly push-in", camera);
        Assert.DoesNotContain("f/1.4", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", camera, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bokeh", camera, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clean_framing_prose_is_kept_as_written()
    {
        var row = new CameraDirective(
            ShotScale.Wide,
            "24mm anamorphic lens",
            "locked tripod",
            "Establishing wide shot, 24mm anamorphic lens, static locked camera, subject centred with headroom");

        Assert.Equal(
            "Establishing wide shot, 24mm anamorphic lens, static locked camera, subject centred with headroom",
            CameraTagWriter.Resolve(row, null, null, false, true, 1));
    }

    [Fact]
    public void Resolve_classifier_wins_over_action_and_cycle()
    {
        var row = new CameraDirective(
            ShotScale.Wide, "24mm lens", "locked tripod",
            "Wide establishing hold, 24mm lens, static");
        var framing = CameraTagWriter.Resolve(
            row,
            "camera behind, back to camera",
            previousCameraTag: "Medium shot, 35mm lens, hold",
            sameSpeakerRun: true,
            hasSpeech: true,
            onScreenCastCount: 1);
        Assert.Equal("Wide establishing hold, 24mm lens, static", framing);
    }

    [Fact]
    public void Resolve_lifts_action_camera_instead_of_medium_hold()
    {
        var framing = CameraTagWriter.Resolve(
            classifierRow: null,
            actionAndBlocking: "camera behind, back to camera",
            previousCameraTag: null,
            sameSpeakerRun: false,
            hasSpeech: true,
            onScreenCastCount: 1);
        Assert.NotNull(framing);
        Assert.Contains("camera behind", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Medium shot", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slow push-in", framing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_reuses_previous_camera_not_ecu_eyes()
    {
        var framing = CameraTagWriter.Resolve(
            classifierRow: null,
            actionAndBlocking: "Character faces the window.",
            previousCameraTag: "Wide shot, 24mm lens, hold",
            sameSpeakerRun: true,
            hasSpeech: true,
            onScreenCastCount: 1);
        Assert.Contains("Wide shot", framing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24mm", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Extreme close-up", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("85mm", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", framing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_medium_hold_when_nothing_else_applies()
    {
        var framing = CameraTagWriter.Resolve(
            classifierRow: null,
            actionAndBlocking: "Character faces the window.",
            previousCameraTag: null,
            sameSpeakerRun: false,
            hasSpeech: true,
            onScreenCastCount: 1);
        Assert.Equal(CameraTagWriter.MediumHoldFraming, framing);
        Assert.DoesNotContain("85mm", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("macro", framing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", framing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReusePrevious_does_not_keep_ots_without_listener()
    {
        var reused = CameraTagWriter.ReusePrevious(
            "Over-the-shoulder shot, 50mm lens, listening perspective",
            onScreenCastCount: 1);
        Assert.Equal(CameraTagWriter.MediumHoldFraming, reused);
    }

    [Fact]
    public void FallbackFraming_never_invents_dof_or_ecu()
    {
        for (var step = 0; step < 8; step++)
        {
            var framing = Stage2PlannerService.GetMonologueCameraFraming(step, 1);
            Assert.Equal(CameraTagWriter.MediumHoldFraming, framing);
            Assert.DoesNotContain("shallow depth of field", framing, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Extreme close-up", framing, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Over-the-shoulder", framing, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("macro", framing, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReadCameraTag_reads_tagged_prompt()
    {
        var vp = $"{PromptTags.Wrap(PromptFieldTags.Action, "Faces the window")} {PromptTags.Wrap(PromptFieldTags.Camera, "Wide shot, 24mm lens, hold")}";
        Assert.Equal("Wide shot, 24mm lens, hold", CameraTagWriter.ReadCameraTag(vp));
    }
}
