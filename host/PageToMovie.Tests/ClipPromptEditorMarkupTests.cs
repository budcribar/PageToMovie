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
