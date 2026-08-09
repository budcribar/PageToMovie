using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: DialogueVerify — partial methods/properties for the Scenes page
public partial class Scenes
{

    /// <summary>Select clips in the open scene that have dialogue mismatches or speaker swaps.</summary>
    internal void SelectMismatchedClips()
    {
        if (_detail is null) return;
        _selectedClips.Clear();
        foreach (var c in _detail.Clips.Where(c => c.DialogueVerification is { Status: "mismatch" } or { Status: "speaker_swap" }))
            _selectedClips.Add(c.ClipNumber);
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
        if (string.IsNullOrWhiteSpace(_projectId) || _detail is null || clip is null) return;
        try
        {
            _verifyingClip = true;
            _verifyingClipNumber = clip.ClipNumber;
            _verifyCurrent = 1;
            _verifyTotal = 1;
            _verifyStatusLabel = $"Verifying dialogue for S{_detail.SceneNumber:D2} C{clip.ClipNumber:D2}...";
            StateHasChanged();

            var expectedSize = await ResolveExpectedClipSizeAsync(_detail.SceneNumber, clip.ClipNumber);
            var videoBytes = await MediaFolder.GetClipBytesAsync(_projectId, _detail.SceneNumber, clip.ClipNumber, expectedSize);
            var ver = await Engine.VerifyClipDialogueAsync(_projectId, _detail.SceneNumber, clip.ClipNumber, videoBytes: videoBytes, force: true);
            if (ver is not null)
            {
                clip.DialogueVerification = ver;
                if (_showVerificationModal && _verifModalClipNumber == clip.ClipNumber && _verifModalSceneNumber == _detail.SceneNumber)
                {
                    _verifModalResult = ver;
                }
                if (string.Equals(ver.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                {
                    _error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                }

                _detail = (await Engine.GetSceneDetailAsync(_projectId, _detail.SceneNumber))?.Scene;
                var scenesDto = await Engine.GetScenesAsync(_projectId);
                if (scenesDto?.Scenes is not null)
                {
                    _scenes = scenesDto.Scenes;
                }
            }
        }
        catch (Exception ex)
        {
            _error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            StateHasChanged();
        }
    }


    /// <summary>
    /// From the all-scenes view: at least one selected scene has a finished clip on disk to check.
    /// Dialogue verification reads the clip's video, so scenes with nothing on disk have nothing to check.
    /// Gates the list-view "Verify Scene Dialogue" button so it never reads as a dead click.
    /// </summary>
    internal bool SelectedScenesHaveClipsToVerify =>
        _scenes is not null &&
        _selected.Count > 0 &&
        _scenes.Any(s => _selected.Contains(s.SceneNumber) && s.ClipsOnDisk > 0);


    internal async Task VerifySelectedScenesDialogueAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId)) return;

        // Build the clip work list from context. Detail view: the open scene's checked clips (or its
        // unverified on-disk clips). All-scenes view: every on-disk clip across the selected scenes.
        // Only clips with video on disk can be checked — there's nothing to analyse otherwise.
        var targets = new List<(int Scene, int Clip)>();

        if (_detail is not null)
        {
            if (_selectedClips.Count > 0)
            {
                foreach (var cn in _selectedClips.OrderBy(c => c))
                    targets.Add((_detail.SceneNumber, cn));
            }
            else
            {
                foreach (var c in _detail.Clips
                    .Where(c => c.OnDisk && (c.DialogueVerification is null || !string.Equals(c.DialogueVerification.Status, "verified", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(c => c.ClipNumber))
                    targets.Add((_detail.SceneNumber, c.ClipNumber));
            }
        }
        else if (_selected.Count > 0)
        {
            // All-scenes view: gather each selected scene's on-disk clips (the button is gated so this
            // path only runs when at least one selected scene actually has finished clips).
            foreach (var sn in _selected.OrderBy(x => x))
            {
                var det = (await Engine.GetSceneDetailAsync(_projectId, sn))?.Scene;
                if (det?.Clips is null) continue;
                foreach (var c in det.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                    targets.Add((sn, c.ClipNumber));
            }
        }

        if (targets.Count == 0)
        {
            // Never a silent dead click — say why there's nothing to do.
            _message = _detail is not null
                ? "All clips verified. Tick specific clip boxes in the first column to force a re-check."
                : _selected.Count == 0
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
            StateHasChanged();

            foreach (var (sceneNum, cn) in targets)
            {
                _verifyCurrent++;
                _verifyingClipNumber = cn;
                var clip = _detail?.SceneNumber == sceneNum
                    ? _detail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)
                    : null;
                _verifyStatusLabel = $"Verifying dialogue for S{sceneNum:D2} C{cn:D2} (Speaker: {clip?.Speaker ?? "Unknown"})...";
                StateHasChanged();

                var expectedSize = await ResolveExpectedClipSizeAsync(sceneNum, cn);
                var videoBytes = await MediaFolder.GetClipBytesAsync(_projectId, sceneNum, cn, expectedSize);
                var ver = await Engine.VerifyClipDialogueAsync(_projectId, sceneNum, cn, videoBytes: videoBytes, force: true);
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
                        _error = ver.SummaryNote ?? "Clip Dialogue Verification requires Google Gemini (GEMINI_API_KEY). Gemini is the only provider that supports native video & audio dialogue analysis. Please add your Gemini key in Configuration.";
                    }
                    StateHasChanged();
                }
            }

            if (_detail is not null)
            {
                _detail = (await Engine.GetSceneDetailAsync(_projectId, _detail.SceneNumber))?.Scene;
            }
            var scenesDto = await Engine.GetScenesAsync(_projectId);
            if (scenesDto?.Scenes is not null)
            {
                _scenes = scenesDto.Scenes;
            }
        }
        catch (Exception ex)
        {
            _error = $"Dialogue verification failed: {ex.Message}";
        }
        finally
        {
            _verifyingClip = false;
            _verifyingClipNumber = 0;
            _verifyCurrent = 0;
            _verifyTotal = 0;
            StateHasChanged();
        }
    }

}
