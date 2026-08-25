using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_ClipFieldEditor
{
    [Parameter] public ClipEditRequest? Editor { get; set; }
    [Parameter] public bool IsNew { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public IReadOnlyList<string> CharacterOptions { get; set; } = Array.Empty<string>();
    [Parameter] public HashSet<string> CastKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback<(string Key, bool On)> CastToggled { get; set; }

    private bool ShowAdvanced;

    private Task ToggleCast(string key, bool on) => CastToggled.InvokeAsync((key, on));

    private static string FormatChar(string key) => KeyFormatting.ShortChar(key);

    /// <summary>Escape hatch: edit the whole prompt as one string, exactly as before.</summary>
    private bool RawPrompt;

    private List<ClipPromptSection> _sections = new();

    /// <summary>
    /// The request instance the sections were parsed from. Re-parsing on every render would throw
    /// away whatever the user is part-way through typing, so the split is rebuilt only when the
    /// modal is opened on a different clip.
    /// </summary>
    private ClipEditRequest? _sectionsFor;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_sectionsFor, Editor))
            return;
        _sectionsFor = Editor;
        _sections = Editor is null
            ? new List<ClipPromptSection>()
            : ClipPromptSections.Parse(Editor.VisualPrompt).ToList();
    }

    /// <summary>
    /// Label for one box. Free-text runs are all "Action"; they are numbered only when a prompt
    /// has more than one, so the common single-action case stays unadorned.
    /// </summary>
    private string SectionLabel(int index)
    {
        var section = _sections[index];
        if (!section.IsFreeText)
            return section.Label;
        var freeText = _sections.Where(s => s.IsFreeText).ToList();
        if (freeText.Count <= 1)
            return section.Label;
        return $"{section.Label} {freeText.FindIndex(s => ReferenceEquals(s, section)) + 1}";
    }

    private static int SectionRows(ClipPromptSection section) => section.Field switch
    {
        ClipPromptField.Action => 4,
        ClipPromptField.StyleLock or ClipPromptField.Lighting or ClipPromptField.Grade => 2,
        _ => 1,
    };

    private static string SectionHint(ClipPromptField field) => field switch
    {
        ClipPromptField.Action => "What happens in this shot",
        ClipPromptField.StyleLock => "Art direction held across every clip",
        ClipPromptField.Setting => "Where and when",
        ClipPromptField.Cast => "Who is visible",
        ClipPromptField.Sound => "Ambient / foley cue",
        ClipPromptField.Lighting => "Light quality and direction",
        ClipPromptField.Camera => "Shot size, lens, movement",
        ClipPromptField.Performance => "Acting intensity and expression",
        ClipPromptField.Optics => "Aperture and depth of field",
        ClipPromptField.Grade => "Film stock and colour",
        _ => "",
    };

    /// <summary>
    /// Write one box back. The prompt is rebuilt from all sections, so untouched fields — and the
    /// exact spacing between them — come back verbatim.
    /// </summary>
    private void SetSection(int index, string? value)
    {
        if (Editor is null || index < 0 || index >= _sections.Count)
            return;
        _sections[index] = _sections[index].WithValue(value);
        Editor.VisualPrompt = ClipPromptSections.Compose(_sections);
    }

    /// <summary>Switching back from raw text re-splits whatever the user typed there.</summary>
    private void ToggleRawPrompt()
    {
        RawPrompt = !RawPrompt;
        if (!RawPrompt && Editor is not null)
            _sections = ClipPromptSections.Parse(Editor.VisualPrompt).ToList();
    }
}
