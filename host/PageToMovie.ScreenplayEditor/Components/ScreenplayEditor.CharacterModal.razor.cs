using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_CharacterModal : ComponentBase
{
    [Inject] private IJSRuntime Js { get; set; } = null!;

    [Parameter]
    public bool IsOpen { get; set; }

    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    [Parameter]
    public ScreenplayModel Model { get; set; } = new();

    [Parameter]
    public EventCallback OnChangedCallback { get; set; }

    /// <summary>Speaker from the dialogue line that opened this modal.</summary>
    [Parameter]
    public string? FocusName { get; set; }

    public string NewCharacterName { get; set; } = "";

    private bool _scrollPending;

    public List<string> OrderedCharacterNames
    {
        get
        {
            var all = Model.GetAllCharacters().ToList();
            if (string.IsNullOrWhiteSpace(FocusName)) return all;
            var focus = FocusName.Trim();
            if (!all.Any(n => n.Equals(focus, StringComparison.OrdinalIgnoreCase)))
            {
                Model.GetOrCreateCharacterProfile(focus.ToUpperInvariant());
                all = Model.GetAllCharacters().ToList();
            }
            return all
                .OrderByDescending(n => n.Equals(focus, StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    protected override void OnParametersSet()
    {
        if (IsOpen && !string.IsNullOrWhiteSpace(FocusName))
            _scrollPending = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_scrollPending || !IsOpen || string.IsNullOrWhiteSpace(FocusName))
            return;
        _scrollPending = false;
        var id = CardDomId(FocusName);
        try
        {
            await Js.InvokeVoidAsync("eval",
                $"document.getElementById('{id}')?.scrollIntoView({{block:'nearest',behavior:'smooth'}})");
        }
        catch { /* JS optional */ }
    }

    internal static string CardDomId(string name)
    {
        var safe = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "char";
        return "spe-char-" + safe.ToLowerInvariant();
    }

    internal bool IsFocused(string name) =>
        !string.IsNullOrWhiteSpace(FocusName)
        && name.Equals(FocusName.Trim(), StringComparison.OrdinalIgnoreCase);

    public async Task Close()
    {
        IsOpen = false;
        if (IsOpenChanged.HasDelegate)
        {
            await IsOpenChanged.InvokeAsync(false);
        }
    }

    public async Task OnChanged()
    {
        if (OnChangedCallback.HasDelegate)
        {
            await OnChangedCallback.InvokeAsync();
        }
    }

    public async Task AddCharacter()
    {
        if (!string.IsNullOrWhiteSpace(NewCharacterName))
        {
            string upper = NewCharacterName.Trim().ToUpperInvariant();
            Model.GetOrCreateCharacterProfile(upper);
            NewCharacterName = "";
            await OnChanged();
        }
    }

    public async Task RemoveCharacter(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string upper = name.Trim().ToUpperInvariant();
            Model.CharacterProfiles.RemoveAll(c => c.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
            await OnChanged();
        }
    }
}
