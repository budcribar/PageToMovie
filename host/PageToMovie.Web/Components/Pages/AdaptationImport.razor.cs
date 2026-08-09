using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport
{
    public override string StepKey => "import";

    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ImportDrop? _drop;
    internal ImportDrop Drop => _drop ??= new ImportDrop(this);
    private ImportGate? _gate;
    internal ImportGate Gate => _gate ??= new ImportGate(this);
    private ImportRuntime? _runtime;
    internal ImportRuntime Runtime => _runtime ??= new ImportRuntime(this);

    // ── Field forwarders (markup keeps same member names) ──────────────────
    private bool _importing
    {
        get => Drop._importing;
        set => Drop._importing = value;
    }
    private string _importStatus
    {
        get => Drop._importStatus;
        set => Drop._importStatus = value;
    }
    private int? _importPct
    {
        get => Drop._importPct;
        set => Drop._importPct = value;
    }
    private string? _chosenFileName
    {
        get => Drop._chosenFileName;
        set => Drop._chosenFileName = value;
    }
    private bool _dragOver
    {
        get => Drop._dragOver;
        set => Drop._dragOver = value;
    }
    private int _inputFileKey
    {
        get => Drop._inputFileKey;
        set => Drop._inputFileKey = value;
    }
    private string _importBlockedReason
    {
        get => Gate._importBlockedReason;
        set => Gate._importBlockedReason = value;
    }
    private int _targetMinutesEdit
    {
        get => Runtime._targetMinutesEdit;
        set => Runtime._targetMinutesEdit = value;
    }
    private bool _savingRuntime
    {
        get => Runtime._savingRuntime;
        set => Runtime._savingRuntime = value;
    }
    private string? _runtimeMessage
    {
        get => Runtime._runtimeMessage;
        set => Runtime._runtimeMessage = value;
    }

    // ── Method / property forwarders ───────────────────────────────────────
    private bool ImportReady => Gate.ImportReady;
    private static bool IsUsablePlanningModel(string? id) => ImportGate.IsUsablePlanningModel(id);
    private bool CanContinueToScreenplay => Gate.CanContinueToScreenplay;
    private void RefreshImportGate() => Gate.RefreshImportGate();

    private void OnDragEnter(DragEventArgs e) => Drop.OnDragEnter(e);
    private void OnDragOver(DragEventArgs e) => Drop.OnDragOver(e);
    private void OnDragLeave(DragEventArgs e) => Drop.OnDragLeave(e);
    private void OnDrop(DragEventArgs e) => Drop.OnDrop(e);
    private Task OnSourceSelectedAsync(InputFileChangeEventArgs e) => Drop.OnSourceSelectedAsync(e);
    private Task ImportBufferedAsync(string name, byte[] bytes) => Drop.ImportBufferedAsync(name, bytes);
    private Task<bool> WaitForJobDoneAsync(string expectedKind, int basePct, int spanPct) =>
        Drop.WaitForJobDoneAsync(expectedKind, basePct, spanPct);
    private static string FriendlyJobStatus(JobSnapshot snap) => ImportDrop.FriendlyJobStatus(snap);
    private static string FriendlyError(string? raw) => ImportDrop.FriendlyError(raw);
    private static bool IsFountainName(string? name) => ImportDrop.IsFountainName(name);
    private static bool IsPdfName(string? name) => ImportDrop.IsPdfName(name);
    private static bool IsTxtName(string? name) => ImportDrop.IsTxtName(name);

    private void SyncTargetEditFromStatus() => Runtime.SyncTargetEditFromStatus();
    private Task SaveFilmRuntimeAsync() => Runtime.SaveFilmRuntimeAsync();
    private Task ResetFilmRuntimeNaturalAsync() => Runtime.ResetFilmRuntimeNaturalAsync();
    private Task OnFilmLengthChangedAsync() => Runtime.OnFilmLengthChangedAsync();

    // ── Lifecycle orchestration ────────────────────────────────────────────
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
        Runtime.SyncTargetEditFromStatus();
        Gate.RefreshImportGate();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        Gate.RefreshImportGate();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        await Drop.OnAfterRenderAsync(firstRender);
    }
}
