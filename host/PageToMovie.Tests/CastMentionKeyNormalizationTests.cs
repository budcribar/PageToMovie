using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Action prose named cast in screenplay caps while every structured block in the same prompt used
/// Character_* keys (aliased to C1/C2 later) — two naming schemes for one person, with nothing
/// linking them. It also made SanitizeActionText append "{key} is on screen." to every clip,
/// because Contains(key) can never match a caps mention.
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
    public void No_cast_or_no_text_is_a_no_op()
    {
        Assert.Equal("MARY walks.", Normalize("MARY walks."));
        Assert.Equal("", Normalize("", "Character_Mary"));
    }

    /// <summary>
    /// The payoff: with keys in the action, SanitizeActionText's on-screen fallback stops firing.
    /// Every real Mary19 prompt carried a redundant "C1 is on screen." because of this.
    /// </summary>
    [Fact]
    public void Normalized_action_no_longer_triggers_the_on_screen_append()
    {
        var keys = new[] { "Character_Mary" };
        var caps = ClipVideoPromptBuilder.SanitizeActionText("MARY walks the lane.", keys);
        Assert.Contains("Character_Mary is on screen.", caps, StringComparison.Ordinal);

        var normalized = ClipVideoPromptBuilder.SanitizeActionText(
            Normalize("MARY walks the lane.", "Character_Mary"), keys);
        Assert.DoesNotContain("is on screen.", normalized, StringComparison.Ordinal);
    }
}
