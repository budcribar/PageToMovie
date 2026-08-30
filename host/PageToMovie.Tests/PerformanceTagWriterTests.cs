using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class PerformanceTagWriterTests
{
    [Theory]
    [InlineData("face the house", true)]
    [InlineData("back to camera, face to house", true)]
    [InlineData("look down the lens", true)]
    [InlineData("look into the camera", true)]
    [InlineData("address the viewer", true)]
    [InlineData("Character faces the window.", false)]
    [InlineData("Hands rest on the table.", false)]
    [InlineData("A long silence fills the room.", false)]
    public void HasAddressLanguage_detects_gaze_not_body_blocking(string action, bool expected) =>
        Assert.Equal(expected, PerformanceTagWriter.HasAddressLanguage(action));

    [Fact]
    public void StripEyelineFromAction_drops_face_the_house_keeps_window_blocking()
    {
        var stripped = PerformanceTagWriter.StripEyelineFromAction(
            "Character faces the window. Camera behind, face to house.");
        Assert.Contains("faces the window", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("face to house", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("face the house", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripEyelineFromAction_drops_lens_address()
    {
        var stripped = PerformanceTagWriter.StripEyelineFromAction(
            "Character sits at the table. Look down the lens. Address the viewer.");
        Assert.Contains("sits at the table", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("look down the lens", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address the viewer", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSinglePerformanceLock_keeps_one_vision_meta_lock()
    {
        const string lockText =
            "PERFORMANCE LOCK: first-person confessional; addresses an implied listener when speaking.";
        var dual =
            "STYLE LOCK: photoreal gothic\n\n" +
            "action\n\n" +
            "PERFORMANCE LOCK: look at each other\n" +
            "- [performance] PERFORMANCE LOCK: leftover house rule\n" +
            "- Performance: facial expression and gaze match the beat and project PERFORMANCE rules.\n";
        var one = PerformanceTagWriter.EnsureSinglePerformanceLock(dual, lockText);
        Assert.Equal(1, PerformanceTagWriter.CountPerformanceLocks(one + "\n"));
        Assert.Contains("confessional", one, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("look at each other", one, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("facial expression and gaze", one, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- [performance]", one, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("STYLE LOCK:", one, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSinglePerformanceLock_does_not_invent_when_lock_missing()
    {
        var leftover =
            "STYLE LOCK: photoreal gothic\n\naction\n\nPERFORMANCE LOCK: leftover\n";
        var stripped = PerformanceTagWriter.EnsureSinglePerformanceLock(leftover, performanceLock: null);
        Assert.DoesNotContain("PERFORMANCE LOCK", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STYLE LOCK:", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("action", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildVisualPrompt_confessional_lock_does_not_emit_face_the_house()
    {
        var beat = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["visual_event"] = "Character faces the window. Face to house.",
            ["dialogue"] = "I will not turn around.",
            ["speaker"] = "Character_Narrator",
            ["delivery"] = "spoken_on_camera",
            ["primary_subject"] = "Character_Narrator",
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
        };
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. ROOM - DAY",
            ["story_beats"] = new List<object?> { beat },
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
        };
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Narrator"] = new Dictionary<string, object?>
            {
                ["display_name_policy"] = "ok_anytime",
                ["canonical_given_name"] = "Narrator",
            },
        };
        var vp = Stage2PlannerService.BuildVisualPrompt(beat, scene, seeds, new Dictionary<string, List<string>>());
        var action = PerformanceTagWriter.ReadActionTag(vp);
        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.Contains("faces the window", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("face to house", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("face the house", action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_address_welded_into_a_sentence_stays_rather_than_break_the_beat()
    {
        // Removing the phrase in place left "Nick and lifts the lantern" / "She before turning
        // away". Performance owns the tag; the beat keeps its grammar.
        Assert.Equal(
            "Nick looks into the camera and lifts the lantern",
            PerformanceTagWriter.StripEyelineFromAction("Nick looks into the camera and lifts the lantern."));

        Assert.Equal(
            "She addresses the audience before turning away",
            PerformanceTagWriter.StripEyelineFromAction("She addresses the audience before turning away."));

        Assert.Equal(
            "He gazes at the lens, then steps back from the bed",
            PerformanceTagWriter.StripEyelineFromAction("He gazes at the lens, then steps back from the bed."));
    }

    [Fact]
    public void An_address_standing_on_its_own_still_goes()
    {
        Assert.Equal(
            "He sets the lantern down",
            PerformanceTagWriter.StripEyelineFromAction("Confessional address. He sets the lantern down."));

        Assert.Equal(
            "He sets the lantern down",
            PerformanceTagWriter.StripEyelineFromAction("He sets the lantern down. Looks into the lens."));
    }
}
