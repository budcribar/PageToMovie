using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport
{
    /// <summary>Drag/drop + file pick + import pipeline for Adaptation Import.</summary>
    internal sealed class ImportDrop
    {
        private readonly AdaptationImport S;
        public ImportDrop(AdaptationImport host) => S = host;

        internal bool _importing;
        internal string _importStatus = "";
        internal int? _importPct;
        internal string? _chosenFileName;
        internal bool _dragOver;
        /// <summary>Bumped after each selection so InputFile remounts cleanly.</summary>
        internal int _inputFileKey;

        private const string ImportCancelledMessage = "Import cancelled. You can start again when ready.";

        internal void OnDragEnter(DragEventArgs e)
        {
            if (_importing || S.Busy || S.Jobs.JobRunning || !S.Gate.ImportReady) return;
            _dragOver = true;
        }

        internal void OnDragOver(DragEventArgs e) => OnDragEnter(e);

        internal void OnDragLeave(DragEventArgs e) => _dragOver = false;

        internal void OnDrop(DragEventArgs e)
        {
            // preventDefault (markup) stops the browser from opening the dropped .txt as a navigation.
            // JS (ptmImportDrop) assigns the File to the InputFile and fires change → OnSourceSelectedAsync.
            _dragOver = false;
        }

        internal async Task OnAfterRenderAsync(bool firstRender)
        {
            if (_importing || S.Busy || S.Jobs.JobRunning || !S.Gate.ImportReady) return;
            try
            {
                // Re-bind after each InputFile remount (@key) so drop keeps working.
                await S.Js.InvokeVoidAsync("ptmImportDrop.attachBySelector", "[data-testid=import-dropzone]");
            }
            catch
            {
                // script not loaded yet — next render retries
            }
        }

        internal async Task OnSourceSelectedAsync(InputFileChangeEventArgs e)
        {
            var file = e.File;
            if (file is null || _importing) return;
            if (!S.Session.IsLoggedIn)
            {
                S.Error = "Sign in required to import books.";
                S.Nav.NavigateTo("/login?returnUrl=/adaptation/import");
                return;
            }
            // Fresh status (Settings may have just been saved)
            try { await S.LoadAsync(); } catch { /* keep previous */ }
            S.Gate.RefreshImportGate();
            if (!S.Gate.ImportReady)
            {
                S.Error = S.Gate._importBlockedReason;
                _inputFileKey++;
                return;
            }

            // CRITICAL: read the browser file into memory BEFORE any re-render that unmounts
            // InputFile (progress UI). Opening the stream after unmount throws:
            //   Cannot read properties of null (reading '_blazorFilesById')
            byte[] bytes;
            string name;
            try
            {
                name = file.Name;
                _chosenFileName = name;
                _dragOver = false;
                const long maxBook = 80 * 1024 * 1024;
                if (file.Size > maxBook)
                {
                    S.Error = $"File too large ({file.Size / (1024.0 * 1024):F0} MB). Max is 80 MB.";
                    _inputFileKey++;
                    return;
                }
                await using var stream = file.OpenReadStream(maxAllowedSize: maxBook);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                S.Error = FriendlyError(ex.Message);
                _inputFileKey++; // remount so the next pick works
                return;
            }

            _inputFileKey++; // remount InputFile for a clean next selection
            await ImportBufferedAsync(name, bytes);
        }

        internal async Task ImportBufferedAsync(string name, byte[] bytes)
        {
            if (!S.Session.IsLoggedIn)
            {
                S.Error = "Sign in required to import books.";
                return;
            }
            if (bytes.Length == 0)
            {
                S.Error = "That file is empty. Pick a PDF, text, or fountain file with content.";
                return;
            }

            _importing = true;
            S.Busy = true;
            S.Error = null;
            S.Message = null; // clear stale “Book ready” / Next guidance during pipeline
            S.Jobs.ResetClientCancel();
            _chosenFileName = name;
            _importPct = 8;
            _importStatus = $"Reading {name}…";
            S.StateHasChanged();

            try
            {
                if (IsFountainName(name))
                {
                    _importStatus = "Loading screenplay…";
                    _importPct = 40;
                    S.StateHasChanged();

                    await using var stream = new MemoryStream(bytes, writable: false);
                    await S.Engine.ImportFountainAsync(S.ProjectId, name, stream);

                    _importPct = 100;
                    _importStatus = "Done";
                    await S.LoadAsync();
                    S.Nav.NavigateTo("adaptation/screenplay");
                    return;
                }

                // PDF or TXT
                _importStatus = "Saving file…";
                _importPct = 15;
                S.StateHasChanged();

                await using (var stream = new MemoryStream(bytes, writable: false))
                {
                    await S.Engine.UploadBookAsync(S.ProjectId, name, stream);
                }

                if (IsPdfName(name) || IsTxtName(name))
                {
                    // One job: prepare (PDF/OCR) + book→Fountain (long books need background lifetime)
                    S.Message = null;
                    _importStatus = IsPdfName(name) ? "Reading book…" : "Writing screenplay…";
                    _importPct = 20;
                    S.StateHasChanged();

                    await S.Jobs.EnsureHubAsync();
                    await S.Engine.StartBookImportAsync(
                        S.ProjectId,
                        skipPrepare: IsTxtName(name), // upload already wrote book text for plain .txt
                        forceExtract: IsPdfName(name),
                        forceVision: false,
                        autoVision: true,
                        model: S.Pipeline.Model);

                    var ok = await WaitForJobDoneAsync(
                        "book_import",
                        basePct: 20,
                        spanPct: 75);
                    if (!ok)
                        return;
                }
                else
                {
                    throw new InvalidOperationException("Use a screenplay (.fountain), PDF, or .txt file.");
                }

                _importPct = 100;
                _importStatus = "Done";
                S.StateHasChanged();
                await S.LoadAsync();
                S.Nav.NavigateTo("adaptation/screenplay");
            }
            catch (Exception ex)
            {
                S.Error = FriendlyError(ex.Message);
                _importStatus = "Failed";
                _importPct = null;
            }
            finally
            {
                _importing = false;
                S.Busy = false;
            }
        }

        /// <summary>Poll until the current job finishes. Returns false on error/cancel.</summary>
        internal async Task<bool> WaitForJobDoneAsync(
            string expectedKind,
            int basePct,
            int spanPct)
        {
            await Task.Delay(400);
            var state = new JobPollState();

            // Long novels: multi-chunk adapt can run 30–60+ minutes
            for (var i = 0; i < 3600; i++)
            {
                if (IsCancel()) return false;

                try
                {
                    var result = await TryHandleJobPollAsync(expectedKind, basePct, spanPct, i, state);
                    if (result is { } done)
                        return done;
                }
                catch
                {
                    // 502 during deploy — keep waiting, but honor Cancel immediately.
                    if (IsCancel()) return false;
                }

                await Task.Delay(1000);
            }

            S.Error = "Timed out while importing the book.";
            return false;
        }

        private sealed class JobPollState
        {
            public bool SawRunning;
        }

        private bool IsCancel(bool jobCancelled = false)
        {
            if (!jobCancelled && !S.Jobs.ClientCancelRequested)
                return false;
            S.Message = ImportCancelledMessage;
            S.Error = null;
            return true;
        }

        /// <summary>One poll tick. Null = keep looping; true/false = job finished.</summary>
        private async Task<bool?> TryHandleJobPollAsync(
            string expectedKind,
            int basePct,
            int spanPct,
            int iteration,
            JobPollState state)
        {
            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var jobs = await S.Engine.GetJobAsync(pollCts.Token);
            if (IsCancel()) return false;

            var snap = jobs?.Job;
            if (snap is null) return null;

            ApplyImportProgressFromSnapshot(snap, basePct, spanPct);
            await S.InvokeAsync(S.StateHasChanged);
            return HandleJobPollStatus(snap, expectedKind, iteration, state);
        }

        private void ApplyImportProgressFromSnapshot(JobSnapshot snap, int basePct, int spanPct)
        {
            S.Jobs.Job = snap;
            S.Jobs.AbsorbProgressFromSnapshot(snap);
            S.Jobs.AbsorbProgressFromLine(snap.Message);

            _importStatus = FriendlyJobStatus(snap);

            // Prefer engine Index/Total (phase scale); soft-crawl when quiet mid-adapt.
            var (_, tot, waiting, displayIdx) = AdaptationPageBase.AdaptationStepUi.ComputeJobProgress(
                snap, S.Jobs.ProgressIndex, S.Jobs.ProgressTotal, jobRunning: true);
            var pctWithin = AdaptationPageBase.AdaptationStepUi.ComputeProgressPercent(
                displayIdx, JobProgressDenom(tot), waiting, jobRunning: true, snap.StartedAt);
            var mapped = basePct + (int)Math.Round(spanPct * (pctWithin / 100.0));
            _importPct = ClampImportPct(mapped, basePct, basePct + spanPct - 1);
        }

        private static int JobProgressDenom(int tot) => tot > 0 ? tot : 10;

        private static int ClampImportPct(int mapped, int lo, int hi) =>
            mapped < lo ? lo : mapped > hi ? hi : mapped;

        private bool? HandleJobPollStatus(
            JobSnapshot snap,
            string expectedKind,
            int iteration,
            JobPollState state)
        {
            var st = snap.Status ?? "";
            var kindOk = string.IsNullOrEmpty(snap.Kind) ||
                         string.Equals(snap.Kind, expectedKind, StringComparison.OrdinalIgnoreCase);

            if (st is "running" or "queued")
                state.SawRunning = true;

            if (st is "error" or "cancelled")
                return HandleJobPollFailure(snap, st);

            if (IsJobPollDone(st, kindOk, state.SawRunning, iteration))
                return true;

            return null;
        }

        private static bool IsJobPollDone(string st, bool kindOk, bool sawRunning, int iteration) =>
            st == "done" && kindOk && (sawRunning || iteration >= 2);

        private bool? HandleJobPollFailure(JobSnapshot snap, string st)
        {
            if (IsCancel(jobCancelled: st == "cancelled"))
                return false;
            S.Error = FriendlyError(snap.Error ?? snap.Message ?? "Could not import the book");
            return false;
        }

        /// <summary>Operator-facing status (no mechanism jargon). Admins still see raw log below.</summary>
        internal static string FriendlyJobStatus(JobSnapshot snap) =>
            AdaptationPageBase.AdaptationJobs.OperatorJobRunningMessage(snap);

        internal static string FriendlyError(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Story import failed. Check Configuration to ensure your AI provider is connected and try uploading again.";
            var s = raw.Replace("\r\n", "\n").Trim();
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[..nl].Trim();
            if (s.StartsWith("System.", StringComparison.Ordinal))
            {
                var colon = s.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && colon < 80)
                    s = s[(colon + 2)..].Trim();
            }
            if (s.Contains("XAI_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("API key missing", StringComparison.OrdinalIgnoreCase))
                return "No AI provider connected. Open Configuration to connect your AI provider.";
            if (s.Contains("No page images", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Could not extract or render page images", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("libpdfium", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("DllNotFoundException", StringComparison.OrdinalIgnoreCase))
                return "Could not process PDF pages. Please check the file format and try again.";
            if (s.Length > 280) s = s[..280] + "…";
            return s;
        }

        internal static bool IsFountainName(string? name) =>
            name is not null &&
            (name.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase));

        internal static bool IsPdfName(string? name) =>
            name is not null && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        internal static bool IsTxtName(string? name) =>
            name is not null && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
