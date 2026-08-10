using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.ScreenplayEditor.Components;

public partial class ScreenplayEditor_LocationModal : ComponentBase
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

    /// <summary>Location name from the scene that opened this modal (scrolled into view).</summary>
    [Parameter]
    public string? FocusName { get; set; }

    public string NewLocationName { get; set; } = "";

    private bool _scrollPending;

    public List<string> OrderedLocationNames
    {
        get
        {
            var all = Model.GetAllLocations().ToList();
            if (string.IsNullOrWhiteSpace(FocusName)) return all;
            var focus = FocusName.Trim();
            // Ensure focused location exists so it can be edited even if not yet in profiles.
            if (!all.Any(n => n.Equals(focus, StringComparison.OrdinalIgnoreCase)))
            {
                Model.GetOrCreateLocationProfile(focus.ToUpperInvariant());
                all = Model.GetAllLocations().ToList();
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
        if (string.IsNullOrEmpty(safe)) safe = "loc";
        return "spe-loc-" + safe.ToLowerInvariant();
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

    public async Task AddLocation()
    {
        if (!string.IsNullOrWhiteSpace(NewLocationName))
        {
            string upper = NewLocationName.Trim().ToUpperInvariant();
            Model.GetOrCreateLocationProfile(upper);
            NewLocationName = "";
            await OnChanged();
        }
    }

    public async Task RemoveLocation(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string upper = name.Trim().ToUpperInvariant();
            Model.LocationProfiles.RemoveAll(l => l.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
            foreach (var scene in Model.Scenes)
            {
                if (scene.Location.Equals(upper, StringComparison.OrdinalIgnoreCase))
                {
                    scene.Location = "NEW LOCATION";
                }
            }
            await OnChanged();
        }
    }
}
