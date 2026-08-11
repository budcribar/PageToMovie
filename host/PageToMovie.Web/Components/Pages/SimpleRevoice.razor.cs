using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class SimpleRevoice : IAsyncDisposable
{
    internal string? _projectId;
    internal string _narratorKey = "Character_Narrator";
    internal bool _hasClone;
    internal bool _busy;
    internal bool _loading = true;
    internal bool _done;
    private string? _error;
    private string? _message;
    internal string _status = "Ready";
    internal int _doneCount;
    internal string? _previewUrl;
    internal readonly List<ClipRow> _clips = new();
    private readonly List<string> _blobUrls = new();
    private bool _autoStarted;

    internal sealed class ClipRow
    {
        public int Scene { get; init; }
        public int Clip { get; init; }
        public string Dialogue { get; init; } = "";
        public string? Speaker { get; init; }
        public bool IsNarrator { get; init; }
        public string DialoguePreview =>
            Dialogue.Length <= 72 ? Dialogue : Dialogue[..69] + "…";
        public string? VideoUrl { get; set; }
        public string RelativePath => $"assets/video/scene_{Scene:D2}_clip_{Clip:D2}.mp4";
        public string Status { get; set; } = "Queued";

        public bool WillRevoice => IsNarrator && !string.IsNullOrWhiteSpace(Dialogue);

        public string SpeakerLabel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Speaker) && string.IsNullOrWhiteSpace(Dialogue))
                    return "Silent";
                if (IsNarrator) return "Narrator";
                if (string.IsNullOrWhiteSpace(Speaker)) return "Other";
                return Speaker.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
            }
        }

        public string SpeakerBadgeClass => WillRevoice
            ? "text-bg-primary"
            : string.IsNullOrWhiteSpace(Dialogue)
                ? "text-bg-light text-dark border"
                : "text-bg-secondary";
    }

    internal int _narratorClipCount => _clips.Count(c => c.WillRevoice);
    internal int _keepClipCount => _clips.Count - _narratorClipCount;

    protected override async Task OnInitializedAsync()
    {
        if (!Session.IsLoggedIn) return;
        _projectId = ActiveProject.ProjectId;
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            _loading = false;
            return;
        }

        try
        {
            await ActiveProject.RefreshReadinessAsync(Engine);
            _projectId = ActiveProject.ProjectId ?? _projectId;

            // Prefer narrator seed with a stored clone.
            try
            {
                var chars = await Engine.GetCharactersAsync(_projectId);
                var withVoice = chars?.Characters?
                    .FirstOrDefault(c =>
                        !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId));
                var narrator = chars?.Characters?
                    .FirstOrDefault(c =>
                        string.Equals(c.Key, "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
                        (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false));
                if (narrator is not null)
                    _narratorKey = narrator.Key ?? _narratorKey;
                else if (withVoice is not null)
                    _narratorKey = withVoice.Key ?? _narratorKey;

                _hasClone = chars?.Characters?.Any(c =>
                    string.Equals(c.Key, _narratorKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId)) == true;
            }
            catch
            {
                _hasClone = true; // try speak anyway — API will reject if missing
            }

            var scenes = await Engine.GetScenesAsync(_projectId);
            if (scenes?.Scenes is { Count: > 0 })
            {
                foreach (var s in scenes.Scenes.OrderBy(x => x.SceneNumber))
                {
                    var detail = await Engine.GetSceneDetailAsync(_projectId, s.SceneNumber);
                    if (detail?.Scene?.Clips is not { Count: > 0 } clips) continue;
                    foreach (var c in clips.OrderBy(x => x.ClipNumber))
                    {
                        var speaker = (c.Speaker ?? "").Trim();
                        var dialogue = (c.Dialogue ?? "").Trim();
                        var isNarrator = IsNarratorSpeaker(speaker, _narratorKey);
                        _clips.Add(new ClipRow
                        {
                            Scene = s.SceneNumber,
                            Clip = c.ClipNumber,
                            Dialogue = dialogue,
                            Speaker = string.IsNullOrEmpty(speaker) ? null : speaker,
                            IsNarrator = isNarrator,
                            VideoUrl = c.OnDisk ? Engine.ClipVideoUrl(_projectId, s.SceneNumber, c.ClipNumber) : null,
                            Status = isNarrator && dialogue.Length > 0
                                ? "Queued"
                                : isNarrator
                                    ? "Keep · no line"
                                    : string.IsNullOrEmpty(dialogue)
                                        ? "Keep · silent"
                                        : $"Keep · {ShortSpeaker(speaker)}",
                        });
                    }
                }
            }

            if (_clips.Count == 0)
                _status = "No clips in this project yet";
            else if (_narratorClipCount == 0)
                _status = "No narrator clips to re-voice";
            else
                _status = $"{_narratorClipCount} narrator clip(s) · {_keepClipCount} kept as-is";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _autoStarted || _busy || _loading) return;
        if (!_hasClone || _narratorClipCount == 0 || string.IsNullOrWhiteSpace(_projectId)) return;
        // Auto-run once when arriving from “Put my voice on the film”.
        _autoStarted = true;
        await RunAsync();
    }

    internal async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _narratorClipCount == 0 || _busy) return;
        _busy = true;
        _done = false;
        _error = null;
        _message = null;
        _doneCount = 0;
        _status = "Starting…";
        StateHasChanged();

        try
        {
            // Prefer client copies when the media folder is connected.
            if (!MediaFolder.IsConnected)
                await MediaFolder.TryReconnectAsync();

            var okCount = 0;
            var failCount = 0;
            var keepCount = 0;
            double? estUsd = 0;

            for (var i = 0; i < _clips.Count; i++)
            {
                var row = _clips[i];
                _status = $"Clip {i + 1} of {_clips.Count}: S{row.Scene:D2} C{row.Clip:D2}";
                StateHasChanged();

                try
                {
                    // Only re-voice narrator dialogue. Mom / others / silent clips stay original.
                    if (!row.WillRevoice)
                    {
                        row.Status = string.IsNullOrWhiteSpace(row.Dialogue)
                            ? "Keep · silent"
                            : row.IsNarrator
                                ? "Keep · no line"
                                : $"Keep · {row.SpeakerLabel}";
                        keepCount++;
                        _doneCount = i + 1;
                        StateHasChanged();
                        continue;
                    }

                    row.Status = "Working…";
                    StateHasChanged();

                    var videoUrl = await ResolveVideoUrlAsync(row);
                    if (string.IsNullOrWhiteSpace(videoUrl))
                    {
                        row.Status = "Skip · no picture";
                        failCount++;
                        _doneCount = i + 1;
                        continue;
                    }

                    row.Status = "Speaking…";
                    StateHasChanged();
                    var speak = await Engine.SpeakVoiceAsync(_projectId, _narratorKey, row.Dialogue);
                    if (!speak.Ok)
                    {
                        row.Status = "Fail · TTS";
                        _error = speak.Error ?? "Speech failed";
                        failCount++;
                        _doneCount = i + 1;
                        continue;
                    }
                    if (speak.EstimatedUsd is { } u) estUsd = (estUsd ?? 0) + u;

                    var audioUrl = await AudioUrlFromSpeakAsync(speak);
                    if (string.IsNullOrWhiteSpace(audioUrl))
                    {
                        row.Status = "Fail · audio";
                        failCount++;
                        _doneCount = i + 1;
                        continue;
                    }

                    row.Status = "Muxing…";
                    StateHasChanged();
                    var mux = await Js.InvokeAsync<FfmpegResult>(
                        "PageToMovieFfmpeg.replaceVideoAudioAsync", videoUrl, audioUrl);
                    if (mux is not { Success: true } || string.IsNullOrWhiteSpace(mux.Url))
                    {
                        row.Status = "Fail · mux";
                        failCount++;
                        _doneCount = i + 1;
                        if (!string.IsNullOrWhiteSpace(mux?.Error))
                            _error = mux.Error;
                        continue;
                    }

                    var outUrl = mux.Url;

                    // Persist into the client media folder when connected.
                    if (MediaFolder.IsConnected)
                    {
                        try
                        {
                            var clientPath = $"{_projectId}/{row.RelativePath}";
                            var saved = await Js.InvokeAsync<SaveBlobResult>(
                                "PageToMovieMedia.saveBlobUrlAsync", outUrl, clientPath);
                            if (saved is { Success: true } && !string.IsNullOrWhiteSpace(saved.Sha256))
                            {
                                try
                                {
                                    await Engine.RegisterMediaAsync(_projectId, new MediaRegisterRequest
                                    {
                                        RelativePath = row.RelativePath,
                                        Sha256 = saved.Sha256,
                                        SizeBytes = saved.SizeBytes,
                                        Kind = "clip",
                                        Scene = row.Scene,
                                        Clip = row.Clip,
                                    });
                                }
                                catch { /* non-fatal */ }
                            }
                        }
                        catch { /* keep going — blob still previewable */ }
                    }

                    if (!string.IsNullOrEmpty(_previewUrl) && _previewUrl.StartsWith("blob:", StringComparison.Ordinal))
                    {
                        try { await Js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", _previewUrl); } catch { /* */ }
                    }
                    _previewUrl = outUrl;
                    _blobUrls.Add(outUrl);
                    row.Status = "Done";
                    okCount++;
                }
                catch (Exception ex)
                {
                    row.Status = "Fail";
                    _error = ex.Message;
                    failCount++;
                }

                _doneCount = i + 1;
                StateHasChanged();
            }

            _done = true;
            _status = failCount == 0
                ? $"Done — {okCount} narrator clip(s) re-voiced · {keepCount} kept"
                : $"Finished with issues — {okCount} re-voiced, {failCount} failed, {keepCount} kept";
            _message = failCount == 0
                ? "Narrator lines use your voice. Other speakers (and silent clips) are unchanged. Open Preview to stitch."
                : "Some narrator clips could not be re-voiced. Fix those (missing video or voice key) and run again.";
            if (estUsd is > 0)
                _message += $" · TTS ~${estUsd:0.0000}";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _status = "Stopped";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<string?> ResolveVideoUrlAsync(ClipRow row)
    {
        // 1) Client media folder
        if (MediaFolder.IsConnected)
        {
            try
            {
                var local = await MediaFolder.GetCurrentBlobUrlAsync(_projectId!, row.RelativePath, null);
                if (!string.IsNullOrWhiteSpace(local))
                    return local;
            }
            catch { /* fall through */ }
        }

        // 2) Server clip URL
        if (!string.IsNullOrWhiteSpace(row.VideoUrl))
            return Engine.BrowserMediaPath(row.VideoUrl);

        return Engine.BrowserMediaPath(Engine.ClipVideoUrl(_projectId!, row.Scene, row.Clip));
    }

    private async Task<string?> AudioUrlFromSpeakAsync(SpeakVoiceDto speak)
    {
        if (!string.IsNullOrWhiteSpace(speak.AudioBase64))
        {
            try
            {
                var mime = string.IsNullOrWhiteSpace(speak.ContentType) ? "audio/mpeg" : speak.ContentType;
                var url = await Js.InvokeAsync<string?>("PageToMovieMedia.blobUrlFromBase64", speak.AudioBase64, mime);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _blobUrls.Add(url);
                    return url;
                }
            }
            catch
            {
                // fall through to clientUrl
            }
        }

        if (!string.IsNullOrWhiteSpace(speak.ClientUrl))
            return Engine.BrowserMediaPath(speak.ClientUrl);
        return null;
    }

    internal static string StatusClass(string status) => status switch
    {
        "Done" => "badge text-bg-success",
        "Queued" => "badge text-bg-primary",
        var s when s.StartsWith("Keep", StringComparison.OrdinalIgnoreCase) => "badge text-bg-secondary",
        var s when s.StartsWith("Fail", StringComparison.OrdinalIgnoreCase) => "badge text-bg-danger",
        var s when s.StartsWith("Skip", StringComparison.OrdinalIgnoreCase) => "badge text-bg-warning text-dark",
        _ => "badge text-bg-primary",
    };

    /// <summary>
    /// True when this clip's speaker is the project narrator (exact key, or name contains "narrator").
    /// </summary>
    private static bool IsNarratorSpeaker(string? speaker, string narratorKey) =>
        CastKindClassifier.IsNarratorSpeaker(speaker, narratorKey);

    private static string ShortSpeaker(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker)) return "other";
        return speaker.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var u in _blobUrls.Distinct())
        {
            if (u.StartsWith("blob:", StringComparison.Ordinal))
            {
                try { await Js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", u); } catch { /* */ }
            }
        }
    }

    private sealed class FfmpegResult
    {
        public bool Success { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
    }

    private sealed class SaveBlobResult
    {
        public bool Success { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string? RelativePath { get; set; }
        public string? Error { get; set; }
    }
}
