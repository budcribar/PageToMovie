using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class CharacterPickCard
{
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public string? ImageUrl { get; set; }
    [Parameter] public bool Selected { get; set; }
    [Parameter] public bool ShowUseButton { get; set; } = true;
    [Parameter] public string UseLabel { get; set; } = "Use";
    [Parameter] public string? UseTestId { get; set; } = "char-use-look";
    [Parameter] public string? UseTitle { get; set; }
    [Parameter] public bool UseDisabled { get; set; }
    /// <summary>
    /// This card's save is in flight. Saving a look is a server round-trip through the portrait
    /// style gate, so it can sit for seconds — a button that only greys out reads as a click that
    /// did not land, and gets clicked again.
    /// </summary>
    [Parameter] public bool Busy { get; set; }
    [Parameter] public string? BusyLabel { get; set; }
    [Parameter] public EventCallback OnSelect { get; set; }
    [Parameter] public EventCallback OnUse { get; set; }
    [Parameter] public EventCallback OnFrameClick { get; set; }
    [Parameter] public bool FrameDisabled { get; set; }
    [Parameter] public string? FrameTitle { get; set; }
    [Parameter] public string? FrameAriaLabel { get; set; }
    [Parameter] public bool ShowZoomBadge { get; set; }
    [Parameter] public string ZoomBadgeText { get; set; } = "Save";
    [Parameter] public string? TestId { get; set; }
    [Parameter] public RenderFragment? ExtraActions { get; set; }
}
