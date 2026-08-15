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

public partial class VoiceCaptureStep_ComparePane
{
    [CascadingParameter] public VoiceCaptureStep Host { get; set; } = default;

    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool Recording { get; set; }
    [Parameter] public int Light { get; set; }
    [Parameter] public bool Listening { get; set; }
    [Parameter] public bool HasTake { get; set; }
    [Parameter] public bool HasOriginal { get; set; }
    [Parameter] public int? Score { get; set; }

    private Task OnPlayYouAsync() => Host.PlayYouAsync();
    private Task OnPlayBothAsync() => Host.PlayBothAsync();
}
