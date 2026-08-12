using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PageToMovie.Web.Components.Pages;

public abstract partial class AdaptationPageBase
{
    /// <summary>Job hub / poll / progress domain for adaptation step pages.</summary>
    public sealed class AdaptationJobs
    {
        private readonly AdaptationPageBase S;
        public AdaptationJobs(AdaptationPageBase host) => S = host;

        public JobSnapshot? Job;
        public int ProgressIndex;
        public int ProgressTotal;
        private CancellationTokenSource? _pollCts;
        /// <summary>True after user hits Cancel — waiters should exit even if the API is dead.</summary>
        public bool ClientCancelRequested { get; private set; }

        public bool JobRunning =>
            !ClientCancelRequested &&
            (string.Equals(Job?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Job?.Status, "queued", StringComparison.OrdinalIgnoreCase));

        public void OnJobUpdated(JobSnapshot snap)
        {
            if (ClientCancelRequested) return;
            Job = snap;
            AbsorbProgressFromSnapshot(snap);
            AbsorbProgressFromLine(snap.Message);
            if (snap.Status is "done" or "error" or "cancelled")
            {
                _pollCts?.Cancel();
                _pollCts?.Dispose();
                _pollCts = null;
                if (snap.Status == "done" && ProgressTotal > 0)
                    ProgressIndex = ProgressTotal;
                _ = S.InvokeAsync(async () =>
                {
                    await S.SoftLoadAsync();
                    try { await S.ActiveProject.RefreshReadinessAsync(S.Engine, _pollCts?.Token ?? CancellationToken.None); } catch { /* nav gates */ }
                    if (snap.Status == "done")
                    {
                        // Avoid flashing technical “Book ready · quality=good…” while Import
                        // continues into draft generation (Busy stays true).
                        if (!S.Busy)
                            S.Message = AdaptationStepUi.OperatorJobDoneMessage(snap);
                        try { await S.OnAdaptationJobTerminalAsync(snap); }
                        catch (Exception ex) { S.Error ??= ex.Message; }
                    }
                    else if (snap.Status == "error")
                        S.Error = snap.Error ?? snap.Message ?? "Job failed";
                    S.StateHasChanged();
                });
            }
            else
            {
                _ = S.InvokeAsync(S.StateHasChanged);
            }
        }

        public void OnJobLog(string line)
        {
            if (Job is null)
            {
                // Preserve phase Total when log arrives before full snapshot (avoid Total=0 → 35% bar).
                Job = new JobSnapshot
                {
                    Status = "running",
                    Message = line,
                    Log = new List<string> { line },
                    Index = ProgressIndex,
                    Total = ProgressTotal > 0 ? ProgressTotal : 10,
                };
            }
            else
            {
                Job.Message = line;
                if (Job.Log.Count == 0 || Job.Log[^1] != line)
                {
                    Job.Log.Add(line);
                    if (Job.Log.Count > 120)
                        Job.Log = Job.Log.TakeLast(120).ToList();
                }
            }
            AbsorbProgressFromLine(line);
            if (Job is not null)
            {
                // Always keep a positive Total for adapt jobs so the bar can move.
                if (Job.Total <= 0)
                    Job.Total = ProgressTotal > 0 ? ProgressTotal : 10;
                if (ProgressTotal > 0)
                    Job.Total = Math.Max(Job.Total, ProgressTotal);
                if (ProgressIndex > 0)
                    Job.Index = Math.Max(Job.Index, ProgressIndex);
            }
            _ = S.InvokeAsync(S.StateHasChanged);
        }

        public void AbsorbProgressFromSnapshot(JobSnapshot snap)
        {
            if (snap.Total > 0)
                ProgressTotal = Math.Max(ProgressTotal, snap.Total);
            if (snap.Index > 0)
                ProgressIndex = Math.Max(ProgressIndex, snap.Index);
            // Never let a live adapt job report Total=0 after we have phase scale.
            if (JobRunning && ProgressTotal <= 0 &&
                snap.Kind is "stage1" or "stage2" or "book_import" or "book_prepare" or "plan_looks")
                ProgressTotal = 10;
        }

        public void AbsorbProgressFromLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Prefer a fixed 10-step phase scale for screenplay/shot-plan jobs so the bar
            // moves on phase messages even when there are no "chunk i/N" lines (single-pass).
            ProgressTotal = Math.Max(ProgressTotal, 10);

            var mChunk = System.Text.RegularExpressions.Regex.Match(
                line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mChunk.Success &&
                int.TryParse(mChunk.Groups[1].Value, out var cIdx) &&
                int.TryParse(mChunk.Groups[2].Value, out var cTot) &&
                cTot > 0)
            {
                var done = line.Contains("done", StringComparison.OrdinalIgnoreCase);
                var frac = done
                    ? Math.Clamp((double)cIdx / cTot, 0, 1)
                    : Math.Clamp((cIdx - 1.0) / cTot, 0, 1);
                ProgressIndex = Math.Max(ProgressIndex, 4 + (int)Math.Round(4.0 * frac));
                return;
            }

            var mVis = System.Text.RegularExpressions.Regex.Match(
                line, @"(?:Grok vision|Reading page|page)\s+(\d+)\s*/\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mVis.Success &&
                int.TryParse(mVis.Groups[1].Value, out var vIdx) &&
                int.TryParse(mVis.Groups[2].Value, out var vTot) &&
                vTot > 0)
            {
                var frac = Math.Clamp((vIdx - 1.0) / vTot, 0, 1);
                ProgressIndex = Math.Max(ProgressIndex, 1 + (int)Math.Round(2.0 * frac));
                return;
            }

            if (line.Contains("Screenplay ready", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Stage 2 complete", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("shot plan ready", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 10);
            else if (line.Contains("approving", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Fountain draft saved", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("plate", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Attaching", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Merged", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 9);
            else if (line.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Stitch", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 8);
            else if (line.Contains("repair", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("retry", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 7);
            else if (line.Contains("single pass", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Adapting", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Book split", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Writing screenplay", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Drafting", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 5);
            else if (line.Contains("Target runtime", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Planning", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("building", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 3);
            else if (line.Contains("prepare", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Checking book", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("book text", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Loading screenplay", StringComparison.OrdinalIgnoreCase))
                ProgressIndex = Math.Max(ProgressIndex, 1);
        }

        public void StartJobPolling()
        {
            if (ClientCancelRequested) return;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;
            var trackedId = Job?.JobId;
            var emptyOrGoneHits = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && !ClientCancelRequested)
                    {
                        await Task.Delay(1500, ct);
                        if (ClientCancelRequested) break;
                        try
                        {
                            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            pollCts.CancelAfter(TimeSpan.FromSeconds(4));

                            JobSnapshot? snap = null;
                            // Prefer the job we started so we don't stick to a ghost "running" row
                            // after the server process died (Admin shows no active jobs).
                            if (!string.IsNullOrWhiteSpace(trackedId))
                            {
                                try
                                {
                                    snap = await S.Engine.TryGetJobAsync(trackedId, pollCts.Token);
                                }
                                catch { /* fall through to list */ }
                            }
                            if (snap is null)
                            {
                                var jobs = await S.Engine.GetJobAsync(pollCts.Token);
                                snap = jobs?.Job;
                            }

                            if (ClientCancelRequested) break;

                            // Server has nothing running for us — drop zombie progress UI.
                            if (snap is null
                                || (JobRunning
                                    && snap.IsFinished
                                    && !string.IsNullOrWhiteSpace(trackedId)
                                    && !string.Equals(snap.JobId, trackedId, StringComparison.OrdinalIgnoreCase)))
                            {
                                emptyOrGoneHits++;
                                if (JobRunning && emptyOrGoneHits >= 3)
                                {
                                    await S.InvokeAsync(() =>
                                    {
                                        MarkJobLostOnServer(
                                            "The write job is no longer on the server (likely a restart during the long AI call). "
                                            + "Nothing is running in Admin → Jobs — try Create draft from book again.");
                                    });
                                    break;
                                }
                                continue;
                            }

                            emptyOrGoneHits = 0;
                            if (string.IsNullOrWhiteSpace(trackedId) && !string.IsNullOrWhiteSpace(snap.JobId))
                                trackedId = snap.JobId;

                            await S.InvokeAsync(() =>
                            {
                                if (ClientCancelRequested) return;
                                Job = snap;
                                AbsorbProgressFromSnapshot(snap);
                                AbsorbProgressFromLine(snap.Message);
                                if (Job is not null && ProgressTotal > 0)
                                {
                                    Job.Index = Math.Max(Job.Index, ProgressIndex);
                                    Job.Total = Math.Max(Job.Total, ProgressTotal);
                                }
                                if (S is AdaptationImport importPage && JobRunning)
                                {
                                    importPage.Drop._importing = true;
                                    if (string.IsNullOrWhiteSpace(importPage.Drop._importStatus)
                                        || importPage.Drop._importStatus == "Cancelled")
                                        importPage.Drop._importStatus =
                                            snap.Message ?? "Job still running on the server…";
                                }
                                S.StateHasChanged();
                            });
                            if (snap.IsFinished)
                            {
                                if (snap.Status is "done" or "error" or "cancelled" or "partial")
                                    await S.InvokeAsync(async () =>
                                    {
                                        if (S is AdaptationImport importPage)
                                        {
                                            importPage.Drop._importing = false;
                                            importPage.Drop._importPct = null;
                                        }
                                        // Unstick Create-from-book waiters that key off Busy.
                                        if (S.Busy && snap.Status is "error" or "cancelled")
                                        {
                                            S.Busy = false;
                                            S.BusyMessage = null;
                                            S.Error = snap.Error ?? snap.Message;
                                        }
                                        else if (S.Busy && snap.Status is "done" or "partial")
                                        {
                                            // CreateFromBookAsync loop will SoftLoad; still drop Busy if orphaned.
                                        }
                                        await S.SoftLoadAsync();
                                        if (snap.Status == "done")
                                        {
                                            try { await S.OnAdaptationJobTerminalAsync(snap); }
                                            catch (Exception ex) { S.Error ??= ex.Message; }
                                        }
                                        else if (snap.Status == "error")
                                            S.Error = snap.Error ?? snap.Message ?? "Job failed";
                                        S.StateHasChanged();
                                    });
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested || ClientCancelRequested)
                        {
                            break;
                        }
                        catch
                        {
                            // transient 502 during deploy — keep polling until user cancels
                        }
                    }
                }
                catch (OperationCanceledException) { /* expected */ }
                catch { /* ignore poll errors */ }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Drop local running UI when Admin has no active job (process restart / lost job).
        /// </summary>
        public void MarkJobLostOnServer(string message)
        {
            DisposePolling();
            Job = new JobSnapshot
            {
                JobId = Job?.JobId,
                Status = "error",
                Kind = Job?.Kind,
                Message = message,
                Error = message,
                ProjectId = Job?.ProjectId ?? S.ProjectId,
                Index = Job?.Index ?? ProgressIndex,
                Total = Job?.Total > 0 ? Job.Total : ProgressTotal,
                Log = Job?.Log ?? new List<string>(),
                FinishedAt = DateTimeOffset.UtcNow,
            };
            ProgressIndex = 0;
            ProgressTotal = 0;
            S.Busy = false;
            S.BusyMessage = null;
            S.Error = message;
            if (S is AdaptationImport importPage)
            {
                importPage.Drop._importing = false;
                importPage.Drop._importPct = null;
                importPage.Drop._importStatus = "Failed";
            }
            S.StateHasChanged();
        }

        /// <summary>
        /// Browser exit does not cancel server jobs. On page load / re-login, reattach progress
        /// UI and polling so a still-running import is visible and cancellable again.
        /// Only reattach when the server still reports the job as running/queued.
        /// </summary>
        public void TryReattachRunningJob()
        {
            if (ClientCancelRequested || Job is null || !JobRunning)
                return;
            // Don't reattach a ghost — verify asynchronously then either poll or clear.
            var id = Job.JobId;
            _ = Task.Run(async () =>
            {
                try
                {
                    JobSnapshot? live = null;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        try
                        {
                            live = await S.Engine.TryGetJobAsync(id, _pollCts?.Token ?? CancellationToken.None);
                        }
                        catch { /* list fallback */ }
                    }
                    if (live is null)
                    {
                        var list = await S.Engine.GetJobAsync(_pollCts?.Token ?? CancellationToken.None);
                        live = list?.Job;
                    }

                    await S.InvokeAsync(() =>
                    {
                        if (live is null || live.IsFinished
                            || (live.Status is not ("running" or "queued")))
                        {
                            // Stale client snapshot after server restart — do not show Writing 6/10.
                            if (JobRunning)
                            {
                                Job = live is { IsFinished: true } ? live : null;
                                ProgressIndex = 0;
                                ProgressTotal = 0;
                                S.Busy = false;
                                S.BusyMessage = null;
                                if (S is AdaptationImport importPage)
                                {
                                    importPage.Drop._importing = false;
                                    importPage.Drop._importPct = null;
                                }
                            }
                            S.StateHasChanged();
                            return;
                        }

                        Job = live;
                        AbsorbProgressFromSnapshot(live);
                        AbsorbProgressFromLine(live.Message);
                        if (S is AdaptationImport importPage2)
                        {
                            importPage2.Drop._importing = true;
                            if (string.IsNullOrWhiteSpace(importPage2.Drop._importStatus))
                                importPage2.Drop._importStatus =
                                    live.Message ?? "Resumed — job still running on the server…";
                            if (string.IsNullOrWhiteSpace(importPage2.Drop._chosenFileName)
                                && !string.IsNullOrWhiteSpace(live.Kind))
                                importPage2.Drop._chosenFileName = live.Kind;
                        }
                        StartJobPolling();
                        S.StateHasChanged();
                    });
                }
                catch
                {
                    // Leave UI alone if we can't reach the API yet.
                }
            });
        }

        /// <summary>
        /// Always dismisses the local import/job UI. Best-effort server cancel;
        /// if the API is down (deploy/restart), the user is not stuck.
        /// </summary>
        public async Task CancelAsync()
        {
            ClientCancelRequested = true;
            DisposePolling();

            // Best-effort server cancel — short timeout so a dead host cannot pin Cancel.
            _ = await S.Engine.TryCancelJobAsync(CancellationToken.None);

            Job = new JobSnapshot
            {
                Status = "cancelled",
                Kind = Job?.Kind,
                Message = "Cancelled",
                ProjectId = Job?.ProjectId,
                Log = Job?.Log ?? new List<string>(),
                FinishedAt = DateTimeOffset.UtcNow,
            };
            ProgressIndex = 0;
            ProgressTotal = 0;
            S.Busy = false;
            S.Error = null;
            S.Message = "Import cancelled. You can start again when ready.";
            // Import page locks drop zone with _importing — clear it when present.
            if (S is AdaptationImport importPage)
            {
                importPage.Drop._importing = false;
                importPage.Drop._importPct = null;
                importPage.Drop._importStatus = "Cancelled";
            }
            S.StateHasChanged();
            await Task.CompletedTask;
        }

        /// <summary>Call when starting a new import so a prior Cancel does not block.</summary>
        public void ResetClientCancel()
        {
            ClientCancelRequested = false;
        }

        public Task EnsureHubAsync() => S.Hub.EnsureStartedAsync();

        public void DisposePolling()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        public static bool IsJobInFlightMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            return message.Contains("waiting", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("calling", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("parsing", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Grok vision", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Reading page", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("single pass", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Adapting", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Writing screenplay", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Drafting", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Operator-facing live job line (no provider / path / mechanism jargon).
        /// </summary>
        public static string OperatorJobRunningMessage(JobSnapshot snap)
        {
            if (string.Equals(snap.Status, "queued", StringComparison.OrdinalIgnoreCase))
                return "Waiting…";

            var msg = snap.Message ?? "";
            var kind = snap.Kind ?? "";

            var page = System.Text.RegularExpressions.Regex.Match(
                msg, @"page\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (page.Success)
                return $"Reading book — page {page.Groups[1].Value} of {page.Groups[2].Value}";

            var chunk = System.Text.RegularExpressions.Regex.Match(
                msg, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (chunk.Success)
                return $"Writing screenplay — part {chunk.Groups[1].Value} of {chunk.Groups[2].Value}";

            var sceneOf = System.Text.RegularExpressions.Regex.Match(
                msg, @"Scene\s+(\d+)\s+of\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (sceneOf.Success && kind is "stage2")
            {
                var a = sceneOf.Groups[1].Value;
                var b = sceneOf.Groups[2].Value;
                if (int.TryParse(a, out var sa) && int.TryParse(b, out var sb) && sb > 0)
                {
                    var pct = (int)Math.Round(100.0 * Math.Max(0, sa - 1) / sb);
                    return $"Planning shots — scene {a} of {b} ({pct}%)";
                }
                return $"Planning shots — scene {a} of {b}";
            }

            var scenesDone = System.Text.RegularExpressions.Regex.Match(
                msg, @"Planning scenes:\s*(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (scenesDone.Success && kind is "stage2")
            {
                var a = scenesDone.Groups[1].Value;
                var b = scenesDone.Groups[2].Value;
                if (int.TryParse(a, out var da) && int.TryParse(b, out var db) && db > 0)
                {
                    var pct = (int)Math.Round(100.0 * da / db);
                    return $"Planning shots — {a} of {b} scenes done ({pct}%)";
                }
            }

            var scene = System.Text.RegularExpressions.Regex.Match(
                msg, @"Scene\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (scene.Success && kind is "stage2")
            {
                if (snap.Total > 0)
                {
                    var idx = Math.Max(snap.Index, 0);
                    var pct = (int)Math.Round(100.0 * idx / snap.Total);
                    return $"Planning shots — scene {scene.Groups[1].Value} of {snap.Total} ({pct}%)";
                }
                return $"Planning shots — scene {scene.Groups[1].Value}";
            }

            if (msg.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Stitch", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Combining", StringComparison.OrdinalIgnoreCase))
                return "Combining screenplay parts…";
            if (msg.Contains("repair", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Refin", StringComparison.OrdinalIgnoreCase))
                return "Refining screenplay…";
            if (msg.Contains("approving", StringComparison.OrdinalIgnoreCase))
                return "Approving screenplay…";
            if (msg.Contains("plate", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Attaching", StringComparison.OrdinalIgnoreCase))
                return "Matching book pictures…";
            if (msg.Contains("Adapting", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("single pass", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Fountain", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("screenplay", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Phase 2", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Writing", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Drafting", StringComparison.OrdinalIgnoreCase) ||
                kind is "stage1" or "book_import")
            {
                if (kind is "stage2")
                    return "Building shot plan…";
                return "Writing screenplay…";
            }
            if (msg.Contains("Planning", StringComparison.OrdinalIgnoreCase) || kind is "stage2")
                return "Building shot plan…";
            if (msg.Contains("extract", StringComparison.OrdinalIgnoreCase))
                return "Extracting text from the book…";
            if (msg.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("OCR", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Reading", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("prepare", StringComparison.OrdinalIgnoreCase) ||
                kind is "book_prepare")
                return "Reading book…";
            if (string.IsNullOrWhiteSpace(msg))
                return kind switch
                {
                    "stage2" => "Building shot plan…",
                    "stage1" or "book_import" => "Writing screenplay…",
                    _ => "Working…",
                };
            // Last resort: short clean message, never dump long engine lines
            return msg.Length > 80 ? msg[..77] + "…" : msg;
        }
    }
}
