using PageToMovie.ScreenplayEditor.Components;
using PageToMovie.ScreenplayEditor.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The screenplay editor beat row is a single-line input. Browsers strip newlines from a text
/// input's value, so multi-line Fountain dialogue ("…laugh and play" / "to see a lamb at school.")
/// rendered as "playto see" and a save wrote that back. The input binds through a proxy that
/// shows line breaks as spaces while the model keeps them.
/// </summary>
public class ScreenplayBeatEditorInputTests
{
    [Fact]
    public void FlattenLineBreaks_turns_verse_line_breaks_into_single_spaces()
    {
        Assert.Equal(
            "It made the children laugh and play to see a lamb at school.",
            ScreenplayEditor_BeatEditor.FlattenLineBreaks(
                "It made the children laugh and play\nto see a lamb at school."));

        Assert.Equal("a b c", ScreenplayEditor_BeatEditor.FlattenLineBreaks("a\r\n  b \n\n\tc\n"));
    }

    [Fact]
    public void FlattenLineBreaks_leaves_single_line_text_untouched()
    {
        Assert.Equal("Hello there.", ScreenplayEditor_BeatEditor.FlattenLineBreaks("Hello there."));
        Assert.Equal("", ScreenplayEditor_BeatEditor.FlattenLineBreaks(""));
        Assert.Equal("", ScreenplayEditor_BeatEditor.FlattenLineBreaks(null));
    }

    [Fact]
    public void Input_proxy_reads_flattened_but_model_keeps_line_breaks_until_edited()
    {
        var beat = new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            SpokenText = "laugh and play\nto see a lamb at school.",
            Parenthetical = "softly,\nalmost a whisper",
        };
        var editor = new ScreenplayEditor_BeatEditor { Beat = beat };

        Assert.Equal("laugh and play to see a lamb at school.", editor.SpokenTextInput);
        Assert.Equal("softly, almost a whisper", editor.ParentheticalInput);
        // Reading the proxy must not rewrite the model — untouched beats round-trip unchanged.
        Assert.Equal("laugh and play\nto see a lamb at school.", beat.SpokenText);
        Assert.Equal("softly,\nalmost a whisper", beat.Parenthetical);

        editor.SpokenTextInput = "laugh and play to see a lamb at school!";
        Assert.Equal("laugh and play to see a lamb at school!", beat.SpokenText);
    }
}
