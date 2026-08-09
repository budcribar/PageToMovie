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

// Forwarders: CharactersJobs → Host.*
public partial class Characters
{
    internal static string FriendlyCharacterJobStatus(JobSnapshot job) => CharactersJobs.FriendlyCharacterJobStatus(job);

    internal void OnJobUpdated(JobSnapshot snap) => Jobs.OnJobUpdated(snap);

    internal void OnJobLog(string line) => Jobs.OnJobLog(line);

    internal Task CancelAsync() => Jobs.CancelAsync();

    internal ValueTask DisposeAsyncCore() => Jobs.DisposeAsyncCore();


    internal bool JobRunning => Jobs.JobRunning;
        internal bool VoiceJobRunning => Jobs.VoiceJobRunning;
        internal bool PlateSortRunning => Jobs.PlateSortRunning;
}
