using System.Text.Json;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Generate/extend prompts have one vision_meta PERFORMANCE LOCK and one beat
/// <c>&lt;Performance&gt;</c>. Action does not restage gaze. Missing lock is not invented.
/// </summary>
public sealed class PerformanceLockPromptTests
{
    private const string ConfessionalLock =
        "PERFORMANCE LOCK: first-person confessional; when the speaker is on camera they address an implied listener / look down the lens.";

    private const string ObjectiveLock =
        "PERFORMANCE LOCK: objective; characters look at each other / scene action, not the viewer.";

    [Fact]
    public void Build_injects_one_vision_meta_lock_and_strips_face_the_house()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-perf-conf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "source"));
        File.WriteAllText(
            Path.Combine(tmp, "source", "vision_meta.json"),
            JsonSerializer.Serialize(new
            {
                schema_version = "vision_meta.v1",
                visual_medium = VisualMediumStyles.MediumPhotoreal,
                render_style_lock = "STYLE LOCK: photoreal gothic",
                performance_lock = ConfessionalLock,
                decided_by = "adaptation",
            }));

        try
        {
            var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                clip_number = 1,
                visual_prompt =
                    "<Action>Character_Narrator stands at the sill, face to house.</Action> " +
                    "<Performance>Acting intensity 6/10: Haunted stare</Performance>",
                characters_on_screen = new[] { "Character_Narrator" },
                audio_payload = new
                {
                    speaker = "Character_Narrator",
                    dialogue = "True — nervous — very dreadfully nervous.",
                    delivery = "spoken_on_camera",
                },
            })).RootElement.Clone();

            var built = ClipVideoPromptBuilder.Build(clip, tmp, visualMedium: VisualMediumStyles.MediumPhotoreal);

            Assert.Equal(1, PerformanceTagWriter.CountPerformanceLocks(built.Prompt + "\n"));
            Assert.Contains("confessional", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"<{PromptFieldTags.Performance}>", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("face to house", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("face the house", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("facial expression and gaze", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("- Performance:", built.Prompt, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Build_objective_lock_does_not_emit_lens_address_in_action()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-perf-obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "source"));
        File.WriteAllText(
            Path.Combine(tmp, "source", "vision_meta.json"),
            JsonSerializer.Serialize(new
            {
                schema_version = "vision_meta.v1",
                visual_medium = VisualMediumStyles.MediumIllustrated,
                render_style_lock = VisualMediumStyles.IllustratedStyleLock,
                performance_lock = ObjectiveLock,
                decided_by = "adaptation",
            }));

        try
        {
            var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                clip_number = 1,
                visual_prompt =
                    "<Action>Character_Hero sits at the desk. Look down the lens. Address the viewer.</Action> " +
                    "<Performance>Acting intensity 4/10: Softened sincere eyes</Performance>",
                characters_on_screen = new[] { "Character_Hero" },
                audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
            })).RootElement.Clone();

            var built = ClipVideoPromptBuilder.Build(clip, tmp, visualMedium: VisualMediumStyles.MediumIllustrated);

            Assert.Equal(1, PerformanceTagWriter.CountPerformanceLocks(built.Prompt + "\n"));
            Assert.Contains("objective", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("look down the lens", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("address the viewer", built.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sits at the desk", built.Prompt, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Build_does_not_invent_a_lock_when_vision_meta_has_none()
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 1,
            visual_prompt = "INT. ROOM - DAY. Character_Hero stands.",
            characters_on_screen = new[] { "Character_Hero" },
            veo_continuation_source = "none",
            audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
        })).RootElement.Clone();

        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath());
        Assert.DoesNotContain("PERFORMANCE LOCK", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("facial expression and gaze", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(built.PerformanceLock));
    }

    [Fact]
    public void House_Performance_bullet_is_absent_from_retired_clip_gen_rules()
    {
        var rules = ClipVideoPromptBuilder.TryLoadClipGenRules();
        Assert.False(string.IsNullOrWhiteSpace(rules));
        Assert.DoesNotContain("facial expression and gaze", rules!, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(ClipVideoPromptBuilder.PromptBodyFromClipGenRules(rules)));
    }
}
