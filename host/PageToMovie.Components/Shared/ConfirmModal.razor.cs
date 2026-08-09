using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class ConfirmModal
{
    /// <summary>When true the modal is rendered. The parent owns the open/close state.</summary>
    [Parameter] public bool Visible { get; set; }
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string ConfirmLabel { get; set; } = "Confirm";
    [Parameter] public string CancelLabel { get; set; } = "Cancel";
    /// <summary>Bootstrap button class for the confirm action (e.g. "btn-danger", "btn-primary").</summary>
    [Parameter] public string ConfirmClass { get; set; } = "btn-danger";
    /// <summary>Disables both buttons while a parent operation is in flight.</summary>
    [Parameter] public bool Busy { get; set; }
    /// <summary>Optional data-testid on the confirm button (omitted when null).</summary>
    [Parameter] public string? ConfirmTestId { get; set; }
    /// <summary>Extra classes on the modal-dialog (e.g. "modal-lg").</summary>
    [Parameter] public string DialogClass { get; set; } = "";
    [Parameter] public string BodyClass { get; set; } = "py-3";
    [Parameter] public string BackdropOpacity { get; set; } = ".5";
    [Parameter] public EventCallback OnConfirm { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
