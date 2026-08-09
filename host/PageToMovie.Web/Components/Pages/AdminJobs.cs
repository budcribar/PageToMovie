using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    /// <summary>Jobs/locks/log domain for the Admin page.</summary>
    public sealed class AdminJobs
    {
        private readonly Admin S;
        public AdminJobs(Admin host) => S = host;

        internal bool _hubLive;
        internal List<AdminLockDto> _locks = new();
        internal string? _logJobId;
        internal string _logJobIdInput = "";
        internal JobSnapshot? _jobLog;
        internal string? _logError;

        internal string JobLogText =>
            _jobLog?.Log is { Count: > 0 } lines
                ? string.Join("\n", lines)
                : "(no log lines — job may have finished and been pruned, or never wrote logs)";

        internal async Task CancelJobAsync(string jobId)
        {
            S._busy = true;
            S._actionMsg = null;
            try
            {
                await S.Api.AdminCancelJobAsync(jobId);
                S._actionMsg = $"Cancel requested for {jobId}";
                await S.State.RefreshAsync();
            }
            catch (Exception ex) { S._actionMsg = ex.Message; }
            finally { S._busy = false; }
        }

        internal async Task LoadJobLogAsync(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;
            _logError = null;
            _logJobId = jobId.Trim();
            _logJobIdInput = _logJobId;
            try
            {
                var detail = await S.Api.GetJobByIdAsync(_logJobId);
                _jobLog = detail?.Job;
                if (_jobLog is null)
                    _logError = "Job not found (finished jobs are pruned from memory after a while). Check Railway logs for older work.";
                else
                    S._actionMsg = $"Loaded log for {ShortId(_logJobId)} · {_jobLog.Log?.Count ?? 0} line(s)";
            }
            catch (Exception ex)
            {
                _jobLog = null;
                _logError = ex.Message;
            }
        }

        internal void ClearJobLog()
        {
            _jobLog = null;
            _logJobId = null;
            _logError = null;
        }

        internal async Task ReleaseLockAsync(string resource)
        {
            S._busy = true;
            S._actionMsg = null;
            try
            {
                await S.Api.AdminReleaseLockAsync(resource, force: true);
                S._actionMsg = $"Released {resource}";
                await S.State.RefreshAsync();
            }
            catch (Exception ex) { S._actionMsg = ex.Message; }
            finally { S._busy = false; }
        }
    }
}
