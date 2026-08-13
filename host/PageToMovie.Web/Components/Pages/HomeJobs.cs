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

public partial class Home
{
    /// <summary>Jobs domain for the Home page. Owns related UI state and behavior.</summary>
    public sealed class HomeJobs
    {
        private readonly Home S;
        public HomeJobs(Home host) => S = host;

        internal JobSnapshot? _job;

        /// <summary>My jobs panel; collapsed by default, auto-opens when a job is running/queued.</summary>
            internal bool _jobsExpanded;

        /// <summary>Null = follow auto rules; otherwise honor last user click.</summary>
            internal bool? _jobsUserPreference;

        internal List<JobSnapshot> _myJobs = new();

        internal UncommittedStatusDto? _packageStatus;

        internal bool _packageStatusLoading;


        internal string ActivePackageProjectId =>
            S.ActiveProject.ProjectId ?? S.Projects._projects?.Active?.Id ?? "";


        internal string? PackageHistoryUrl
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_packageStatus?.HistoryUrl))
                    return _packageStatus.HistoryUrl;
                var pid = ActivePackageProjectId;
                if (string.IsNullOrWhiteSpace(pid)) return null;
                return S.Projects._historyUrls.TryGetValue(pid, out var hu) ? hu : null;
            }
        }


        internal bool CanSavePackageRevision
        {
            get
            {
                var pid = ActivePackageProjectId;
                if (string.IsNullOrWhiteSpace(pid) || !S.Session.IsLoggedIn) return false;
                if (S.Session.IsAdmin) return true;
                var owner = S.Projects._projects?.Active?.OwnerUserId
                            ?? S.Projects._projects?.Projects.FirstOrDefault(x =>
                                string.Equals(x.Id, pid, StringComparison.OrdinalIgnoreCase))?.OwnerUserId;
                return string.Equals(S.Session.UserId, owner, StringComparison.OrdinalIgnoreCase);
            }
        }


        internal async Task RefreshPackageStatusAsync()
        {
            var pid = S.ActiveProject.ProjectId ?? S.Projects._projects?.Active?.Id;
            if (string.IsNullOrWhiteSpace(pid) || !S.Session.IsLoggedIn)
            {
                _packageStatus = null;
                _packageStatusLoading = false;
                return;
            }

            _packageStatusLoading = true;
            try
            {
                var env = await S.Engine.GetProjectUncommittedStatusAsync(pid);
                _packageStatus = env?.Status;
                if (!string.IsNullOrWhiteSpace(_packageStatus?.LastCommitHash))
                    S.Projects._revisionHashes[pid] = _packageStatus.LastCommitHash;
                if (!string.IsNullOrWhiteSpace(_packageStatus?.HistoryUrl))
                    S.Projects._historyUrls[pid] = _packageStatus.HistoryUrl;
            }
            catch
            {
                // non-fatal
            }
            finally
            {
                _packageStatusLoading = false;
            }
        }


        internal bool HasActiveJob =>
            _job is not null &&
            (string.Equals(_job.Status, "running", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(_job.Status, "queued", StringComparison.OrdinalIgnoreCase));


        internal void ToggleJobsExpanded()
        {
            _jobsExpanded = !_jobsExpanded;
            _jobsUserPreference = _jobsExpanded;
        }


        internal void SyncJobsExpandedFromJob()
        {
            if (_jobsUserPreference is bool prefer)
                _jobsExpanded = prefer;
            else
                _jobsExpanded = HasActiveJob; // collapsed when idle, open when work is running
        }


        internal void OnJobUpdated(JobSnapshot snap)
        {
            _job = snap;
            SyncJobsExpandedFromJob();
            _ = S.InvokeAsync(async () =>
            {
                try
                {
                    var mine = await S.Engine.GetJobsAsync(mine: true);
                    _myJobs = mine?.Jobs?
                        .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                        .Take(12)
                        .ToList()
                        ?? new List<JobSnapshot>();
                }
                catch { /* ignore */ }
                SyncJobsExpandedFromJob();
                // Jobs write cost_ledger on completion — keep home spend fresh.
                if (snap.Status is "done" or "error" or "cancelled")
                {
                    try { await S.Costs.RefreshProjectCostAsync(); }
                    catch { /* ignore */ }
                }
                S.StateHasChanged();
            });
        }


        internal void OnJobLog(string line)
        {
            if (_job is not null)
            {
                _job.Message = line;
                // Keep rolling admin log so video API prompts appear live on Home
                _job.Log ??= new List<string>();
                _job.Log.Add(line);
                if (_job.Log.Count > 2000)
                    _job.Log.RemoveRange(0, _job.Log.Count - 1500);
            }
            _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal static bool JobLogHasVideoPayload(JobSnapshot j) =>
            j.Log.Any(l =>
                l.Contains("PROMPT BEGIN", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("[Grok] Submit", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("video-extend", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("reference-to-video", StringComparison.OrdinalIgnoreCase));


        internal async Task CancelAsync()
        {
            // Always dismiss local UI; server cancel is best-effort (deploy/restart 502s).
            _ = await S.Engine.TryCancelJobAsync();
            if (_job is not null)
            {
                _job.Status = "cancelled";
                _job.Message = "Cancelled";
                _job.FinishedAt = DateTimeOffset.UtcNow;
            }
            S._busy = false;
            S._error = null;
            S._message = "Cancelled. You can try again when ready.";
            S.StateHasChanged();
        }


        internal static string FriendlyStatus(string? status) =>
            string.IsNullOrWhiteSpace(status) ? "…" : status;


        internal static string FriendlyKind(string? kind) => kind switch
        {
            "character" => "Portrait",
            "character-plates" => "Book pictures",
            "stage1" => "Screenplay",
            "stage2" => "Shot plan",
            "video" or "clip" => "Clip",
            _ => string.IsNullOrWhiteSpace(kind) ? "Job" : kind,
        };


        /// <summary>Short operator line — prefer real error text when the job failed.</summary>
        internal static string FriendlyJobLine(JobSnapshot j)
        {
            if (string.Equals(j.Status, "error", StringComparison.OrdinalIgnoreCase))
                return FriendlyOperatorError(j);
            if (string.Equals(j.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                return "Cancelled.";
            if (string.Equals(j.Status, "done", StringComparison.OrdinalIgnoreCase))
                return "Done.";
            // Running / queued: prefer a short outcome phrase over raw Message (which may be technical)
            if (string.Equals(j.Kind, "character", StringComparison.OrdinalIgnoreCase))
                return "Creating portrait…";
            if (string.Equals(j.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
                return "Matching book pictures…";
            if (string.Equals(j.Kind, "book_import", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Kind, "book_prepare", StringComparison.OrdinalIgnoreCase))
            {
                var running = j.Message ?? "Importing book…";
                return Home.TrimOneLine(running, 160);
            }
            var msg = j.Message ?? "";
            return Home.TrimOneLine(msg, 120);
        }


        /// <summary>
        /// Operator-facing error. Prefer the job's Error/Message (what failed) with light cleanup,
        /// instead of a useless generic "Something went wrong."
        /// </summary>
        internal static string FriendlyOperatorError(JobSnapshot j)
        {
            var raw = Home.FirstNonEmpty(j.Error, j.Message);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cleaned = SanitizeOperatorError(raw);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return cleaned;
            }

            // Fallbacks only when the job carried no useful text
            if (string.Equals(j.Kind, "character", StringComparison.OrdinalIgnoreCase))
                return "Portrait generation failed. Check your image API key and try again.";
            if (string.Equals(j.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
                return "Could not match book pictures. Ensure the book was prepared and your AI provider is connected.";
            if (string.Equals(j.Kind, "book_import", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Kind, "book_prepare", StringComparison.OrdinalIgnoreCase))
                return "Story import failed. Check Configuration to ensure your AI provider is connected and try again.";
            if (string.Equals(j.Kind, "stage1", StringComparison.OrdinalIgnoreCase))
                return "Screenplay draft failed. Check your connected AI provider and story text.";
            if (string.Equals(j.Kind, "stage2", StringComparison.OrdinalIgnoreCase))
                return "Shot plan failed. Check your connected AI provider and screenplay.";
            return "Job failed. Open the project and check Configuration or job details.";
        }


        /// <summary>Strip stack/path noise; keep the actionable sentence.</summary>
        internal static string SanitizeOperatorError(string raw)
        {
            var s = FirstLineWithoutTypePrefix(raw);
            return RewriteKnownOperatorError(s) ?? Home.TrimOneLine(s, 280);
        }

        private static string FirstLineWithoutTypePrefix(string raw)
        {
            var s = raw.Replace("\r\n", "\n").Trim();
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[..nl].Trim();

            if (!s.StartsWith("System.", StringComparison.Ordinal))
                return s;
            var colon = s.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon < 80)
                s = s[(colon + 2)..].Trim();
            return s;
        }

        private static string? RewriteKnownOperatorError(string s)
        {
            if (ContainsAny(s, "XAI_API_KEY", "API key missing", "Connect your AI"))
                return "No AI provider connected. Open Configuration to connect your AI provider.";
            if (ContainsAny(s, "No page images", "Could not extract or render page images",
                    "Page render failed", "libpdfium", "libSkiaSharp", "DllNotFoundException"))
                return "Could not process PDF pages. Check your uploaded file format or try re-uploading.";
            if (s.Contains("No PDF and no book_full", StringComparison.OrdinalIgnoreCase))
                return "No story file found on the server. Re-upload the source file, then import again.";
            if (ContainsAny(s, "timeout", "timed out"))
                return "Story import timed out. Please try importing again.";
            if (ContainsAny(s, "could not be decrypted", "encryption keys changed", "DataProtector"))
                return "Saved credentials need to be re-entered. Open Configuration and save your settings again.";
            if (ContainsAny(s, "HTTP 401", "Unauthorized", "Incorrect API key"))
                return "Service connection rejected credentials (401). Open Configuration and save valid credentials.";
            if (ContainsAny(s, "HTTP 429", "rate limit"))
                return "Service rate limit hit. Please wait a minute and try again.";
            if (IsScreenplayBuildError(s))
                return "Screenplay generation failed after book text was ready. " + Home.TrimOneLine(s, 200);
            return null;
        }

        private static bool IsScreenplayBuildError(string s) =>
            s.Contains("Could not build a usable screenplay", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Book adapt", StringComparison.OrdinalIgnoreCase) ||
            (s.Contains("chunk", StringComparison.OrdinalIgnoreCase) && s.Contains("failed", StringComparison.OrdinalIgnoreCase));

        private static bool ContainsAny(string s, params string[] needles) =>
            needles.Any(n => s.Contains(n, StringComparison.OrdinalIgnoreCase));

    }
}
