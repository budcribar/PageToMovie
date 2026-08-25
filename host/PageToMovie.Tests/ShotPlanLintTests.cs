using System.Text.Json;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>One generic lint for "plan text contradicts cast facts" — findings are surfaced (clip stale
/// reason / job log), never patched at gen time. Rule 1: a voice-only role on screen or dressed.</summary>
public class ShotPlanLintTests
{
    [Fact]
    public void Voice_only_role_on_screen_or_dressed_is_a_finding()
    {
        using var doc = JsonDocument.Parse("""
            {"clip_number":2,"characters_on_screen":["Character_Mary","Character_Narrator"],
             "visual_prompt":"<Setting>EXT. SCHOOLHOUSE - DAY</Setting> <Cast>Character_Mary, Character_Narrator</Cast> <Action>THE LAMB waits</Action> <Wardrobe>Character_Narrator still wears wool jacket, felt hat</Wardrobe> <Lighting>x</Lighting>"}
            """);
        var findings = ShotPlanLint.Check(doc.RootElement, new[] { "Character_Narrator" });
        var f = Assert.Single(findings);
        Assert.Equal("voice_only_on_screen", f.Rule);
        Assert.Contains("lists it on screen and dresses it", f.Message);
    }

    [Fact]
    public void Voice_only_speaker_in_characters_on_screen_alone_is_not_a_finding()
    {
        using var doc = JsonDocument.Parse("""
            {"clip_number":1,"characters_on_screen":["Character_Mary","Character_Narrator"],
             "visual_prompt":"MARY walks. OFF-CAMERA VOICEOVER Character_Narrator says \"x\" Character_Mary is on screen."}
            """);
        Assert.Empty(ShotPlanLint.Check(doc.RootElement, new[] { "Character_Narrator" }));
    }

    [Fact]
    public void Clean_plan_has_no_findings()
    {
        using var doc = JsonDocument.Parse("""
            {"clip_number":1,"characters_on_screen":["Character_Mary"],
             "visual_prompt":"MARY walks. OFF-CAMERA VOICEOVER Character_Narrator says \"Mary had a little lamb.\" Character_Mary is on screen."}
            """);
        Assert.Empty(ShotPlanLint.Check(doc.RootElement, new[] { "Character_Narrator" }));
    }

    private const string PlannedStyle =
        "stylized 3D animated children's picture-book CG (same render family as animal hero)";

    private static JsonDocument ClipWithStyle(string styleLock) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt =
                $"<{PromptFieldTags.StyleLock}>{styleLock}</{PromptFieldTags.StyleLock}> " +
                "<Setting>INT. SCHOOLROOM - DAY</Setting> <Action>The children point</Action>",
        }));

    /// <summary>
    /// Changing the style after the shot plan is built leaves the old lock inside every clip, so
    /// the model gets two mediums at once — Mary19 shipped watercolor alongside 3D CG in 19 clips.
    /// </summary>
    [Fact]
    public void Style_lock_that_disagrees_with_the_project_is_a_finding()
    {
        using var doc = ClipWithStyle(PlannedStyle);
        var findings = ShotPlanLint.Check(
            doc.RootElement,
            Array.Empty<string>(),
            "STYLE LOCK: illustrated picture-book; painted nursery-rhyme world with flat watercolor washes");
        var f = Assert.Single(findings);
        Assert.Equal("style_lock_drift", f.Rule);
        Assert.Contains("rebuild the shot plan", f.Message);
    }

    [Fact]
    public void Matching_style_lock_is_not_a_finding()
    {
        using var doc = ClipWithStyle(PlannedStyle);
        Assert.Empty(ShotPlanLint.Check(doc.RootElement, Array.Empty<string>(), $"STYLE LOCK: {PlannedStyle}"));
    }

    /// <summary>
    /// Compared on the leading clause, where the medium is named. Reworded descriptive tails must
    /// not fire on every clip forever, and a bare style head (no "STYLE LOCK:" prefix) still works.
    /// </summary>
    [Fact]
    public void Reworded_tail_and_unprefixed_head_still_agree()
    {
        using var doc = ClipWithStyle(PlannedStyle);
        Assert.Empty(ShotPlanLint.Check(
            doc.RootElement, Array.Empty<string>(),
            $"STYLE LOCK: {PlannedStyle}, soft edges, muted palette, gentle grain"));

        using var bare = ClipWithStyle(PlannedStyle);
        Assert.Empty(ShotPlanLint.Check(bare.RootElement, Array.Empty<string>(), PlannedStyle));
    }

    [Fact]
    public void No_style_head_means_no_style_finding()
    {
        using var doc = ClipWithStyle(PlannedStyle);
        Assert.Empty(ShotPlanLint.Check(doc.RootElement, Array.Empty<string>()));
        Assert.Empty(ShotPlanLint.Check(doc.RootElement, Array.Empty<string>(), "   "));
    }

    /// <summary>
    /// A plan built before Stage 2 tagged its fields carries no <c>&lt;StyleLock&gt;</c>, so drift
    /// is simply not reported — rebuilding it is the fix for drift anyway. There is no prose
    /// fallback to guess the old layout back.
    /// </summary>
    [Fact]
    public void Untagged_legacy_plan_reports_no_style_drift()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt = "STYLE LOCK: stylized 3D CG. INT. SCHOOLROOM - DAY. The children point.",
        }));
        Assert.Empty(ShotPlanLint.Check(
            doc.RootElement, Array.Empty<string>(), "STYLE LOCK: illustrated watercolor picture-book"));
    }
}
