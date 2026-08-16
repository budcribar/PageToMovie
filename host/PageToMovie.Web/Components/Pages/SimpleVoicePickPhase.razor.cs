using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

/// <summary>
/// Phase.Pick. <see cref="StoriesLoading"/> is passed as a changing parameter so this
/// child re-renders when the spinner flips (CascadingValue IsFixed does not notify).
/// </summary>
public partial class SimpleVoicePickPhase : PageSliceComponent
{
    [CascadingParameter] public required SimpleVoice Host { get; set; }

    [Parameter] public bool StoriesLoading { get; set; }

    [Parameter] public bool Busy { get; set; }
}
