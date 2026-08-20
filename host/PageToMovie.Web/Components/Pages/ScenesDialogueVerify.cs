using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    /// <summary>DialogueVerify domain for the Scenes page. Owns related UI state and behavior.</summary>
    public sealed class ScenesDialogueVerify
    {

    private readonly Scenes S;
    public ScenesDialogueVerify(Scenes host) => S = host;



    internal bool _verifyingClip;


    internal int _verifyingClipNumber;


    internal int _verifyCurrent;


    internal int _verifyTotal;


    internal string _verifyStatusLabel = "Verifying dialogue...";



    internal bool _showVerificationModal;


    internal int _verifModalSceneNumber;


    internal int _verifModalClipNumber;


    internal ClipDialogueVerificationResult? _verifModalResult;



    /// <summary>Select clips in the open scene that have dialogue mismatches or speaker swaps.</summary>
    internal void SelectMismatchedClips()
    {
        if (S.List._detail is null) return;
        S.ClipSel._selectedClips.Clear();
        foreach (var c in S.List._detail.Clips.Where(c => c.DialogueVerification is { Status: "mismatch" } or { Status: "speaker_swap" } or { Status: "visual_defect" }))
            S.ClipSel._selectedClips.Add(c.ClipNumber);
    }



    internal static MarkupString RenderDiffHtml(string? expected, string? heard) =>
        DialogueDiffHtml.Render(expected, heard);



    internal async Task VerifyClipDialogueManualAsync(ClipSummary clip)
    {
        if (string.IsNullOrWhiteSpace(S._projectId) || S.List._detail is null || clip is null) return;
        try
        {
            _verifyingClip = true;
            _verifyingClipNumber = clip.ClipNumber;
            _verifyCurrent = 1;
            _verifyTotal = 1;
            _verifyStatusLabel = $"Verifying dialogue for S{S.List._detail.SceneNumber:D2} C{clip.ClipNumber:D2}...";
            S.StateHasChanged();

            var expectedSize = await S.ClipRegen.ResolveExpectedClipSizeAsync(S.List._detail.SceneNumber, clip.ClipNumber);
            var videoBytes = await S.MediaFolder.GetClipBytesAsync(S._projectId, S.List._detail.SceneNumber, clip.ClipNumber, expectedSize);
            var ver = await S.Engine.VerifyClipDialogueAsync(S._projectId, S.List._detail.SceneNumber, clip.ClipNumber, videoBytes: videoBytes, force: true);
            if (ver is not null)
            {
                clip.DialogueVerification = ver;
                if (_showVerificationModal && _verifModalClipNumber == clip.ClipNumber && _verifModalSceneNumber == S.List._detail.SceneNumber)
                {
                    _verifModalResult = ver;
                }
                if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                {
                    S._error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                }

                S.List._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, S.List._detail.SceneNumber))?.Scene;
                var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
                if (scenesDto?.Scenes is not null)
                {
                    S.List._scenes = scenesDto.Scenes;
                }
            }
        }
        catch (Exception ex)
        {
            S._error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            S.StateHasChanged();
        }
    }



    /// <summary>
    /// From the all-scenes view: at least one selected scene has a finished clip on disk to check.
    /// Dialogue verification reads the clip's video, so scenes with nothing on disk have nothing to check.
    /// Gates the list-view "Verify Scene" button so it never reads as a dead click.
    /// </summary>
    internal bool SelectedScenesHaveClipsToVerify =>
        S.List._scenes is not null &&
        S.List._selected.Count > 0 &&
        S.List._scenes.Any(s => S.List._selected.Contains(s.SceneNumber) && s.ClipsOnDisk > 0);



    internal async Task VerifySelectedScenesDialogueAsync()
    {
        if (string.IsNullOrWhiteSpace(S._projectId)) return;

        // Build the clip work list from context. Detail view: the open scene's checked clips (or its
        // unverified on-disk clips). All-scenes view: every on-disk clip across the selected scenes.
        // Only clips with video on disk can be checked — there's nothing to analyse otherwise.
        var targets = await CollectDialogueVerifyTargetsAsync();

        if (targets.Count == 0)
        {
            // Never a silent dead click — say why there's nothing to do.
            S._message = EmptyVerifyTargetsMessage();
            return;
        }

        try
        {
            await RunDialogueVerificationAsync(targets);
            await RefreshScenesAfterVerificationAsync();
        }
        catch (Exception ex)
        {
            S._error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            S.StateHasChanged();
        }
    }

    private async Task<List<(int Scene, int Clip)>> CollectDialogueVerifyTargetsAsync()
    {
        var targets = new List<(int Scene, int Clip)>();
        if (S.List._detail is not null)
            CollectDetailViewTargets(S.List._detail, targets);
        else if (S.List._selected.Count > 0)
            await CollectSelectedScenesTargetsAsync(targets);
        return targets;
    }

    private void CollectDetailViewTargets(SceneDetail detail, List<(int Scene, int Clip)> targets)
    {
        if (S.ClipSel._selectedClips.Count > 0)
        {
            foreach (var cn in S.ClipSel._selectedClips.OrderBy(c => c))
                targets.Add((detail.SceneNumber, cn));
        }
        else
        {
            foreach (var c in detail.Clips
                .Where(NeedsDialogueVerification)
                .OrderBy(c => c.ClipNumber))
                targets.Add((detail.SceneNumber, c.ClipNumber));
        }
    }

    private static bool NeedsDialogueVerification(ClipSummary c) =>
        c.OnDisk && (c.DialogueVerification is null || !string.Equals(c.DialogueVerification.Status, "verified", StringComparison.OrdinalIgnoreCase));

    private async Task CollectSelectedScenesTargetsAsync(List<(int Scene, int Clip)> targets)
    {
        // All-scenes view: gather each selected scene's on-disk clips (the button is gated so this
        // path only runs when at least one selected scene actually has finished clips).
        foreach (var sn in S.List._selected.OrderBy(x => x))
        {
            var det = (await S.Engine.GetSceneDetailAsync(S._projectId, sn))?.Scene;
            if (det?.Clips is null) continue;
            foreach (var c in det.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                targets.Add((sn, c.ClipNumber));
        }
    }

    private string EmptyVerifyTargetsMessage()
    {
        if (S.List._detail is not null)
            return "All clips verified. Tick specific clip boxes in the first column to force a re-check.";
        if (S.List._selected.Count == 0)
            return "Select one or more scenes with finished clips to verify.";
        return "Selected scenes have no finished clips to verify yet.";
    }

    private async Task RunDialogueVerificationAsync(List<(int Scene, int Clip)> targets)
    {
        _verifyingClip = true;
        _verifyCurrent = 0;
        _verifyTotal = targets.Count;
        _verifyStatusLabel = $"Verifying dialogue for {targets.Count} clip(s)...";
        S.StateHasChanged();

        foreach (var (sceneNum, cn) in targets)
            await VerifyOneClipDialogueAsync(sceneNum, cn);
    }

    private async Task VerifyOneClipDialogueAsync(int sceneNum, int cn)
    {
        _verifyCurrent++;
        _verifyingClipNumber = cn;
        var clip = S.List._detail?.SceneNumber == sceneNum
            ? S.List._detail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)
            : null;
        _verifyStatusLabel = $"Verifying dialogue for S{sceneNum:D2} C{cn:D2} (Speaker: {clip?.Speaker ?? "Unknown"})...";
        S.StateHasChanged();

        var expectedSize = await S.ClipRegen.ResolveExpectedClipSizeAsync(sceneNum, cn);
        var videoBytes = await S.MediaFolder.GetClipBytesAsync(S._projectId, sceneNum, cn, expectedSize);
        var ver = await S.Engine.VerifyClipDialogueAsync(S._projectId, sceneNum, cn, videoBytes: videoBytes, force: true);
        if (ver is not null)
            ApplyVerificationResult(clip, sceneNum, cn, ver);
    }

    private void ApplyVerificationResult(ClipSummary? clip, int sceneNum, int cn, ClipDialogueVerificationResult ver)
    {
        if (clip is not null)
        {
            clip.DialogueVerification = ver;
            if (_showVerificationModal && _verifModalClipNumber == cn && _verifModalSceneNumber == sceneNum)
            {
                _verifModalResult = ver;
            }
        }
        if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
        {
            S._error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
        }
        S.StateHasChanged();
    }

    private async Task RefreshScenesAfterVerificationAsync()
    {
        if (S.List._detail is not null)
        {
            S.List._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, S.List._detail.SceneNumber))?.Scene;
        }
        var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
        if (scenesDto?.Scenes is not null)
        {
            S.List._scenes = scenesDto.Scenes;
        }
    }


    }
}
