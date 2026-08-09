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

public partial class Scenes
{
    /// <summary>DialogueVerify domain for the Scenes page. Owns related UI state and behavior.</summary>
    internal sealed class ScenesDialogueVerify
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
        if (S._detail is null) return;
        S._selectedClips.Clear();
        foreach (var c in S._detail.Clips.Where(c => c.DialogueVerification is { Status: "mismatch" } or { Status: "speaker_swap" }))
            S._selectedClips.Add(c.ClipNumber);
    }



    internal static MarkupString RenderDiffHtml(string? expected, string? heard)
    {
        var expStr = expected ?? "";
        var heardStr = heard ?? "";
        if (string.IsNullOrWhiteSpace(expStr) && string.IsNullOrWhiteSpace(heardStr))
            return new MarkupString("—");

        var expWords = System.Text.RegularExpressions.Regex.Split(expStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        var heardWords = System.Text.RegularExpressions.Regex.Split(heardStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

        static string Clean(string w) => System.Text.RegularExpressions.Regex.Replace(w.ToLowerInvariant(), @"[^\w]", "");

        var expClean = expWords.Select(Clean).ToList();
        var heardClean = heardWords.Select(Clean).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"small\">");

        // Expected line: missing words highlighted in strikethrough red
        sb.Append("<div><strong>Expected:</strong> ");
        for (int i = 0; i < expWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(expWords[i]);
            var c = expClean[i];
            if (!string.IsNullOrEmpty(c) && !heardClean.Contains(c))
            {
                sb.Append($"<span class=\"badge bg-danger-subtle text-danger text-decoration-line-through me-1\" title=\"Missing from spoken clip audio\">{word}</span> ");
            }
            else
            {
                sb.Append($"{word} ");
            }
        }
        sb.Append("</div>");

        // Heard line: extra words highlighted in soft yellow
        sb.Append("<div><strong>Heard:</strong> ");
        for (int i = 0; i < heardWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(heardWords[i]);
            var c = heardClean[i];
            if (!string.IsNullOrEmpty(c) && !expClean.Contains(c))
            {
                sb.Append($"<span class=\"badge bg-warning-subtle text-warning border border-warning-subtle me-1\" title=\"Extra/changed word heard in clip\">{word}</span> ");
            }
            else
            {
                sb.Append($"{word} ");
            }
        }
        sb.Append("</div></div>");

        return new MarkupString(sb.ToString());
    }



    internal async Task VerifyClipDialogueManualAsync(ClipSummary clip)
    {
        if (string.IsNullOrWhiteSpace(S._projectId) || S._detail is null || clip is null) return;
        try
        {
            _verifyingClip = true;
            _verifyingClipNumber = clip.ClipNumber;
            _verifyCurrent = 1;
            _verifyTotal = 1;
            _verifyStatusLabel = $"Verifying dialogue for S{S._detail.SceneNumber:D2} C{clip.ClipNumber:D2}...";
            S.StateHasChanged();

            var expectedSize = await S.ResolveExpectedClipSizeAsync(S._detail.SceneNumber, clip.ClipNumber);
            var videoBytes = await S.MediaFolder.GetClipBytesAsync(S._projectId, S._detail.SceneNumber, clip.ClipNumber, expectedSize);
            var ver = await S.Engine.VerifyClipDialogueAsync(S._projectId, S._detail.SceneNumber, clip.ClipNumber, videoBytes: videoBytes, force: true);
            if (ver is not null)
            {
                clip.DialogueVerification = ver;
                if (_showVerificationModal && _verifModalClipNumber == clip.ClipNumber && _verifModalSceneNumber == S._detail.SceneNumber)
                {
                    _verifModalResult = ver;
                }
                if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                {
                    S._error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                }

                S._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, S._detail.SceneNumber))?.Scene;
                var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
                if (scenesDto?.Scenes is not null)
                {
                    S._scenes = scenesDto.Scenes;
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
    /// Gates the list-view "Verify Scene Dialogue" button so it never reads as a dead click.
    /// </summary>
    internal bool SelectedScenesHaveClipsToVerify =>
        S._scenes is not null &&
        S._selected.Count > 0 &&
        S._scenes.Any(s => S._selected.Contains(s.SceneNumber) && s.ClipsOnDisk > 0);



    internal async Task VerifySelectedScenesDialogueAsync()
    {
        if (string.IsNullOrWhiteSpace(S._projectId)) return;

        // Build the clip work list from context. Detail view: the open scene's checked clips (or its
        // unverified on-disk clips). All-scenes view: every on-disk clip across the selected scenes.
        // Only clips with video on disk can be checked — there's nothing to analyse otherwise.
        var targets = new List<(int Scene, int Clip)>();

        if (S._detail is not null)
        {
            if (S._selectedClips.Count > 0)
            {
                foreach (var cn in S._selectedClips.OrderBy(c => c))
                    targets.Add((S._detail.SceneNumber, cn));
            }
            else
            {
                foreach (var c in S._detail.Clips
                    .Where(c => c.OnDisk && (c.DialogueVerification is null || !string.Equals(c.DialogueVerification.Status, "verified", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(c => c.ClipNumber))
                    targets.Add((S._detail.SceneNumber, c.ClipNumber));
            }
        }
        else if (S._selected.Count > 0)
        {
            // All-scenes view: gather each selected scene's on-disk clips (the button is gated so this
            // path only runs when at least one selected scene actually has finished clips).
            foreach (var sn in S._selected.OrderBy(x => x))
            {
                var det = (await S.Engine.GetSceneDetailAsync(S._projectId, sn))?.Scene;
                if (det?.Clips is null) continue;
                foreach (var c in det.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                    targets.Add((sn, c.ClipNumber));
            }
        }

        if (targets.Count == 0)
        {
            // Never a silent dead click — say why there's nothing to do.
            S._message = S._detail is not null
                ? "All clips verified. Tick specific clip boxes in the first column to force a re-check."
                : S._selected.Count == 0
                    ? "Select one or more scenes with finished clips to verify."
                    : "Selected scenes have no finished clips to verify yet.";
            return;
        }

        try
        {
            _verifyingClip = true;
            _verifyCurrent = 0;
            _verifyTotal = targets.Count;
            _verifyStatusLabel = $"Verifying dialogue for {targets.Count} clip(s)...";
            S.StateHasChanged();

            foreach (var (sceneNum, cn) in targets)
            {
                _verifyCurrent++;
                _verifyingClipNumber = cn;
                var clip = S._detail?.SceneNumber == sceneNum
                    ? S._detail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)
                    : null;
                _verifyStatusLabel = $"Verifying dialogue for S{sceneNum:D2} C{cn:D2} (Speaker: {clip?.Speaker ?? "Unknown"})...";
                S.StateHasChanged();

                var expectedSize = await S.ResolveExpectedClipSizeAsync(sceneNum, cn);
                var videoBytes = await S.MediaFolder.GetClipBytesAsync(S._projectId, sceneNum, cn, expectedSize);
                var ver = await S.Engine.VerifyClipDialogueAsync(S._projectId, sceneNum, cn, videoBytes: videoBytes, force: true);
                if (ver is not null)
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
            }

            if (S._detail is not null)
            {
                S._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, S._detail.SceneNumber))?.Scene;
            }
            var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
            if (scenesDto?.Scenes is not null)
            {
                S._scenes = scenesDto.Scenes;
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


    }
}
