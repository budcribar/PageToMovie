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

// Forwarders: ScenesClipEditor → Host.*
public partial class Scenes
{
    internal void ToggleClipDurationSort() => ClipEd.ToggleClipDurationSort();

    internal IEnumerable<ClipSummary> SortedDetailClips => ClipEd.SortedDetailClips;

    internal bool IsClipGenBusy(int clipNumber) => ClipEd.IsClipGenBusy(clipNumber);

    internal bool PreviousClipMissing(int clipNumber) => ClipEd.PreviousClipMissing(clipNumber);

    internal void SelectMissingClips() => ClipEd.SelectMissingClips();

    internal Task OpenInExternalEditorAsync(int? sceneNumber = null, int? clipNumber = null) => ClipEd.OpenInExternalEditorAsync(sceneNumber, clipNumber);

    internal void ToggleClipSelect(int cn, bool on) => ClipEd.ToggleClipSelect(cn, on);

    internal void ClearClipSelection() => ClipEd.ClearClipSelection();

    internal void ToggleSelectAllClips(bool on) => ClipEd.ToggleSelectAllClips(on);

    internal double? EstimateSelectedClipsCostUsd() => ClipEd.EstimateSelectedClipsCostUsd();

    internal Task EnsurePredecessorsUploadedAsync(List<(int Scene, int Clip)> targets) => ClipEd.EnsurePredecessorsUploadedAsync(targets);

    internal Task<double?> ResolveActiveVideoExtendModelAsync() => ClipEd.ResolveActiveVideoExtendModelAsync();

    internal Task<List<(int Scene, int Clip)>> MissingClipTargetsAsync(int sn) => ClipEd.MissingClipTargetsAsync(sn);

    internal Task RegenSelectedClipsAsync() => ClipEd.RegenSelectedClipsAsync();

    internal void SelectClip(int? cn) => ClipEd.SelectClip(cn);

    internal Task<long?> ResolveExpectedClipSizeAsync(int scene, int clip) => ClipEd.ResolveExpectedClipSizeAsync(scene, clip);

    internal Task RegenClipAsync(int sn, int cn) => ClipEd.RegenClipAsync(sn, cn);

    internal static bool ClipExceedsEditDurationCap(ClipSummary clip) => ScenesClipEditor.ClipExceedsEditDurationCap(clip);

    internal void OpenVideoEditPrompt() => ClipEd.OpenVideoEditPrompt();

    internal void CloseVideoEditPrompt() => ClipEd.CloseVideoEditPrompt();

    internal Task SubmitVideoEditAsync() => ClipEd.SubmitVideoEditAsync();

    internal void OpenClipEditor(ClipSummary clip) => ClipEd.OpenClipEditor(clip);

    internal void OpenAddClipDialog() => ClipEd.OpenAddClipDialog();

    internal void CloseClipEditor() => ClipEd.CloseClipEditor();

    internal void ToggleClipEditorCast(string charKey, bool on) => ClipEd.ToggleClipEditorCast(charKey, on);

    internal Task OnClipEditorCastToggled((string Key, bool On) args) => ClipEd.OnClipEditorCastToggled(args);

    internal Task SaveClipEditorAsync() => ClipEd.SaveClipEditorAsync();

    internal void RequestDeleteClip(int scene, int clip) => ClipEd.RequestDeleteClip(scene, clip);

    internal void CancelDeleteClip() => ClipEd.CancelDeleteClip();

    internal Task ConfirmDeleteClipAsync() => ClipEd.ConfirmDeleteClipAsync();

    internal int EstimateSelectedClips() => ClipEd.EstimateSelectedClips();

    internal Task OpenClipCompareAsync(int sceneNumber, int clipNumber) => ClipEd.OpenClipCompareAsync(sceneNumber, clipNumber);

    internal void CloseClipCompare() => ClipEd.CloseClipCompare();

    internal Task PromoteClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipEd.PromoteClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipEd.SoftDeleteClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId) => ClipEd.RestoreClipVersionAsync(sceneNumber, clipNumber, versionId);

    internal Task EmptyClipTrashAsync(int sceneNumber, int clipNumber) => ClipEd.EmptyClipTrashAsync(sceneNumber, clipNumber);



    internal bool AllClipsSelected => ClipEd.AllClipsSelected;
    internal (int Scene, int Clip)? _deleteClipTarget
    {
        get => ClipEd._deleteClipTarget;
        set => ClipEd._deleteClipTarget = value;
    }

}
