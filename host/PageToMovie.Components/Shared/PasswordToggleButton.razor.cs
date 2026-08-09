using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class PasswordToggleButton
{
    [Parameter, EditorRequired] public bool Shown { get; set; }
    [Parameter] public EventCallback OnToggle { get; set; }
}
