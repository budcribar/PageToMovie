using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: ConfigurationMediaTheme → Host.*
public partial class Configuration
{
    internal Task PreviewThemeAsync() => Media.PreviewThemeAsync();

    internal void OnMediaFolderChanged() => Media.OnMediaFolderChanged();

    internal Task OnThemeChangedAsync() => Media.OnThemeChangedAsync();

    internal Task ConnectMediaFolderAsync() => Media.ConnectMediaFolderAsync();

    internal Task ReconnectMediaFolderAsync() => Media.ReconnectMediaFolderAsync();

}
