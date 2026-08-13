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

public partial class Characters
{
    /// <summary>Jobs domain for the Characters page. Owns related UI state and behavior.</summary>
    public sealed class CharactersJobs
    {
        private readonly Characters S;
        public CharactersJobs(Characters host) => S = host;

        private const string JobStatusError = "error";
        private const string JobStatusCancelled = "cancelled";
        private const string JobStatusQueued = "queued";
        private const string JobStatusRunning = "running";

        internal JobSnapshot? _job;


        internal bool VoiceJobRunning =>
            _job is not null &&
            string.Equals(_job.Kind, "voice-preview", StringComparison.OrdinalIgnoreCase) &&
            (_job.Status is JobStatusRunning or JobStatusQueued) &&
            string.Equals(_job.CharKey, S.List._selectedKey, StringComparison.OrdinalIgnoreCase);


        internal bool JobRunning =>
            _job is not null &&
            (_job.Status is JobStatusRunning or JobStatusQueued);


        internal bool PlateSortRunning =>
            JobRunning &&
            string.Equals(_job?.Kind, "character-plates", StringComparison.OrdinalIgnoreCase);


        internal static string FriendlyCharacterJobStatus(JobSnapshot job)
        {
            var kind = job.Kind ?? "";
            if (string.Equals(kind, "cast-extract", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(job.Message)
                    ? "Building cast from screenplay…"
                    : job.Message;
            if (string.Equals(kind, "character-plates", StringComparison.OrdinalIgnoreCase))
                return job.Total > 0
                    ? $"Matching book pictures… ({job.Index} of {job.Total})"
                    : "Matching book pictures…";
            if (string.Equals(kind, "character", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "character_variants", StringComparison.OrdinalIgnoreCase))
                return job.Total > 0
                    ? $"Creating portrait… ({job.Index} of {job.Total})"
                    : "Creating portrait…";
            if (string.Equals(kind, "plan_looks", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(job.Message))
                    return job.Message;
                return job.Total > 0
                    ? $"Generating looks for plan… ({job.Index} of {job.Total})"
                    : "Generating looks for plan…";
            }
            if (string.Equals(kind, "voice-preview", StringComparison.OrdinalIgnoreCase))
                return "Generating voice sample…";
            return "Working…";
        }


        internal void OnJobUpdated(JobSnapshot snap)
        {
            // New job id → always take the snapshot (Index may be 0)
            // Same job → update as usual
            _job = snap;
            if (IsTerminalKind(snap, "voice-preview"))
                _ = S.InvokeAsync(() => HandleVoicePreviewJobAsync(snap));
            else if (IsTerminalKind(snap, "cast-extract"))
                _ = S.InvokeAsync(() => HandleCastExtractJobAsync(snap));
            else if (IsPlanLooksTerminal(snap))
                _ = S.InvokeAsync(() => HandlePlanLooksJobAsync(snap));
            else if (IsTerminalKind(snap, "character-plates"))
                _ = S.InvokeAsync(() => HandleCharacterPlatesJobAsync(snap));
            else if (IsTerminalKind(snap, "character"))
                _ = S.InvokeAsync(() => HandleCharacterJobAsync(snap));
            else
                _ = S.InvokeAsync(S.StateHasChanged);
        }

        private static bool IsTerminalKind(JobSnapshot snap, string kind)
            => (snap.Status is "done" or JobStatusError or JobStatusCancelled)
               && string.Equals(snap.Kind, kind, StringComparison.OrdinalIgnoreCase);

        private static bool IsPlanLooksTerminal(JobSnapshot snap)
            => (snap.Status is "done" or "partial" or JobStatusError or JobStatusCancelled)
               && string.Equals(snap.Kind, "plan_looks", StringComparison.OrdinalIgnoreCase);

        private string AdminOrOperatorError(JobSnapshot snap, string adminFallback, string operatorText)
            => S.Session.IsAdmin
                ? (snap.Error ?? snap.Message ?? adminFallback)
                : operatorText;

        private async Task HandleVoicePreviewJobAsync(JobSnapshot snap)
        {
            S.Voice._voicePreviewBusy = false;
            if (snap.Status == "done" &&
                string.Equals(snap.CharKey, S.List._selectedKey, StringComparison.OrdinalIgnoreCase))
            {
                S._error = null;
                S.Voice._voicePreviewError = null;
                S.Voice._voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                S.Voice._voicePreviewUrl = S.Engine.CharacterVoiceAudioUrl(
                    S._projectId, snap.CharKey, S.Voice._voiceAudioBust);
                S.Voice._voicePreviewStale = false;
                S.Voice._voicePreviewHint = "Film voice sample ready.";
                S._message = null;
            }
            else if (snap.Status == JobStatusError)
            {
                S._message = null;
                S.Voice._voicePreviewError = AdminOrOperatorError(
                    snap, "Voice sample failed.", "Could not generate voice sample. Try again.");
            }
            else if (snap.Status == JobStatusCancelled)
            {
                S.Voice._voicePreviewError = null;
                S.Voice._voicePreviewHint = "Voice sample cancelled.";
            }
            S.StateHasChanged();
            await Task.CompletedTask;
        }

        private async Task HandleCastExtractJobAsync(JobSnapshot snap)
        {
            S.List._extractingCast = false;
            S.List._rebuildCastHadExisting = false;
            S._busy = false;
            await S.List.LoadAsync();
            if (snap.Status == "done")
            {
                S._error = null;
                S._message = Characters.StripTrailingKeyDump(
                    snap.Message ?? "Cast ready — review looks, then lock portraits");
                S.List._lastCastExtractKeys = S.List._chars?
                    .Select(c => c.Key)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToList();
            }
            else if (snap.Status == JobStatusError)
            {
                S._message = null;
                S._error = AdminOrOperatorError(
                    snap, "Cast extract failed.", "Could not build cast. Try again.");
            }
            else
            {
                S._error = null;
                S._message = "Cast extract cancelled.";
            }
            S.StateHasChanged();
        }

        private async Task HandlePlanLooksJobAsync(JobSnapshot snap)
        {
            await S.List.LoadAsync();
            if (snap.Status is "done" or "partial")
            {
                S._error = null;
                S._message = snap.Message ?? "Plan looks ready — AI locked best picks (override any plate anytime).";
            }
            else if (snap.Status == JobStatusError)
            {
                S._message = null;
                S._error = AdminOrOperatorError(
                    snap, "Plan looks failed.", "Could not generate plan looks. Try again.");
            }
            else
            {
                S._error = null;
                S._message = "Plan looks cancelled.";
            }
            S.StateHasChanged();
        }

        private async Task HandleCharacterPlatesJobAsync(JobSnapshot snap)
        {
            await S.List.SoftReloadAsync();
            if (snap.Status == "done")
            {
                S._error = null;
                S._message = "Book pictures matched.";
            }
            else if (snap.Status == JobStatusError)
            {
                S._message = null;
                S._error = AdminOrOperatorError(
                    snap, "Could not match book pictures.", "Could not match book pictures. Try again.");
            }
            else if (snap.Status == JobStatusCancelled)
            {
                S._error = null;
                S._message = "Matching cancelled.";
            }
            S.StateHasChanged();
        }

        private async Task HandleCharacterJobAsync(JobSnapshot snap)
        {
            // Leave "Generating…" as soon as the job finishes (even if files need a moment)
            if (S.LookPipe._mode == Mode.WaitingGenerate)
                S.LookPipe._mode = Mode.PickSource;

            await S.List.SoftReloadAsync();
            if (snap.Status == "done" &&
                string.Equals(snap.CharKey, S.List._selectedKey, StringComparison.OrdinalIgnoreCase))
            {
                S._error = null;
                S._message = null;
                // Brief delay so variant files are visible after write/flush
                await Task.Delay(150);
                await S.List.SoftReloadAsync();
                S.LookPipe.BeginCompareFromVariants();
            }
            else if (snap.Status == JobStatusError)
            {
                S._message = null;
                S._error = AdminOrOperatorError(
                    snap, "Portrait generation failed.", "Portrait generation failed. Try again.");
                S.LookPipe._mode = Mode.PickSource;
            }
            else if (snap.Status == JobStatusCancelled)
            {
                S.LookPipe._mode = Mode.PickSource;
            }
            S.StateHasChanged();
        }


        internal void OnJobLog(string line)
        {
            if (_job is not null)
            {
                _job.Message = line;
                if (_job.Log.Count == 0 || _job.Log[^1] != line)
                {
                    _job.Log.Add(line);
                    if (_job.Log.Count > 80)
                        _job.Log = _job.Log.TakeLast(80).ToList();
                }
            }
            _ = S.InvokeAsync(S.StateHasChanged);
        }


        internal async Task CancelAsync()
        {
            // Always dismiss local UI; server cancel is best-effort (deploy/restart 502s).
            _ = await S.Engine.TryCancelJobAsync();
            var kind = _job?.Kind;
            _job = new JobSnapshot
            {
                Status = JobStatusCancelled,
                Kind = kind,
                Message = "Cancelled",
                CharKey = _job?.CharKey,
                ProjectId = _job?.ProjectId,
                Log = _job?.Log ?? new List<string>(),
                FinishedAt = DateTimeOffset.UtcNow,
            };
            if (S.LookPipe._mode == Mode.WaitingGenerate)
                S.LookPipe._mode = Mode.PickSource;
            S.List._extractingCast = false;
            S.List._rebuildCastHadExisting = false;
            S._busy = false;
            S._error = null;
            S._message = "Cancelled. You can try again when ready.";
            S.StateHasChanged();
        }

        internal async Task StartPlanLooksAsync()
        {
            if (string.IsNullOrWhiteSpace(S._projectId) || JobRunning) return;
            S._error = null;
            S._message = null;
            S._busy = true;
            try
            {
                await S.Engine.StartPlanLooksAsync(new StartPlanLooksRequest
                {
                    ProjectId = S._projectId,
                    Count = 3,
                    SkipAlreadyLocked = true,
                    IncludeCast = true,
                    IncludeLocations = true,
                });
                S._message = "Generating looks for plan cast + places…";
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
                S.StateHasChanged();
            }
        }


        internal async ValueTask DisposeAsyncCore()

        {
            S.Hub.JobUpdated -= OnJobUpdated;
            S.Hub.JobLog -= OnJobLog;
            await Task.CompletedTask;
        }

    }
}
