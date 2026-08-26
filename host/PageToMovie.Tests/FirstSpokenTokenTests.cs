using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The opening cue names the line's first word so the model cannot swallow it. Finding that word
/// used to be anchored at position 0, so a line opening with punctuation matched nothing and the
/// cue quietly degraded to a version that names no word at all.
/// </summary>
/// <remarks>
/// That matters because leading punctuation is deliberate. An ellipsis or em dash at the head of a
/// line is how you ask for a beat of hesitation before the first word — and pairing it with a cue
/// that names no word, or names the punctuation, tells the model two different things at once and
/// makes the experiment unreadable.
/// </remarks>
public sealed class FirstSpokenTokenTests
{
    [Theory]
    [InlineData("It made the children laugh.", "It")]
    [InlineData("And everywhere that Mary went", "And")]
    [InlineData("True!-nervous-very", "True!")]
    [InlineData("Who's there?", "Who's")]
    public void The_plain_first_word_is_unchanged(string line, string expected) =>
        Assert.Equal(expected, ClipVideoPromptBuilder.FirstSpokenToken(line));

    /// <summary>The regression: a hesitation beat before the line must not hide the word.</summary>
    [Theory]
    [InlineData("… It made the children laugh.")]
    [InlineData("...It made the children laugh.")]
    [InlineData("— It made the children laugh.")]
    [InlineData("-- It made the children laugh.")]
    [InlineData("\"It made the children laugh.\"")]
    [InlineData("[PAUSE] It made the children laugh.")]
    public void A_line_opening_with_punctuation_still_finds_its_first_word(string line) =>
        Assert.Equal("It", ClipVideoPromptBuilder.FirstSpokenToken(line));

    [Fact]
    public void A_line_with_no_word_at_all_still_yields_nothing()
    {
        Assert.Equal("", ClipVideoPromptBuilder.FirstSpokenToken("…"));
        Assert.Equal("", ClipVideoPromptBuilder.FirstSpokenToken("   "));
        Assert.Equal("", ClipVideoPromptBuilder.FirstSpokenToken(null));
    }
}
