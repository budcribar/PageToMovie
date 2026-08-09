using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Forwarders: AdminJobs → Host.*
public partial class Admin
{
    internal string JobLogText => Jobs.JobLogText;

    internal Task CancelJobAsync(string jobId) => Jobs.CancelJobAsync(jobId);

    internal Task LoadJobLogAsync(string jobId) => Jobs.LoadJobLogAsync(jobId);

    internal void ClearJobLog() => Jobs.ClearJobLog();

    internal Task ReleaseLockAsync(string resource) => Jobs.ReleaseLockAsync(resource);
}
