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

    /// <summary>Location from outline click or the scene gear that opened this modal.</summary>
    [Parameter]
    public string? FocusName { get; set; }

    public string NewLocationName { get; set; } = "";

    /// <summary>
    /// Opened with a specific place (outline Locs / scene gear) — show only that card.
    /// Menu “Edit locations” with no focus still lists everything.
    /// </summary>
    public bool SingleLocationMode
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FocusName)) return false;
            // Ensure the focused place is in the model so it can be edited alone.
            var focus = FocusName.Trim().ToUpperInvariant();
            Model.GetOrCreateLocationProfile(focus);
            return true;
        }
    }

    public List<string> OrderedLocationNames
    {
        get
        {
            var all = Model.GetAllLocations().ToList();
            if (string.IsNullOrWhiteSpace(FocusName)) return all;

            var focus = FocusName.Trim().ToUpperInvariant();
            Model.GetOrCreateLocationProfile(focus);
            // Outline / scene gear → only that location.
            return new List<string> { focus };
        }
    }

    protected override void OnParametersSet()
    {
        // no scroll needed in single-location mode
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;

    internal static string CardDomId(string name)
    {
        var safe = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "loc";
        return "spe-loc-" + safe.ToLowerInvariant();
    }

    internal bool IsFocused(string name)
    {
        if (string.IsNullOrWhiteSpace(FocusName) || string.IsNullOrWhiteSpace(name))
            return false;
        var a = NormalizeLoc(FocusName);
        var b = NormalizeLoc(name);
        return a == b || a.Contains(b) || b.Contains(a);
    }

    private static string NormalizeLoc(string s) =>
        new string((s ?? "").Where(c => char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant();

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