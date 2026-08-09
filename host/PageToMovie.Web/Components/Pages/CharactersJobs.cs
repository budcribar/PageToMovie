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
    internal sealed class CharactersJobs
    {
        private readonly Characters S;
        public CharactersJobs(Characters host) => S = host;

        internal JobSnapshot? _job;


        internal bool VoiceJobRunning =>
            _job is not null &&
            string.Equals(_job.Kind, "voice-preview", StringComparison.OrdinalIgnoreCase) &&
            (_job.Status is "running" or "queued") &&
            string.Equals(_job.CharKey, S._selectedKey, StringComparison.OrdinalIgnoreCase);


        internal bool JobRunning =>
            string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase);


        internal bool PlateSortRunning =>
            JobRunning &&
            string.Equals(_job?.Kind, "character-plates", StringComparison.OrdinalIgnoreCase);


        internal static string FriendlyCharacterJobStatus(JobSnapshot job)
        {
            var kind = job.Kind ?? "";
            if (string.Equals(kind, "character-plates", StringComparison.OrdinalIgnoreCase))
                return job.Total > 0
                    ? $"Matching book pictures… ({job.Index} of {job.Total})"
                    : "Matching book pictures…";
            if (string.Equals(kind, "character", StringComparison.OrdinalIgnoreCase))
                return "Creating portrait…";
            if (string.Equals(kind, "voice-preview", StringComparison.OrdinalIgnoreCase))
                return "Generating voice sample…";
            return "Working…";
        }


        internal void OnJobUpdated(JobSnapshot snap)
        {
            // New job id → always take the snapshot (Index may be 0)
            // Same job → update as usual
            _job = snap;
            if ((snap.Status is "done" or "error" or "cancelled") &&
                string.Equals(snap.Kind, "voice-preview", StringComparison.OrdinalIgnoreCase))
            {
                _ = S.InvokeAsync(async () =>
                {
                    S._voicePreviewBusy = false;
                    if (snap.Status == "done" &&
                        string.Equals(snap.CharKey, S._selectedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        S._error = null;
                        S._voicePreviewError = null;
                        S._voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        S._voicePreviewUrl = S.Engine.CharacterVoiceAudioUrl(
                            S._projectId, snap.CharKey!, S._voiceAudioBust);
                        S._voicePreviewStale = false;
                        S._voicePreviewHint = "Film voice sample ready.";
                        S._message = null;
                    }
                    else if (snap.Status == "error")
                    {
                        S._message = null;
                        S._voicePreviewError = S.Session.IsAdmin
                            ? (snap.Error ?? snap.Message ?? "Voice sample failed.")
                            : "Could not generate voice sample. Try again.";
                    }
                    else if (snap.Status == "cancelled")
                    {
                        S._voicePreviewError = null;
                        S._voicePreviewHint = "Voice sample cancelled.";
                    }
                    S.StateHasChanged();
                    await Task.CompletedTask;
                });
            }
            else if ((snap.Status is "done" or "error" or "cancelled") &&
                string.Equals(snap.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
            {
                _ = S.InvokeAsync(async () =>
                {
                    await S.SoftReloadAsync();
                    if (snap.Status == "done")
                    {
                        S._error = null;
                        S._message = "Book pictures matched.";
                    }
                    else if (snap.Status == "error")
                    {
                        S._message = null;
                        S._error = S.Session.IsAdmin
                            ? (snap.Error ?? snap.Message ?? "Could not match book pictures.")
                            : "Could not match book pictures. Try again.";
                    }
                    else if (snap.Status == "cancelled")
                    {
                        S._error = null;
                        S._message = "Matching cancelled.";
                    }
                    S.StateHasChanged();
                });
            }
            else if ((snap.Status is "done" or "error" or "cancelled") &&
                string.Equals(snap.Kind, "character", StringComparison.OrdinalIgnoreCase))
            {
                _ = S.InvokeAsync(async () =>
                {
                    // Leave "Generating…" as soon as the job finishes (even if files need a moment)
                    if (snap.Status is "done" or "error" or "cancelled")
                    {
                        if (S._mode == Mode.WaitingGenerate)
                            S._mode = Mode.PickSource;
                    }

                    await S.SoftReloadAsync();
                    if (snap.Status == "done" &&
                        string.Equals(snap.CharKey, S._selectedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        S._error = null;
                        S._message = null;
                        // Brief delay so variant files are visible after write/flush
                        await Task.Delay(150);
                        await S.SoftReloadAsync();
                        S.BeginCompareFromVariants();
                    }
                    else if (snap.Status == "error")
                    {
                        S._message = null;
                        S._error = S.Session.IsAdmin
                            ? (snap.Error ?? snap.Message ?? "Portrait generation failed.")
                            : "Portrait generation failed. Try again.";
                        S._mode = Mode.PickSource;
                    }
                    else if (snap.Status == "cancelled")
                    {
                        S._mode = Mode.PickSource;
                    }
                    S.StateHasChanged();
                });
            }
            else
            {
                _ = S.InvokeAsync(S.StateHasChanged);
            }
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
            S._busy = true;
            try
            {
                await S.Engine.CancelJobAsync();
                S._message = "Cancel requested";
                var jobs = await S.Engine.GetJobAsync();
                _job = jobs?.Job;
                if (S._mode == Mode.WaitingGenerate)
                    S._mode = Mode.PickSource;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async ValueTask DisposeAsyncCore()

        {
            S.Hub.JobUpdated -= OnJobUpdated;
            S.Hub.JobLog -= OnJobLog;
            await Task.CompletedTask;
        }

    }
}
