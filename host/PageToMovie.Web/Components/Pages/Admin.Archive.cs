using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminArchive → Host.*
public partial class Admin
{
    internal const long MaxImportBytes = AdminArchive.MaxImportBytes;

    internal Task RefreshProjectOptionsAsync() => Archive.RefreshProjectOptionsAsync();

    internal void OnImportFileSelected(InputFileChangeEventArgs e) => Archive.OnImportFileSelected(e);

    internal Task RunArchiveActionAsync(Func<Task> action) => Archive.RunArchiveActionAsync(action);

    internal Task ExportProjectAsync() => Archive.ExportProjectAsync();

    internal Task ExportLogsAsync() => Archive.ExportLogsAsync();

    internal Task ImportProjectAsync() => Archive.ImportProjectAsync();

    internal Task AugmentMusicAsync() => Archive.AugmentMusicAsync();

    internal Task SynthesizeAudioAsync() => Archive.SynthesizeAudioAsync();
}
