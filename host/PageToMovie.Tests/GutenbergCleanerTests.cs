using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class GutenbergCleanerTests
{
    [Fact]
    public void StripHeaderAndFooter_StripsGutenbergHeaderAndFooter_WhenPresent()
    {
        var rawText = """
            The Project Gutenberg EBook of The Tell-Tale Heart, by Edgar Allan Poe

            This eBook is for the use of anyone anywhere at no cost...

            Title: The Tell-Tale Heart
            Author: Edgar Allan Poe

            *** START OF THIS PROJECT GUTENBERG EBOOK THE TELL-TALE HEART ***

            TRUE!—nervous—very, very dreadfully nervous I had been and am;
            but why will you say that I am mad?

            *** END OF THIS PROJECT GUTENBERG EBOOK THE TELL-TALE HEART ***

            End of Project Gutenberg's The Tell-Tale Heart, by Edgar Allan Poe
            *** END OF THIS PROJECT GUTENBERG EBOOK ***
            """;

        var cleaned = GutenbergCleaner.StripHeaderAndFooter(rawText);

        Assert.Contains("TRUE!—nervous—very, very dreadfully nervous", cleaned);
        Assert.DoesNotContain("The Project Gutenberg EBook of The Tell-Tale Heart", cleaned);
        Assert.DoesNotContain("START OF THIS PROJECT GUTENBERG EBOOK", cleaned);
        Assert.DoesNotContain("END OF THIS PROJECT GUTENBERG EBOOK", cleaned);
        Assert.DoesNotContain("End of Project Gutenberg's", cleaned);
    }

    [Fact]
    public void StripHeaderAndFooter_LeavesCleanTextIntact_WhenNoHeaderPresent()
    {
        var rawText = """
            TRUE!—nervous—very, very dreadfully nervous I had been and am;
            but why will you say that I am mad?
            """;

        var cleaned = GutenbergCleaner.StripHeaderAndFooter(rawText);

        Assert.Equal(rawText.Trim(), cleaned);
    }

    [Fact]
    public void HasGutenbergHeader_DetectsGutenbergPreamble()
    {
        var textWithHeader = "The Project Gutenberg EBook of Dracula\n*** START OF THE PROJECT GUTENBERG EBOOK DRACULA ***\nChapter 1";
        var textWithoutHeader = "Chapter 1\nJonathan Harker's Journal";

        Assert.True(GutenbergCleaner.HasGutenbergHeader(textWithHeader));
        Assert.False(GutenbergCleaner.HasGutenbergHeader(textWithoutHeader));
    }

    [Fact]
    public void StripHeaderAndFooter_HandlesOdysseyStyleStartMarker()
    {
        var raw = """
            The Project Gutenberg eBook of The Odyssey

            This eBook is for the use of anyone anywhere in the United States and
            most other parts of the world at no cost and with almost no restrictions
            whatsoever.

            Title: The Odyssey
            Author: Homer

            *** START OF THE PROJECT GUTENBERG EBOOK THE ODYSSEY ***

            The Odyssey

            by Homer

            BOOK I

            Tell me, O Muse, of that ingenious hero

            *** END OF THE PROJECT GUTENBERG EBOOK THE ODYSSEY ***
            """;

        var cleaned = GutenbergCleaner.StripHeaderAndFooter(raw);

        Assert.Contains("BOOK I", cleaned);
        Assert.Contains("Tell me, O Muse", cleaned);
        Assert.DoesNotContain("Project Gutenberg eBook", cleaned);
        Assert.DoesNotContain("START OF THE PROJECT GUTENBERG", cleaned);
        Assert.DoesNotContain("END OF THE PROJECT GUTENBERG", cleaned);
    }
}