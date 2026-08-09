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

namespace PageToMovie.Web.Components.Pages;

public partial class ClipPromptCompareViewer
{
    [Parameter] public string VersionAHash { get; set; } = "git-commit-v1";
    [Parameter] public string VersionAVideoUrl { get; set; } = "";
    [Parameter] public string VersionAPrompt { get; set; } = "Cinematic shot of Buster standing in dimly lit parlor.";

    [Parameter] public string VersionBHash { get; set; } = "git-commit-v2";
    [Parameter] public string VersionBVideoUrl { get; set; } = "";
    [Parameter] public string VersionBPrompt { get; set; } = "Cinematic shot of Buster standing in dimly lit parlor, warm candlelight, 35mm lens, 4k resolution.";
}
