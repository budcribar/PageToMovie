using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport
{

    public override string StepKey => "import";

    private bool _importing;
    private string _importStatus = "";
    private int? _importPct;
    private string? _chosenFileName;
    private bool _dragOver;
    /// <summary>Bumped after each selection so InputFile remounts cleanly.</summary>
    private int _inputFileKey;
    private string _importBlockedReason = "Choose a Script & planning model in Settings for this project.";
    private int _targetMinutesEdit = 5;
    private bool _savingRuntime;
    private string? _runtimeMessage;

    /// <summary>True when this project has a planning model and a usable AI key.</summary>
    private bool ImportReady =>
        Status is not null
        && Status.XaiConfigured
        && IsUsablePlanningModel(Status.PlanningModel);

    private static bool IsUsablePlanningModel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var s = id.Trim();
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private bool CanContinueToScreenplay =>
        Status is not null &&
        (Status.Screenplay.DraftExists ||
         (Status.Stage1.Present && Status.Stage1.SceneCount > 0));

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
        if (!Session.IsLoggedIn)
        {
            Error = "Sign in required to import books.";
            Nav.NavigateTo("/login?returnUrl=/adaptation/import");
            return;
        }
        // Re-load status so PlanningModel reflects Settings saved just before navigating here.
        try { await LoadAsync(); } catch { /* base already tried */ }
        SyncTargetEditFromStatus();
        RefreshImportGate();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        RefreshImportGate();
    }

    private void RefreshImportGate()
    {
        if (Status is null)
        {
            _importBlockedReason = "Loading project…";
            return;
        }
        if (!Status.XaiConfigured)
        {
            _importBlockedReason =
                "No AI key on this account. Open Settings and add a key for Script & planning (xAI, OpenAI, Anthropic, Google, …).";
            return;
        }
        if (string.IsNullOrWhiteSpace(Status.PlanningModel) || !IsUsablePlanningModel(Status.PlanningModel))
        {
            _importBlockedReason =
                "Script & planning: no model selected for this project. Open Settings → Studio coverage → Script & planning, choose a model, then come back here.";
            return;
        }
        _importBlockedReason = "";
        // Keep base Model in sync so StartBookImportAsync sends the chosen id.
        if (IsUsablePlanningModel(Status.PlanningModel))
            Model = Status.PlanningModel;
    }

    private void OnDragEnter(DragEventArgs e)
    {
        if (_importing || Busy || JobRunning || !ImportReady) return;
        _dragOver = true;
    }

    private void OnDragOver(DragEventArgs e)
    {
        if (_importing || Busy || JobRunning || !ImportReady) return;
        _dragOver = true;
    }

    private void OnDragLeave(DragEventArgs e) => _dragOver = false;

    private void OnDrop(DragEventArgs e)
    {
        // preventDefault (markup) stops the browser from opening the dropped .txt as a navigation.
        // JS (ptmImportDrop) assigns the File to the InputFile and fires change → OnSourceSelectedAsync.
        _dragOver = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (_importing || Busy || JobRunning || !ImportReady) return;
        try
        {
            // Re-bind after each InputFile remount (@key) so drop keeps working.
            await Js.InvokeVoidAsync("ptmImportDrop.attachBySelector", "[data-testid=import-dropzone]");
        }
        catch
        {
            // script not loaded yet — next render retries
        }
    }

    private async Task OnSourceSelectedAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null || _importing) return;
        if (!Session.IsLoggedIn)
        {
            Error = "Sign in required to import books.";
            Nav.NavigateTo("/login?returnUrl=/adaptation/import");
            return;
        }
        // Fresh status (Settings may have just been saved)
        try { await LoadAsync(); } catch { /* keep previous */ }
        RefreshImportGate();
        if (!ImportReady)
        {
            Error = _importBlockedReason;
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
            Error = FriendlyError(ex.Message);
            _inputFileKey++; // remount so the next pick works
            return;
        }

        _inputFileKey++; // remount InputFile for a clean next selection
        await ImportBufferedAsync(name, bytes);
    }

    private async Task ImportBufferedAsync(string name, byte[] bytes)
    {
        if (!Session.IsLoggedIn)
        {
            Error = "Sign in required to import books.";
            return;
        }
        if (bytes.Length == 0)
        {
            Error = "That file is empty. Pick a PDF, text, or fountain file with content.";
            return;
        }

        _importing = true;
        Busy = true;
        Error = null;
        Message = null; // clear stale “Book ready” / Next guidance during pipeline
        _chosenFileName = name;
        _importPct = 8;
        _importStatus = $"Reading {name}…";
        StateHasChanged();

        try
        {
            if (IsFountainName(name))
            {
                _importStatus = "Loading screenplay…";
                _importPct = 40;
                StateHasChanged();

                await using var stream = new MemoryStream(bytes, writable: false);
                await Engine.ImportFountainAsync(ProjectId, name, stream);

                _importPct = 100;
                _importStatus = "Done";
                await LoadAsync();
                Nav.NavigateTo("adaptation/screenplay");
                return;
            }

            // PDF or TXT
            _importStatus = "Saving file…";
            _importPct = 15;
            StateHasChanged();

            await using (var stream = new MemoryStream(bytes, writable: false))
            {
                await Engine.UploadBookAsync(ProjectId, name, stream);
            }

            if (IsPdfName(name) || IsTxtName(name))
            {
                // One job: prepare (PDF/OCR) + book→Fountain (long books need background lifetime)
                Message = null;
                _importStatus = IsPdfName(name) ? "Reading book…" : "Writing screenplay…";
                _importPct = 20;
                StateHasChanged();

                await EnsureHubAsync();
                await Engine.StartBookImportAsync(
                    ProjectId,
                    skipPrepare: IsTxtName(name), // upload already wrote book text for plain .txt
                    forceExtract: IsPdfName(name),
                    forceVision: false,
                    autoVision: true,
                    model: Model);

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
            StateHasChanged();
            await LoadAsync();
            Nav.NavigateTo("adaptation/screenplay");
        }
        catch (Exception ex)
        {
            Error = FriendlyError(ex.Message);
            _importStatus = "Failed";
            _importPct = null;
        }
        finally
        {
            _importing = false;
            Busy = false;
        }
    }

    /// <summary>Poll until the current job finishes. Returns false on error/cancel.</summary>
    private async Task<bool> WaitForJobDoneAsync(
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
                var jobs = await Engine.GetJobAsync();
                var snap = jobs?.Job;
                if (snap is not null)
                {
                    Job = snap;
                    AbsorbProgressFromSnapshot(snap);
                    AbsorbProgressFromLine(snap.Message);

                    _importStatus = FriendlyJobStatus(snap);

                    // Prefer engine Index/Total (phase scale); soft-crawl when quiet mid-adapt.
                    var (_, tot, waiting, displayIdx) = AdaptationPageBase.ComputeJobProgress(
                        snap, ProgressIndex, ProgressTotal, jobRunning: true);
                    var pctWithin = AdaptationPageBase.ComputeProgressPercent(
                        displayIdx, tot > 0 ? tot : 10, waiting, jobRunning: true, snap.StartedAt);
                    var mapped = basePct + (int)Math.Round(spanPct * (pctWithin / 100.0));
                    var lo = basePct;
                    var hi = basePct + spanPct - 1;
                    _importPct = mapped < lo ? lo : mapped > hi ? hi : mapped;

                    await InvokeAsync(StateHasChanged);

                    var st = snap.Status ?? "";
                    var kindOk = string.IsNullOrEmpty(snap.Kind) ||
                                 string.Equals(snap.Kind, expectedKind, StringComparison.OrdinalIgnoreCase);

                    if (st is "running" or "queued")
                        sawRunning = true;

                    if (st is "error" or "cancelled")
                    {
                        Error = FriendlyError(snap.Error ?? snap.Message ?? "Could not import the book");
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

        Error = "Timed out while importing the book.";
        return false;
    }

    /// <summary>Operator-facing status (no mechanism jargon). Admins still see raw log below.</summary>
    private static string FriendlyJobStatus(JobSnapshot snap) =>
        AdaptationPageBase.OperatorJobRunningMessage(snap);

    private static string FriendlyError(string? raw)
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

    private static bool IsFountainName(string? name) =>
        name is not null &&
        (name.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase));

    private static bool IsPdfName(string? name) =>
        name is not null && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsTxtName(string? name) =>
        name is not null && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private void SyncTargetEditFromStatus()
    {
        var tmin = Status?.Book.TargetRuntimeMinutes
            ?? Status?.Book.SuggestedTotalMinutes
            ?? Status?.Book.NaturalRuntimeMinutes
            ?? TotalMinutes;
        _targetMinutesEdit = Math.Clamp(tmin, 2, 180);
    }

    private async Task SaveFilmRuntimeAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        _savingRuntime = true;
        _runtimeMessage = null;
        Error = null;
        try
        {
            var dto = await Engine.SetFilmRuntimeAsync(ProjectId, _targetMinutesEdit);
            if (dto is null || !dto.Ok)
                throw new InvalidOperationException("Could not save film length.");
            _runtimeMessage = dto.Message ?? $"Target set to {dto.TargetMinutes} min.";
            TotalMinutes = dto.TargetMinutes;
            await LoadAsync();
            SyncTargetEditFromStatus();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _savingRuntime = false;
        }
    }

    private async Task ResetFilmRuntimeNaturalAsync()
    {
        var natural = Status?.Book.NaturalRuntimeMinutes
            ?? Status?.Book.SuggestedTotalMinutes
            ?? _targetMinutesEdit;
        _targetMinutesEdit = Math.Clamp(natural, 2, 180);
        await SaveFilmRuntimeAsync();
    }


    private async Task OnFilmLengthChangedAsync()
    {
        try
        {
            await LoadAsync();
            SyncTargetEditFromStatus();
        }
        catch { /* ignore */ }
    }
}
