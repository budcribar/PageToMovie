using Microsoft.AspNetCore.Components;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_BeatEditor : ComponentBase
{
    [Parameter]
    public ScreenplayBeat Beat { get; set; } = new();

    [Parameter]
    public int Index { get; set; }

    [Parameter]
    public bool IsFirst { get; set; }

    [Parameter]
    public bool IsLast { get; set; }

    [Parameter]
    public List<string> AvailableCharacters { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    /// <summary>Opens the characters modal focused on this beat's speaker when possible.</summary>
    [Parameter]
    public EventCallback<string?> OnEditCharactersClick { get; set; }

    [Parameter]
    public EventCallback OnMoveUpCallback { get; set; }

    [Parameter]
    public EventCallback OnMoveDownCallback { get; set; }

    [Parameter]
    public EventCallback OnDeleteCallback { get; set; }

    [Parameter]
    public EventCallback<(int from, int to)> OnReorderBeats { get; set; }

    public static int ActiveDragIndex { get; set; } = -1;

    /// <summary>
    /// Multi-line Fountain dialogue (verse: "…laugh and play" / "to see a lamb at school.") is kept
    /// in the model with its line breaks, but the beat row is a single-line &lt;input&gt; and browsers
    /// strip newlines from a text input's value outright — "play\nto" rendered as "playto", and a
    /// save then wrote that back into the screenplay. Present line breaks as spaces here; only a
    /// beat the user actually edits is flattened, untouched beats round-trip unchanged.
    /// </summary>
    public string SpokenTextInput
    {
        get => FlattenLineBreaks(Beat.SpokenText);
        set => Beat.SpokenText = value;
    }

    public string ParentheticalInput
    {
        get => FlattenLineBreaks(Beat.Parenthetical);
        set => Beat.Parenthetical = value;
    }

    public static string FlattenLineBreaks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0) return text;
        return LineBreakRun.Replace(text, " ").Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex LineBreakRun =
        new(@"[ \t]*(?:\r\n|\r|\n)+[ \t]*", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task EditCharactersAsync()
    {
        if (OnEditCharactersClick.HasDelegate)
            await OnEditCharactersClick.InvokeAsync(Beat.Speaker);
    }

    public async Task MoveUp()
    {
        if (OnMoveUpCallback.HasDelegate)
        {
            await OnMoveUpCallback.InvokeAsync();
        }
    }

    public async Task MoveDown()
    {
        if (OnMoveDownCallback.HasDelegate)
        {
            await OnMoveDownCallback.InvokeAsync();
        }
    }

    public async Task Delete()
    {
        if (OnDeleteCallback.HasDelegate)
        {
            await OnDeleteCallback.InvokeAsync();
        }
    }

    public void HandleDragStart()
    {
        ActiveDragIndex = Index;
    }

    public async Task HandleDrop()
    {
        if (ActiveDragIndex >= 0 && ActiveDragIndex != Index && OnReorderBeats.HasDelegate)
        {
            await OnReorderBeats.InvokeAsync((ActiveDragIndex, Index));
        }
        ActiveDragIndex = -1;
    }
}
