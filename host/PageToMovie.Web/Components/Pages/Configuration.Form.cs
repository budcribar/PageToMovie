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

// Forwarders: ConfigurationProjectForm → Host.*
public partial class Configuration
{
    internal Task OnProjectChangedAsync(ChangeEventArgs e) => Form.OnProjectChangedAsync(e);

    internal Task LoadAsync() => Form.LoadAsync();

    internal Task SaveAsync() => Form.SaveAsync();

    internal Task PersistProjectConfigAsync() => Form.PersistProjectConfigAsync();

    internal Dictionary<string, object?> BuildVendorCostEstimatesSnapshot() => Form.BuildVendorCostEstimatesSnapshot();

    internal string GetStr(string key, string fallback) => Form.GetStr(key, fallback);

    internal int GetInt(string key, int fallback) => Form.GetInt(key, fallback);

    internal double GetDouble(string key, double fallback) => Form.GetDouble(key, fallback);

    internal bool GetBool(string key, bool fallback) => Form.GetBool(key, fallback);

    internal Task ScheduleAutoSaveAsync() => Form.ScheduleAutoSaveAsync();

    internal Task ClearSaveStatusLaterAsync(int epoch) => Form.ClearSaveStatusLaterAsync(epoch);

}
