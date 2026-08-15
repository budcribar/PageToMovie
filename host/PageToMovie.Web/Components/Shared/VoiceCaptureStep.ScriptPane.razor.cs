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

namespace PageToMovie.Web.Components.Shared;

public partial class VoiceCaptureStep_ScriptPane
{
    [CascadingParameter] public required VoiceCaptureStep Host { get; set; }

    /// <summary>Changing parameters so IsFixed cascade still re-renders lights / scores.</summary>
    [Parameter] public int Light { get; set; }
    [Parameter] public bool Recording { get; set; }
    [Parameter] public bool Listening { get; set; }
    [Parameter] public int TeleSession { get; set; }
    [Parameter] public int ScoreEpoch { get; set; }
}
