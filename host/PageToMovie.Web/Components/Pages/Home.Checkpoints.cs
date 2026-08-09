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

// Forwarders: HomeCheckpoints → Host.*
public partial class Home
{
    internal Task ToggleCheckpointsAsync() => Checkpoints.ToggleCheckpointsAsync();

    internal Task LoadCheckpointsAsync() => Checkpoints.LoadCheckpointsAsync();

    internal Task CreateCheckpointAsync() => Checkpoints.CreateCheckpointAsync();

    internal Task RevertCheckpointAsync(CheckpointDto cp) => Checkpoints.RevertCheckpointAsync(cp);

}
