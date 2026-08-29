using System.Text.Json;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Project visual medium is the generate-time SSoT: location plates, plan fallback,
/// fresh overwrite, and extend hops must all bind from <see cref="VisualMediumStyles"/>.
/// </summary>
public class VisualMediumLockTests
{
    [Fact]
    public void Location_plate_prompt_uses_illustrated_medium_not_photoreal()
    {
        var prompt = LocationDesignService.BuildGeneratePrompt(
            "Loc_Schoolhouse",
            "A clapboard schoolhouse at the edge of a meadow.",
            "flat watercolor washes",
            seedFromExisting: false,
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.Contains("illustrated", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watercolor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Photoreal live-action.", prompt, StringComparison.Ordinal);
        Assert.Contains(VisualMediumStyles.NegativeFor(VisualMediumStyles.MediumIllustrated), prompt);
        Assert.Contains(VisualMediumStyles.StyleLockFor(VisualMediumStyles.MediumIllustrated), prompt);
    }

    [Fact]
    public void Location_plate_prompt_uses_photoreal_when_project_is_photoreal()
    {
        var prompt = LocationDesignService.BuildGeneratePrompt(
            "Loc_Street",
            "A wet cobblestone street at night.",
            "",
            seedFromExisting: false,
            visualMedium: VisualMediumStyles.MediumPhotoreal);

        Assert.Contains("Photoreal", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(VisualMediumStyles.NegativeFor(VisualMediumStyles.MediumPhotoreal), prompt);
        Assert.DoesNotContain("Picture-book / illustrated", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCastStyleLock_empty_style_uses_StyleLockFor_not_hardcoded_3d()
    {
        var cast = new List<string> { "Character_Hero" };
        var seeds = new Dictionary<string, object?>
        {
            ["Character_Hero"] = new Dictionary<string, object?> { ["display_name_policy"] = "ok_anytime" },
        };

        var illustrated = Stage2PlannerService.EnsureCastStyleLock(
            "", VisualMediumStyles.MediumIllustrated, cast, seeds);
        Assert.Equal(VisualMediumStyles.IllustratedStyleLock, illustrated);
        Assert.DoesNotContain("stylized 3D animated children's picture-book CG", illustrated);

        var photoreal = Stage2PlannerService.EnsureCastStyleLock(
            "", VisualMediumStyles.MediumPhotoreal, cast, seeds);
        Assert.Equal(VisualMediumStyles.PhotorealStyleLock, photoreal);

        var existing = Stage2PlannerService.EnsureCastStyleLock(
            "STYLE LOCK: already set", VisualMediumStyles.MediumIllustrated, cast, seeds);
        Assert.Equal("STYLE LOCK: already set", existing);
    }

    [Fact]
    public void Fresh_gen_overwrites_disagreeing_plan_StyleLock()
    {
        var clip = ClipWithStyle(
            "<StyleLock>stylized 3D animated children's picture-book CG -- not photoreal</StyleLock> " +
            "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
            "<Action>Character_Mary walks in.</Action>");

        var built = ClipVideoPromptBuilder.Build(
            clip,
            Path.GetTempPath(),
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.Equal("fresh", built.Mode);
        Assert.Contains(VisualMediumStyles.IllustratedStyleLock, built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stylized 3D animated children's picture-book CG", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("photoreal", built.Prompt, StringComparison.OrdinalIgnoreCase); // medium negative
        Assert.Contains("3D render", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extend_keeps_project_medium_and_does_not_ban_illustration()
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt =
                "<StyleLock>stylized 3D animated CG</StyleLock> " +
                "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
                "<Action>Character_Mary turns.</Action>",
            characters_on_screen = new[] { "Character_Mary" },
            veo_continuation_source = "extend_previous",
            audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
        })).RootElement.Clone();

        var tmp = Path.Combine(Path.GetTempPath(), "fs-medium-extend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var prev = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prev, new byte[2048]);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new() { Key = "Character_Mary", DisplayName = "Mary" },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            characters: profiles,
            previousClipVideoPath: prev,
            visualMedium: VisualMediumStyles.MediumIllustrated);

        Assert.Equal("video-extend", built.Mode);
        Assert.Contains(VisualMediumStyles.IllustratedStyleLock, built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Same art medium and renderer", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do not drift to illustration, anime, cartoon", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not drift to photoreal", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Movie_review_prompts_require_scene_clip_cites_for_style()
    {
        var chunk = MovieAutoReviewService.BuildSceneChunkPrompt("Scenes 1-4");
        Assert.Contains("SxxCyy", chunk, StringComparison.Ordinal);
        Assert.Contains("evidence", chunk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S03C01", chunk, StringComparison.Ordinal);
        Assert.Contains("Sxx (scene) or SxxCyy", chunk, StringComparison.Ordinal);

        var exec = MovieAutoReviewService.BuildExecutiveSynthesisSystemPrompt();
        Assert.Contains("S03C01", exec, StringComparison.Ordinal);
        Assert.DoesNotContain("Do NOT list or repeat each scene", exec, StringComparison.Ordinal);
        Assert.Contains("style", exec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Movie_review_flags_style_jump_even_when_group_score_is_high()
    {
        var groups = new List<MovieSceneGroupFeedback>
        {
            new()
            {
                SceneRange = "Scenes 1-4",
                SceneNumbers = { 1, 2, 3, 4 },
                Score = 8,
                ContinuityNotes =
                    "Spatial direction flows logically, though background art transitions abruptly from 2D watercolor to 3D rendered realism.",
                Evidence =
                {
                    new MovieReviewEvidence { Ref = "S03C01", Claim = "background jumps from watercolor to 3D" },
                },
            },
        };

        Assert.True(MovieAutoReviewService.HasStyleMediumIssue(groups[0]));
        var flagged = MovieAutoReviewService.CollectFlaggedScenes(groups);
        Assert.Contains(3, flagged);
        Assert.Equal("S03C01", MovieAutoReviewService.FormatKeyframeLabel(3, 1));
    }

    [Fact]
    public void ParseSceneGroupFeedback_reads_evidence_array()
    {
        var raw = """
            {"overallScore":8,"continuityScore":8,"characterScore":8,"lightingScore":8,"pacingScore":8,"dialogueScore":8,"musicScore":8,
             "continuityNotes":"ok","visualConsistencyNotes":"ok","lightingNotes":"ok","dialogueNotes":"ok","audioNotes":"ok",
             "evidence":[{"ref":"S03C01","claim":"watercolor to 3D"}]}
            """;
        var parsed = MovieAutoReviewService.ParseSceneGroupFeedback(raw, "Scenes 1-4", new[] { 1, 2, 3, 4 });
        Assert.Empty(parsed.Issues);
        Assert.NotNull(parsed.Value);
        var ev = Assert.Single(parsed.Value!.Evidence);
        Assert.Equal("S03C01", ev.Ref);
        Assert.Contains("watercolor", ev.Claim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FitPromptToVideoBudget_does_not_shorten_style_lock_before_sacrificial_tags()
    {
        var style = $"<{PromptFieldTags.StyleLock}>{VisualMediumStyles.IllustratedStyleLock}</{PromptFieldTags.StyleLock}>";
        var optics = $"<{PromptFieldTags.Optics}>" + new string('o', 200) + $"</{PromptFieldTags.Optics}>";
        var core = style + " CHARACTER VARIABLES Character_Hero action " + optics;
        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(core + new string('x', 50), hardCapChars: core.Length - 80);

        Assert.Contains("picture-book", fitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"<{PromptFieldTags.Optics}>", fitted, StringComparison.Ordinal);
    }

    private static JsonElement ClipWithStyle(string visual) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 1,
            visual_prompt = visual,
            characters_on_screen = new[] { "Character_Mary" },
            veo_continuation_source = "none",
            audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
        })).RootElement.Clone();
}
