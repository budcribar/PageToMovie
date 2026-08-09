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

        internal void OnDragEnter(DragEventArgs e)
        {
            if (_importing || S.Busy || S.JobRunning || !S.Gate.ImportReady) return;
            _dragOver = true;
        }

        internal void OnDragOver(DragEventArgs e)
        {
            if (_importing || S.Busy || S.JobRunning || !S.Gate.ImportReady) return;
            _dragOver = true;
        }

        internal void OnDragLeave(DragEventArgs e) => _dragOver = false;

        internal void OnDrop(DragEventArgs e)
        {
            // preventDefault (markup) stops the browser from opening the dropped .txt as a navigation.
            // JS (ptmImportDrop) assigns the File to the InputFile and fires change → OnSourceSelectedAsync.
            _dragOver = false;
        }

        internal async Task OnAfterRenderAsync(bool firstRender)
        {
            if (_importing || S.Busy || S.JobRunning || !S.Gate.ImportReady) return;
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
                await using var stream = file.OpenReadStream(maxBook);
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

                    await S.EnsureHubAsync();
                    await S.Engine.StartBookImportAsync(
                        S.ProjectId,
                        skipPrepare: IsTxtName(name), // upload already wrote book text for plain .txt
                        forceExtract: IsPdfName(name),
                        forceVision: false,
                        autoVision: true,
                        model: S.Model);

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
            var sawRunning = false;

            // Long novels: multi-chunk adapt can run 30–60+ minutes
            for (var i = 0; i < 3600; i++)
            {
                try
                {
                    var jobs = await S.Engine.GetJobAsync();
                    var snap = jobs?.Job;
                    if (snap is not null)
                    {
                        S.Job = snap;
                        S.AbsorbProgressFromSnapshot(snap);
                        S.AbsorbProgressFromLine(snap.Message);

                        _importStatus = FriendlyJobStatus(snap);

                        // Prefer engine Index/Total (phase scale); soft-crawl when quiet mid-adapt.
                        var (_, tot, waiting, displayIdx) = AdaptationPageBase.ComputeJobProgress(
                            snap, S.ProgressIndex, S.ProgressTotal, jobRunning: true);
                        var pctWithin = AdaptationPageBase.ComputeProgressPercent(
                            displayIdx, tot > 0 ? tot : 10, waiting, jobRunning: true, snap.StartedAt);
                        var mapped = basePct + (int)Math.Round(spanPct * (pctWithin / 100.0));
                        var lo = basePct;
                        var hi = basePct + spanPct - 1;
                        _importPct = mapped < lo ? lo : mapped > hi ? hi : mapped;

                        await S.InvokeAsync(S.StateHasChanged);

                        var st = snap.Status ?? "";
                        var kindOk = string.IsNullOrEmpty(snap.Kind) ||
                                     string.Equals(snap.Kind, expectedKind, StringComparison.OrdinalIgnoreCase);

                        if (st is "running" or "queued")
                            sawRunning = true;

                        if (st is "error" or "cancelled")
                        {
                            S.Error = FriendlyError(snap.Error ?? snap.Message ?? "Could not import the book");
                            return false;
                        }

                        if (st == "done" && kindOk && (sawRunning || i >= 2))
                            return true;
                    }
                }
                catch
                {
                    // keep polling
                }

                await Task.Delay(1000);
            }

            S.Error = "Timed out while importing the book.";
            return false;
        }

        /// <summary>Operator-facing status (no mechanism jargon). Admins still see raw log below.</summary>
        internal static string FriendlyJobStatus(JobSnapshot snap) =>
            AdaptationPageBase.OperatorJobRunningMessage(snap);

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
