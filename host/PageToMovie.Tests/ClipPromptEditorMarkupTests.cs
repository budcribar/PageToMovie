using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// "Edit Clip Script" splits the prompt into one labelled box per field instead of a single
/// textarea, with a raw-text escape hatch. The parse/compose behaviour itself is covered by
/// <see cref="ClipPromptSectionsTests"/>; this pins the wiring.
/// </summary>
public class ClipPromptEditorMarkupTests
{
    [Fact]
    public void Prompt_is_edited_as_one_box_per_field()
    {
        var razor = ReadPage("Scenes.ClipFieldEditor.razor");
        Assert.Contains("data-testid=\"clip-prompt-fields\"", razor, StringComparison.Ordinal);
        Assert.Contains("_sections.Count", razor, StringComparison.Ordinal);
        Assert.Contains("SectionLabel(index)", razor, StringComparison.Ordinal);
        Assert.Contains("SetSection(index", razor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parser can only split fields it knows. A prompt it reads oddly must stay editable, so
    /// the single-textarea path is kept rather than replaced.
    /// </summary>
    [Fact]
    public void Raw_text_escape_hatch_is_still_available()
    {
        var razor = ReadPage("Scenes.ClipFieldEditor.razor");
        Assert.Contains("data-testid=\"clip-prompt-raw-toggle\"", razor, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"clip-prompt-raw\"", razor, StringComparison.Ordinal);
        Assert.Contains("@bind=\"ed.VisualPrompt\"", razor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-parsing on every render would discard half-typed input, so the split is rebuilt only
    /// when the modal opens on a different clip.
    /// </summary>
    [Fact]
    public void Sections_are_reparsed_per_clip_not_per_render()
    {
        var code = ReadPage("Scenes.ClipFieldEditor.razor.cs");
        Assert.Contains("ReferenceEquals(_sectionsFor, Editor)", code, StringComparison.Ordinal);
        Assert.Contains("ClipPromptSections.Parse", code, StringComparison.Ordinal);
        Assert.Contains("ClipPromptSections.Compose", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a character says is edited in one place — the Dialogue field, which is the copy that
    /// reaches the model. A plan built before Stage 2 stopped baking the line into the prompt
    /// still carries a <c>&lt;Speech&gt;</c> block; it must not be offered as a second box, since
    /// editing it never changed a word of what was spoken.
    /// </summary>
    [Fact]
    /// <remarks>
    /// The same holds for a legacy <c>&lt;Sound&gt;</c> block: <c>audio_payload</c> owns the sound
    /// and the AUDIO block delivers it, so a box for it would edit nothing — and leaving the block
    /// in the prompt costs a spoken word, not just budget.
    /// </remarks>
    public void Legacy_speech_and_sound_blocks_are_not_a_second_place_to_edit()
    {
        var razor = ReadPage("Scenes.ClipFieldEditor.razor");
        Assert.Contains("IsEditableSection(section)", razor, StringComparison.Ordinal);

        var code = ReadPage("Scenes.ClipFieldEditor.razor.cs");
        Assert.Contains(
            "section.Field is not (ClipPromptField.Speech or ClipPromptField.Sound)",
            code, StringComparison.Ordinal);
        // The dialogue box stays — it is where the spoken line is read from and written back.
        Assert.Contains("data-testid=\"clip-editor-dialogue\"", razor, StringComparison.Ordinal);
        Assert.Contains("@bind=\"ed.Dialogue\"", razor, StringComparison.Ordinal);
    }

    private static string ReadPage(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
