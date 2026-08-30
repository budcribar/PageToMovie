using System.Text.Json;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Engine;
using PageToMovie.Engine.ModelBacked;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The cleanup passes that take model text apart must never take more than the thing they name.
/// Each case here is a shape where an unbounded match ate the surrounding content.
/// </summary>
public class StyleLockAndLightingBoundsTests
{
    [Fact]
    public void Inline_lock_loses_only_its_own_sentence_not_the_beat()
    {
        var stripped = ClipVideoPromptBuilder.StripStyleLocksFromAction(
            "Nick walks into the parlor. STYLE LOCK: stylized 3D animated look. " +
            "He sits down and speaks to the old man.");

        Assert.Equal("Nick walks into the parlor. He sits down and speaks to the old man.", stripped);
    }

    [Fact]
    public void Lock_running_to_the_end_of_a_line_leaves_the_lines_under_it()
    {
        var stripped = ClipVideoPromptBuilder.StripStyleLocksFromAction(
            "STYLE LOCK: watercolor picture-book, never photoreal\nMary crosses the room\nShe kneels by the fire");

        Assert.Contains("Mary crosses the room", stripped, StringComparison.Ordinal);
        Assert.Contains("She kneels by the fire", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("watercolor picture-book", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Style_head_comes_from_the_tag_not_from_prose_under_it()
    {
        Assert.Equal(
            "STYLE LOCK: 2D watercolor",
            ClipVideoPromptBuilder.ExtractStyleHead("<StyleLock>2D watercolor</StyleLock>\nMary crosses the room"));

        // Prose is not a field: the plan writes <StyleLock>, and guessing where a prose lock ends
        // is what swallowed the action under it.
        Assert.Null(ClipVideoPromptBuilder.ExtractStyleHead(
            "STYLE LOCK: watercolor picture-book\nMary crosses the room\nShe kneels by the fire"));
    }

    [Fact]
    public void Lighting_keeps_its_light_when_a_stock_clause_is_dropped()
    {
        var clean = CinematicLightingClassifier.SanitizeLightingToken(
            "Chiaroscuro candlelight, Kodak Vision3 500T film stock, deep obsidian shadows");

        Assert.NotNull(clean);
        Assert.Contains("Chiaroscuro candlelight", clean, StringComparison.Ordinal);
        Assert.Contains("deep obsidian shadows", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("film stock", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lighting_line_that_is_only_a_stock_mention_is_dropped_whole()
    {
        // No lighting directive beats a mangled fragment: this once returned "Warm golden".
        Assert.Null(CinematicLightingClassifier.SanitizeLightingToken("Shot on Kodak film stock"));
    }

    [Fact]
    public void Medium_negatives_survive_ordinary_prose_about_a_look_changing()
    {
        var clip = Clip(new
        {
            clip_number = 1,
            visual_prompt = "<Action>Her look changes as the world becomes still.</Action>",
            characters_on_screen = Array.Empty<string>(),
        });

        Assert.False(ClipVideoPromptBuilder.ClipDeclaresDifferentMedium(
            clip, VisualMediumStyles.MediumIllustrated));
    }

    [Fact]
    public void A_clip_that_declares_another_medium_opts_out_of_the_film_negatives()
    {
        var crossover = Clip(new
        {
            clip_number = 1,
            visual_prompt = "<Action>The page falls away.</Action>",
            visual_medium = VisualMediumStyles.MediumPhotoreal,
        });

        Assert.True(ClipVideoPromptBuilder.ClipDeclaresDifferentMedium(
            crossover, VisualMediumStyles.MediumIllustrated));
        Assert.False(ClipVideoPromptBuilder.ClipDeclaresDifferentMedium(
            crossover, VisualMediumStyles.MediumPhotoreal));
    }

    private static JsonElement Clip(object shape) =>
        JsonDocument.Parse(JsonSerializer.Serialize(shape)).RootElement.Clone();
}
