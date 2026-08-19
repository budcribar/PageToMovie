using System.Text.Json;
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
             "visual_prompt":"EXT. SCHOOLHOUSE - DAY. also on screen: Character_Mary, Character_Narrator. THE LAMB waits. Character_Narrator still wears wool jacket, felt hat <Lighting>x</Lighting>"}
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
}
