using System.Text.Json;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// An on-screen role the shot plan names but the cast does not list: an extra (silent, one clip)
/// renders from description; a role that speaks or recurs must be cast so it can be locked —
/// text-only would give it a different face every clip.
/// </summary>
public class UncastOnScreenPolicyTests
{
    private static JsonElement Plan(params (int Scene, string[][] ClipsOnScreen)[] scenes)
    {
        var doc = new
        {
            scenes = scenes.Select(s => new
            {
                scene_number = s.Scene,
                veo_clips = s.ClipsOnScreen.Select((cos, i) => new { clip_number = i + 1, characters_on_screen = cos }).ToArray(),
            }).ToArray(),
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(doc)).RootElement.Clone();
    }

    private static JsonElement Clip(string[] onScreen, string? speaker = null)
    {
        var o = new Dictionary<string, object?> { ["characters_on_screen"] = onScreen };
        if (speaker is not null) o["audio_payload"] = new { speaker, dialogue = "..." };
        return JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();
    }

    [Fact]
    public void Silent_extra_in_one_clip_renders_from_description()
    {
        var plan = Plan((1, new[] { new[] { "Character_Narrator" }, new[] { "Character_Narrator", "Character_Officer" } }),
                        (2, new[] { new[] { "Character_Old_Man" } }));
        var d = UncastOnScreenPolicy.Decide("Character_Officer", Clip(new[] { "Character_Narrator", "Character_Officer" }), plan);
        Assert.True(d.TextOnly);
        Assert.Equal(1, d.ClipAppearances);
        Assert.False(d.SpeaksInClip);
    }

    [Fact]
    public void Recurring_silent_role_must_be_cast()
    {
        var plan = Plan((1, new[] { new[] { "Character_Officer" }, new[] { "Character_Narrator" } }),
                        (3, new[] { new[] { "Character_Officer", "Character_Narrator" } }));
        var d = UncastOnScreenPolicy.Decide("Character_Officer", Clip(new[] { "Character_Officer" }), plan);
        Assert.Equal(UncastOnScreenPolicy.Verdict.MustBeCast, d.Verdict);
        Assert.Equal(2, d.ClipAppearances);
    }

    [Fact]
    public void Speaking_role_must_be_cast_even_if_it_appears_once()
    {
        var plan = Plan((1, new[] { new[] { "Character_Officer" } }));
        var d = UncastOnScreenPolicy.Decide("Character_Officer", Clip(new[] { "Character_Officer" }, speaker: "Character_Officer"), plan);
        Assert.Equal(UncastOnScreenPolicy.Verdict.MustBeCast, d.Verdict);
        Assert.True(d.SpeaksInClip);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_missing_plan_counts_this_clip_only()
    {
        var d = UncastOnScreenPolicy.Decide("character_officer", Clip(new[] { "Character_Officer" }), blueprintRoot: null);
        Assert.True(d.TextOnly);
        Assert.Equal(1, d.ClipAppearances);
        Assert.Equal(0, UncastOnScreenPolicy.CountClipsOnScreen(JsonDocument.Parse("{}").RootElement, "x"));
    }
}
