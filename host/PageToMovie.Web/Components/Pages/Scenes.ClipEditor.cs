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

// Forwarders: clip domains → Host.*
public partial class Scenes
{
    // ── Selection ──────────────────────────────────────────────────────────
    internal void ToggleClipDurationSort() => ClipSel.ToggleClipDurationSort();

    internal IEnumerable<ClipSummary> SortedDetailClips => ClipSel.SortedDetailClips;

    internal bool IsClipGenBusy(int clipNumber) => ClipSel.IsClipGenBusy(clipNumber);

    internal bool PreviousClipMissing(int clipNumber) => ClipSel.PreviousClipMissing(clipNumber);

    internal void SelectMissingClips() => ClipSel.SelectMissingClips();

    internal void ToggleClipSelect(int cn, bool on) => ClipSel.ToggleClipSelect(cn, on);

    internal void ClearClipSelection() => ClipSel.ClearClipSelection();

    internal void ToggleSelectAllClips(bool on) => ClipSel.ToggleSelectAllClips(on);

    internal double? EstimateSelectedClipsCostUsd() => ClipSel.EstimateSelectedClipsCostUsd();

    internal int EstimateSelectedClips() => ClipSel.EstimateSelectedClips();

    internal bool AllClipsSelected => ClipSel.AllClipsSelected;

    // ── Regen ──────────────────────────────────────────────────────────────
    internal Task OpenInExternalEditorAsync(int? sceneNumber = null, int? clipNumber = null) => ClipRegen.OpenInExternalEditorAsync(sceneNumber, clipNumber);

    internal Task EnsurePredecessorsUploadedAsync(List<(int Scene, int Clip)> targets) => ClipRegen.EnsurePredecessorsUploadedAsync(targets);

    internal Task<double?> ResolveActiveVideoExtendModelAsync() => ClipRegen.ResolveActiveVideoExtendModelAsync();

    internal Task<List<(int Scene, int Clip)>> MissingClipTargetsAsync(int sn) => ClipRegen.MissingClipTargetsAsync(sn);

    internal Task RegenSelectedClipsAsync() => ClipRegen.RegenSelectedClipsAsync();

    internal Task<long?> ResolveExpectedClipSizeAsync(int scene, int clip) => ClipRegen.ResolveExpectedClipSizeAsync(scene, clip);

    internal Task RegenClipAsync(int sn, int cn) => ClipRegen.RegenClipAsync(sn, cn);

    internal static bool ClipExceedsEditDurationCap(ClipSummary clip) => ScenesClipRegen.ClipExceedsEditDurationCap(clip);

    internal void OpenVideoEditPrompt() => ClipRegen.OpenVideoEditPrompt();

    internal void CloseVideoEditPrompt() => ClipRegen.CloseVideoEditPrompt();

    internal Task SubmitVideoEditAsync() => ClipRegen.SubmitVideoEditAsync();

    // ── Form ───────────────────────────────────────────────────────────────
    internal void SelectClip(int? cn) => ClipForm.SelectClip(cn);

    internal void OpenClipEditor(ClipSummary clip) => ClipForm.OpenClipEditor(clip);

    internal void OpenAddClipDialog() => ClipForm.OpenAddClipDialog();

    internal void CloseClipEditor() => ClipForm.CloseClipEditor();

    internal void ToggleClipEditorCast(string charKey, bool on) => ClipForm.ToggleClipEditorCast(charKey, on);

    internal Task OnClipEditorCastToggled((string Key, bool On) args) => ClipForm.OnClipEditorCastToggled(args);

    internal Task SaveClipEditorAsync() => ClipForm.SaveClipEditorAsync();

    internal void RequestDeleteClip(int scene, int clip) => ClipForm.RequestDeleteClip(scene, clip);

    internal void CancelDeleteClip() => ClipForm.CancelDeleteClip();

    internal Task ConfirmDeleteClipAsync() => ClipForm.ConfirmDeleteClipAsync();

    internal (int Scene, int Clip)? _deleteClipTarget
    {
        get => ClipForm._deleteClipTarget;
        set => ClipForm._deleteClipTarget = value;
    }

    // ── Versions ───────────────────────────────────────────────────────────
    internal Task OpenClipCompareAsync(int sceneNumber, int clipNumber) => ClipVer.OpenClipCompareAsync(sceneNumber, clipNumber);

    internal void CloseClipCompare() => ClipVer.CloseClipCompare();

    internal Task PromoteClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipVer.PromoteClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipVer.SoftDeleteClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipVer.RestoreClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task EmptyClipTrashAsync(int sceneNumber, int clipNumber) => ClipVer.EmptyClipTrashAsync(sceneNumber, clipNumber);
}
