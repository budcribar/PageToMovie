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

// Forwarders: HomeJobs → Host.*
public partial class Home
{
    internal string? PackageHistoryUrl => Jobs.PackageHistoryUrl;

    internal bool CanSavePackageRevision => Jobs.CanSavePackageRevision;

    internal Task RefreshPackageStatusAsync() => Jobs.RefreshPackageStatusAsync();

    internal void ToggleJobsExpanded() => Jobs.ToggleJobsExpanded();

    internal void SyncJobsExpandedFromJob() => Jobs.SyncJobsExpandedFromJob();

    internal void OnJobUpdated(JobSnapshot snap) => Jobs.OnJobUpdated(snap);

    internal void OnJobLog(string line) => Jobs.OnJobLog(line);

    internal static bool JobLogHasVideoPayload(JobSnapshot j) => HomeJobs.JobLogHasVideoPayload(j);

    internal Task CancelAsync() => Jobs.CancelAsync();

    internal static string FriendlyStatus(string? status) => HomeJobs.FriendlyStatus(status);

    internal static string FriendlyKind(string? kind) => HomeJobs.FriendlyKind(kind);

    internal static string FriendlyJobLine(JobSnapshot j) => HomeJobs.FriendlyJobLine(j);

    internal static string FriendlyOperatorError(JobSnapshot j) => HomeJobs.FriendlyOperatorError(j);

    internal static string SanitizeOperatorError(string raw) => HomeJobs.SanitizeOperatorError(raw);

    internal bool HasActiveJob => Jobs.HasActiveJob;
    internal string ActivePackageProjectId => Jobs.ActivePackageProjectId;
}
