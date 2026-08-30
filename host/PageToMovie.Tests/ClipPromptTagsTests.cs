using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The tag format is ours, so it is scanned. These pin the behaviour every caller used to
/// re-implement with a <c>&lt;Tag&gt;.*?&lt;/Tag&gt;</c> pattern of its own.
/// </summary>
public class ClipPromptTagsTests
{
    [Fact]
    public void Reads_the_first_block_and_its_value()
    {
        const string text = "<Camera>Medium shot</Camera> <Action>Mary walks</Action>";
        Assert.Equal("Medium shot", ClipPromptTags.ReadFirstInner(text, "Camera"));
        Assert.Equal("<Camera>Medium shot</Camera>", ClipPromptTags.ReadFirstBlock(text, "Camera"));
        Assert.Null(ClipPromptTags.ReadFirstInner(text, "Lighting"));
    }

    [Fact]
    public void A_block_ends_at_its_own_closing_tag_not_a_later_one()
    {
        const string text = "<Action>first</Action> middle <Action>second</Action>";
        Assert.Equal("first", ClipPromptTags.ReadFirstInner(text, "Action"));
        Assert.Equal(2, ClipPromptTags.Find(text, "Action").Count);
    }

    [Fact]
    public void An_unclosed_tag_claims_nothing()
    {
        Assert.Empty(ClipPromptTags.Find("<Action>never closed", "Action"));
        Assert.Equal("<Action>never closed", ClipPromptTags.Remove("<Action>never closed", "Action"));
    }

    [Fact]
    public void Removing_a_block_takes_the_gap_it_leaves()
    {
        Assert.Equal(
            "<Action>Mary walks</Action>",
            ClipPromptTags.Remove("<Cast>Character_Mary</Cast> <Action>Mary walks</Action>", "Cast"));
    }

    [Fact]
    public void Duplicate_blocks_go_by_value_and_the_first_one_stays()
    {
        // Two different lightings are a contradiction to report, not a duplicate to collapse.
        const string same = "<Lighting>candle</Lighting> x <Lighting>candle</Lighting>";
        // The removal takes the whitespace that trailed the copy; what preceded it stays.
        Assert.Equal("<Lighting>candle</Lighting> x ", ClipPromptTags.DropDuplicateBlocks(same, "Lighting"));

        const string different = "<Lighting>candle</Lighting> x <Lighting>daylight</Lighting>";
        Assert.Equal(different, ClipPromptTags.DropDuplicateBlocks(different, "Lighting"));
    }

    [Fact]
    public void Rewriting_a_block_keeps_its_tags_and_everything_around_it()
    {
        Assert.Equal(
            "before <Action>MARY WALKS</Action> after",
            ClipPromptTags.RewriteBlocks("before <Action>Mary walks</Action> after", "Action", v => v.ToUpperInvariant()));
    }
}
