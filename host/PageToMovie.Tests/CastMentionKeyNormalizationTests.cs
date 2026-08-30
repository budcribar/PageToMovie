using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// One spelling per person in a generate/extend prompt: the catalog key (Character_*).
/// Display names (Mary / MARY / The Lamb) normalize to that key. C-indices exist only
/// after CompressPromptText when the prompt is over the char cap, and then Mary and
/// Character_Mary become the same C-index.
/// </summary>
public class CastMentionKeyNormalizationTests
{
    private static readonly Dictionary<string, object?> NoSeeds = new();

    private static string Normalize(string action, params string[] cast) =>
        Stage2PlannerService.NormalizeCastMentionsToKeys(action, cast, NoSeeds);

    [Fact]
    public void Caps_mentions_become_keys()
    {
        var outp = Normalize(
            "THE CHILDREN twist in their seats and point at THE LAMB.",
            "Character_The_Children", "Character_The_Lamb");

        Assert.Equal(
            "Character_The_Children twist in their seats and point at Character_The_Lamb.", outp);
    }

    [Fact]
    public void Title_case_mentions_become_the_same_key()
    {
        var outp = Normalize(
            "Mary walks the lane. The Lamb follows at her heel.",
            "Character_Mary", "Character_The_Lamb");

        Assert.Equal(
            "Character_Mary walks the lane. Character_The_Lamb follows at her heel.", outp);
    }

    /// <summary>Lowercase use is generic prose, not a character cue.</summary>
    [Fact]
    public void Lowercase_nouns_are_left_alone()
    {
        var outp = Normalize(
            "THE LAMB trots in. It is lovely to see a lamb at school.", "Character_The_Lamb");

        Assert.StartsWith("Character_The_Lamb trots in.", outp);
        Assert.Contains("to see a lamb at school", outp, StringComparison.Ordinal);
    }

    /// <summary>Longest form first, or "THE OLD MAN" gets half-eaten by a shorter candidate.</summary>
    [Fact]
    public void Longer_names_win_over_their_own_fragments()
    {
        var outp = Normalize("THE OLD MAN stirs.", "Character_The_Old_Man", "Character_Man");
        Assert.Equal("Character_The_Old_Man stirs.", outp);
    }

    /// <summary>A key can carry an article the prose leaves off.</summary>
    [Fact]
    public void Article_stripped_form_also_matches()
    {
        Assert.Equal("Character_The_Lamb bleats.", Normalize("LAMB bleats.", "Character_The_Lamb"));
        Assert.Equal("Character_The_Lamb bleats.", Normalize("Lamb bleats.", "Character_The_Lamb"));
    }

    [Fact]
    public void Running_twice_changes_nothing()
    {
        var once = Normalize("MARY walks.", "Character_Mary");
        var twice = Normalize(once, "Character_Mary");
        Assert.Equal("Character_Mary walks.", once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Quoted_dialogue_is_not_rewritten()
    {
        var outp = Normalize(
            "MARY says \"Mary, come here\" to THE LAMB.",
            "Character_Mary", "Character_The_Lamb");

        Assert.Equal(
            "Character_Mary says \"Mary, come here\" to Character_The_Lamb.", outp);
    }

    [Fact]
    public void No_cast_or_no_text_is_a_no_op()
    {
        Assert.Equal("MARY walks.", Normalize("MARY walks."));
        Assert.Equal("", Normalize("", "Character_Mary"));
    }

    /// <summary>
    /// Gen-time sanitize also normalizes, so leftover screenplay caps / title-case on an
    /// older plan do not trigger the "{key} is on screen." second spelling.
    /// </summary>
    [Theory]
    [InlineData("MARY walks the lane.")]
    [InlineData("Mary walks the lane.")]
    public void Sanitize_normalizes_display_names_and_does_not_append_on_screen(string action)
    {
        var keys = new[] { "Character_Mary" };
        var clean = ClipVideoPromptBuilder.SanitizeActionText(action, keys);
        Assert.StartsWith("Character_Mary walks the lane.", clean);
        Assert.DoesNotContain("is on screen.", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("Mary", clean.Replace("Character_Mary", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AttachPrimaryToVisual_injects_the_key_not_a_display_name()
    {
        var result = Stage2PlannerService.AttachPrimaryToVisual(
            "He steadies his hands on his knees.", "Character_Narrator", "Narrator");
        Assert.Equal("Character_Narrator steadies his hands on his knees.", result);
        Assert.DoesNotContain("Narrator steadies", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVisualPrompt_has_one_spelling_and_no_C_index()
    {
        var prompt = Stage2PlannerService.BuildVisualPrompt(
            new Dictionary<string, object?>
            {
                ["visual_event"] = "Mary walks. THE LAMB follows.",
                ["primary_subject"] = "Character_Mary",
            },
            new Dictionary<string, object?>
            {
                ["setting"] = "EXT. LANE - DAY",
                ["characters_on_screen"] = new List<object?> { "Character_Mary", "Character_The_Lamb" },
            },
            new Dictionary<string, object?>
            {
                ["Character_Mary"] = new Dictionary<string, object?> { ["canonical_given_name"] = "Mary" },
                ["Character_The_Lamb"] = new Dictionary<string, object?> { ["canonical_given_name"] = "The Lamb" },
            },
            new Dictionary<string, List<string>>());

        Assert.Contains("Character_Mary walks", prompt, StringComparison.Ordinal);
        Assert.Contains("Character_The_Lamb follows", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MARY", prompt, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bC\d+\b", prompt);
        Assert.DoesNotMatch(@"(?<![A-Za-z_])Mary(?![A-Za-z_])", prompt);
        Assert.DoesNotMatch(@"(?<![A-Za-z_])The Lamb(?![A-Za-z_])", prompt);
    }
}
