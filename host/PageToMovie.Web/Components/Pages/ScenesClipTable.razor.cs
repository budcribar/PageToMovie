using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ScenesClipTable : PageSliceComponent
{
    [CascadingParameter] public Scenes Host { get; set; } = default;
    [CascadingParameter] public Scenes.ScenesListState? ListState { get; set; }

    [CascadingParameter] public Scenes.ScenesClipSelection? ClipSel { get; set; }

    [CascadingParameter] public Scenes.ScenesClipForm? ClipForm { get; set; }
    [CascadingParameter] public Scenes.ScenesPlayback? Playback { get; set; }


    [CascadingParameter] public Scenes.ScenesGeneration? Gen { get; set; }

    [CascadingParameter] public Scenes.ScenesClipVersions? ClipVer { get; set; }
    [CascadingParameter] public Scenes.ScenesClipRegen? ClipRegen { get; set; }

    private static string ClipRowClass(bool active, bool isChecked)
    {
        if (active) return "table-primary";
        if (isChecked) return "table-warning";
        return "";
    }

    /// <summary>Row click / expander chip: expand this clip in place (or collapse it again).</summary>
    private void ToggleExpand(int cn) =>
        ClipForm!.SelectClip(ClipForm._selectedClip == cn ? null : cn);

    /// <summary>
    /// ONE status chip per clip — generating > missing > stale > verification verdict > ready.
    /// Stale-from-QA folds the verification score in rather than showing two chips side by side.
    /// </summary>
    private (string Class, string Text, string Title) ClipStatusChip(ClipSummary c)
    {
        if (ClipSel!.IsClipGenBusy(c.ClipNumber))
            return ("bg-warning text-dark", "⏳ Generating…", "Generating — file is about to change");
        if (!c.OnDisk)
            return ("bg-secondary-subtle text-secondary border border-secondary-subtle", "⚪ Missing", "No video yet — generate this clip");
        if (c.IsStale)
        {
            var sr = c.StaleReason ?? "";
            var kind = sr.StartsWith("plan_lint", StringComparison.OrdinalIgnoreCase) ? "plan"
                : sr.StartsWith("dialogue_qa", StringComparison.OrdinalIgnoreCase) ? "QA"
                : sr == "plan_newer" ? "plan newer" : "";
            var help = kind == "plan"
                ? sr + " — regenerating the clip will not clear this; rebuild the shot plan for this scene"
                : (sr.Length > 0 ? sr : "stale") + " — re-generate recommended";
            var score = c.DialogueVerification is { } v && kind == "QA" ? $" ({v.DialogueAccuracyScore:P0})" : "";
            return ("bg-warning text-dark", $"⚠ Stale · {(kind.Length > 0 ? kind : "regen")}{score}", help);
        }
        if (c.DialogueVerification is { } ver)
        {
            var isNoSpeechFail = string.Equals(ver.Status, "no_speech", StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(ver.ExpectedDialogue);
            return ver.Status?.ToLowerInvariant() switch
            {
                "verified" => ("bg-success-subtle text-success border border-success-subtle",
                    $"✓ Verified ({ver.DialogueAccuracyScore:P0})", "Dialogue, speaker and picture verified"),
                "speaker_swap" => ("bg-danger-subtle text-danger border border-danger-subtle",
                    "⚠ Speaker Swap", $"Expected {ver.ExpectedSpeaker}, heard {ver.DetectedSpeaker ?? "someone else"}"),
                "visual_defect" => ("bg-danger-subtle text-danger border border-danger-subtle",
                    "⚠ Visual defect", "The picture is broken — see the report"),
                "mismatch" => ("bg-warning-subtle text-warning border border-warning-subtle",
                    $"⚠ Mismatch ({ver.DialogueAccuracyScore:P0})", "Spoken words differ from the script"),
                "no_speech" when isNoSpeechFail => ("bg-danger-subtle text-danger border border-danger-subtle",
                    "⚠ No speech (0%)", "A line was planned but nothing was spoken"),
                // A silent clip that passed the visual-only check IS verified — same badge as spoken clips.
                "no_speech" => ("bg-success-subtle text-success border border-success-subtle",
                    $"✓ Verified ({ver.DialogueAccuracyScore:P0})", "Silent as planned; picture verified"),
                _ => ("bg-secondary-subtle text-secondary border border-secondary-subtle",
                    ver.Status ?? "unverified", "Verification state unknown"),
            };
        }
        return ("bg-success", "✓ Ready", "On disk — not yet verified");
    }

    private static string ClipDialogueTitle(ClipSummary c)
    {
        if (!string.IsNullOrWhiteSpace(c.Dialogue))
            return $"{c.Speaker}: {c.Dialogue}";
        if (!string.IsNullOrWhiteSpace(c.VisualPrompt))
            return c.VisualPrompt;
        return "Silent clip";
    }

    // ---- drag-and-drop reorder (renumber-on-drop; see docs/ui-dedup-checklist.md) --------------

    private int? _dragClip;

    /// <summary>Drag needs the table in plan order (numbers = order); off while sorted or busy.</summary>
    private bool CanDragClips =>
        ClipSel is { _clipSortByDuration: false } && !Host._busy && Gen is { JobRunning: false };

    private void HandleClipDragStart(int cn) => _dragClip = cn;

    private async Task HandleClipDropAsync(int targetCn)
    {
        var drag = _dragClip;
        _dragClip = null;
        if (drag is not int dragCn || dragCn == targetCn || !CanDragClips) return;
        if (ListState?._detail is not { } detail) return;

        var order = detail.Clips.OrderBy(c => c.ClipNumber).Select(c => c.ClipNumber).ToList();
        var from = order.IndexOf(dragCn);
        var to = order.IndexOf(targetCn);
        if (from < 0 || to < 0) return;
        order.RemoveAt(from);
        order.Insert(to, dragCn);

        Host._busy = true;
        Host._error = null;
        StateHasChanged();
        try
        {
            var (ok, error) = await Engine.ReorderClipsAsync(Host._projectId, detail.SceneNumber, order);
            if (!ok)
            {
                Host._error = error ?? "Reorder failed.";
                return;
            }
            // Old clip numbers are meaningless now — clear selection/expansion before reloading.
            ClipSel!._selectedClips.Clear();
            ClipForm!.SelectClip(null);
            await MediaFolder.ApplyServerRenamesAsync(Host._projectId);
            await ListState.LoadDetailAsync(detail.SceneNumber);
            await ListState.ReloadListAsync();
        }
        catch (Exception ex)
        {
            Host._error = ex.Message;
        }
        finally
        {
            Host._busy = false;
            StateHasChanged();
        }
    }
}
