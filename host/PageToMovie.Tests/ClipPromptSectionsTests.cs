using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Splitting a clip prompt into labelled edit boxes. The prompt is only half tagged — Lighting /
/// Camera / Performance / Optics are real tags, STYLE LOCK, the scene slug, the cast list, the
/// sound cue and the colour grade are prose — so both forms have to parse, and the whole thing has
/// to go back together byte for byte or the editor silently rewrites the shot plan.
/// </summary>
public class ClipPromptSectionsTests
{
    // Shaped exactly like a Stage 2 prompt (Mary19 S02C01, after tagging).
    private const string RealShape =
        "<StyleLock>stylized 3D animated children's picture-book CG -- not photoreal</StyleLock> " +
        "<Setting>INT. SCHOOLROOM - DAY</Setting> " +
        "<Cast>Character_The_Children, Character_Mary</Cast> " +
        "<Action>A one-room schoolhouse in warm wood-brown washes. MARY comes through the door</Action> " +
        "<Sound>wooden desks scrape, children gasp</Sound> " +
        "<Wardrobe>Character_Mary still wears a white pinafore</Wardrobe> " +
        "<Lighting>Soft warm daylight through tall windows.</Lighting> " +
        "<Camera>Wide 4:3, 27mm lens, locked.</Camera> " +
        "<Performance>Acting intensity 5/10: wide-eyed.</Performance> " +
        "<Optics>f/1.4 shallow depth of field.</Optics> " +
        "<Grade>Kodak Vision3 250D 5207 film stock, warm honey-amber woods</Grade>";

    [Fact]
    public void Parse_gives_one_box_per_tagged_field()
    {
        var byField = ClipPromptSections.Parse(RealShape)
            .GroupBy(s => s.Field)
            .ToDictionary(g => g.Key, g => g.First().Value.Trim());

        Assert.Equal("stylized 3D animated children's picture-book CG -- not photoreal", byField[ClipPromptField.StyleLock]);
        Assert.Equal("INT. SCHOOLROOM - DAY", byField[ClipPromptField.Setting]);
        Assert.Equal("Character_The_Children, Character_Mary", byField[ClipPromptField.Cast]);
        Assert.Equal("wooden desks scrape, children gasp", byField[ClipPromptField.Sound]);
        Assert.Equal("Character_Mary still wears a white pinafore", byField[ClipPromptField.Wardrobe]);
        Assert.Equal("Soft warm daylight through tall windows.", byField[ClipPromptField.Lighting]);
        Assert.Equal("Wide 4:3, 27mm lens, locked.", byField[ClipPromptField.Camera]);
        Assert.Equal("Acting intensity 5/10: wide-eyed.", byField[ClipPromptField.Performance]);
        Assert.Equal("f/1.4 shallow depth of field.", byField[ClipPromptField.Optics]);
        Assert.StartsWith("Kodak Vision3 250D 5207", byField[ClipPromptField.Grade]);

        // The action — the field that caused the lamb bug — gets its own box.
        Assert.Contains("comes through the door", byField[ClipPromptField.Action]);
    }

    /// <summary>
    /// A plan built before Stage 2 tagged its fields still opens and still round-trips — it just
    /// arrives as one Action box. There is no prose fallback to guess the old layout back.
    /// </summary>
    [Fact]
    public void Untagged_legacy_plan_degrades_to_one_editable_box()
    {
        const string legacy =
            "STYLE LOCK: watercolor. INT. SCHOOLROOM - DAY. MARY comes through the door. " +
            "Color grading: hand-tinted print stock";
        var sections = ClipPromptSections.Parse(legacy);
        var only = Assert.Single(sections);
        Assert.Equal(ClipPromptField.Action, only.Field);
        Assert.Equal(legacy, ClipPromptSections.Compose(sections));
    }

    /// <summary>Opening and saving without editing must not change one byte of the shot plan.</summary>
    [Theory]
    [InlineData(RealShape)]
    [InlineData("Just free prose with no markers at all.")]
    [InlineData("<Camera>Only a tag.</Camera>")]
    [InlineData("STYLE LOCK: only a style lock")]
    [InlineData("")]
    [InlineData("  \n  ")]
    [InlineData("<Setting>EXT. COUNTRY LANE - DAY</Setting> <Lighting>Even daylight.</Lighting>")]
    public void Compose_of_an_unedited_parse_is_the_original(string prompt)
    {
        Assert.Equal(prompt, ClipPromptSections.Compose(ClipPromptSections.Parse(prompt)));
    }

    [Fact]
    public void Editing_one_field_leaves_the_rest_untouched()
    {
        var sections = ClipPromptSections.Parse(RealShape).ToList();
        var i = sections.FindIndex(s => s.Field == ClipPromptField.Optics);
        sections[i] = sections[i].WithValue("f/8 deep focus.");

        var composed = ClipPromptSections.Compose(sections);
        Assert.Contains("<Optics>f/8 deep focus.</Optics>", composed);
        Assert.DoesNotContain("f/1.4", composed);
        Assert.Contains("MARY comes through the door", composed);
        Assert.Contains("<Camera>Wide 4:3, 27mm lens, locked.</Camera>", composed);
        Assert.StartsWith("<StyleLock>", composed);
    }

    /// <summary>A field cleared to empty takes its marker with it — never a bare "STYLE LOCK:".</summary>
    [Fact]
    public void Clearing_a_field_removes_its_marker_too()
    {
        var sections = ClipPromptSections.Parse(RealShape).ToList();
        var i = sections.FindIndex(s => s.Field == ClipPromptField.Sound);
        sections[i] = sections[i].WithValue("");

        var composed = ClipPromptSections.Compose(sections);
        Assert.DoesNotContain("<Sound>", composed);
        Assert.Contains("<Lighting>", composed);
    }

    [Fact]
    public void Empty_prompt_still_offers_somewhere_to_type()
    {
        var only = Assert.Single(ClipPromptSections.Parse(""));
        Assert.Equal(ClipPromptField.Action, only.Field);
        Assert.True(only.IsFreeText);
    }

    /// <summary>A tag the planner has not emitted yet must not break the split.</summary>
    [Fact]
    public void Unknown_tags_stay_inside_free_text()
    {
        const string p = "MARY waits. <Negative>no logos</Negative> <Camera>Close.</Camera>";
        var sections = ClipPromptSections.Parse(p);
        Assert.Equal(p, ClipPromptSections.Compose(sections));
        Assert.Contains(sections, s => s.Field == ClipPromptField.Camera && s.Value == "Close.");
        Assert.Contains(sections, s => s.Field == ClipPromptField.Action && s.Value.Contains("<Negative>"));
    }
}
