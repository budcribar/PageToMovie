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

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class SimpleRevoice : IAsyncDisposable, IPageSliceHost
{
    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the page-local sections are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }

    internal string? _projectId;
    internal string _narratorKey = "Character_Narrator";
    internal bool _hasClone;
    internal bool _busy;
    internal bool _loading = true;
    internal bool _done;
    internal string? _error;
    internal string? _message;
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

        public string SpeakerBadgeClass
        {
            get
            {
                if (WillRevoice) return "text-bg-primary";
                if (string.IsNullOrWhiteSpace(Dialogue)) return "text-bg-light text-dark border";
                return "text-bg-secondary";
            }
        }
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

            await ResolveNarratorKeyAndCloneAsync();
            await LoadClipsFromScenesAsync();
            _status = StatusAfterLoad();
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

    /// <summary>Prefer narrator seed with a stored clone.</summary>
    private async Task ResolveNarratorKeyAndCloneAsync()
    {
        try
        {
            var chars = await Engine.GetCharactersAsync(_projectId);
            var withVoice = chars?.Characters?
                .FirstOrDefault(HasVoiceProviderId);
            var narrator = chars?.Characters?
                .FirstOrDefault(IsNarratorCharacter);
            if (narrator is not null)
                _narratorKey = narrator.Key ?? _narratorKey;
            else if (withVoice is not null)
                _narratorKey = withVoice.Key ?? _narratorKey;

            _hasClone = chars?.Characters?.Any(HasCloneForNarratorKey) ?? false;
        }
        catch
        {
            _hasClone = true; // try speak anyway — API will reject if missing
        }
    }

    private static bool HasVoiceProviderId(CharacterSummary c) =>
        !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId);

    private static bool IsNarratorCharacter(CharacterSummary c) =>
        string.Equals(c.Key, "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
        (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false);

    private bool HasCloneForNarratorKey(CharacterSummary c) =>
        string.Equals(c.Key, _narratorKey, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(c.VoiceProviderVoiceId);

    private async Task LoadClipsFromScenesAsync()
    {
        var scenes = await Engine.GetScenesAsync(_projectId);
        if (scenes?.Scenes is not { Count: > 0 }) return;

        foreach (var s in scenes.Scenes.OrderBy(x => x.SceneNumber))
        {
            var detail = await Engine.GetSceneDetailAsync(_projectId, s.SceneNumber);
            if (detail?.Scene?.Clips is not { Count: > 0 } clips) continue;
            foreach (var c in clips.OrderBy(x => x.ClipNumber))
                AddClipRow(s.SceneNumber, c);
        }
    }

    private void AddClipRow(int sceneNumber, ClipSummary c)
    {
        var speaker = (c.Speaker ?? "").Trim();
        var dialogue = (c.Dialogue ?? "").Trim();
        var isNarrator = IsNarratorSpeaker(speaker, _narratorKey);
        _clips.Add(new ClipRow
        {
            Scene = sceneNumber,
            Clip = c.ClipNumber,
            Dialogue = dialogue,
            Speaker = string.IsNullOrEmpty(speaker) ? null : speaker,
            IsNarrator = isNarrator,
            VideoUrl = c.OnDisk ? Engine.ClipVideoUrl(_projectId, sceneNumber, c.ClipNumber) : null,
            Status = InitialClipStatus(isNarrator, dialogue, speaker),
        });
    }

    private static string InitialClipStatus(bool isNarrator, string dialogue, string speaker)
    {
        if (isNarrator && dialogue.Length > 0)
            return "Queued";
        if (isNarrator)
            return "Keep · no line";
        if (string.IsNullOrEmpty(dialogue))
            return "Keep · silent";
        return $"Keep · {ShortSpeaker(speaker)}";
    }

    private string StatusAfterLoad()
    {
        if (_clips.Count == 0)
            return "No clips in this project yet";
        if (_narratorClipCount == 0)
            return "No narrator clips to re-voice";
        return $"{_narratorClipCount} narrator clip(s) · {_keepClipCount} kept as-is";
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

            var counters = new RevoiceCounters();

            for (var i = 0; i < _clips.Count; i++)
            {
                var row = _clips[i];
                _status = $"Clip {i + 1} of {_clips.Count}: S{row.Scene:D2} C{row.Clip:D2}";
                StateHasChanged();
                await ProcessRevoiceClipAsync(row, i, counters);
            }

            ApplyFinishSummary(counters);
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

    private async Task ProcessRevoiceClipAsync(ClipRow row, int i, RevoiceCounters counters)
    {
        try
        {
            // Only re-voice narrator dialogue. Mom / others / silent clips stay original.
            if (!row.WillRevoice)
            {
                HandleKeepClip(row, i, counters);
                return;
            }

            if (!await SpeakAndMuxNarratorClipAsync(row, i, counters))
                return;
        }
        catch (Exception ex)
        {
            row.Status = "Fail";
            _error = ex.Message;
            counters.Fail++;
        }

        _doneCount = i + 1;
        StateHasChanged();
    }

    private void HandleKeepClip(ClipRow row, int i, RevoiceCounters counters)
    {
        row.Status = KeepClipStatus(row);
        counters.Keep++;
        _doneCount = i + 1;
        StateHasChanged();
    }

    private static string KeepClipStatus(ClipRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Dialogue))
            return "Keep · silent";
        if (row.IsNarrator)
            return "Keep · no line";
        return $"Keep · {row.SpeakerLabel}";
    }

    private async Task<bool> SpeakAndMuxNarratorClipAsync(ClipRow row, int i, RevoiceCounters counters)
    {
        row.Status = "Working…";
        StateHasChanged();

        var videoUrl = await ResolveVideoUrlAsync(row);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return MarkRevoiceFail(row, i, counters, "Skip · no picture");

        row.Status = "Speaking…";
        StateHasChanged();
        var speak = await Engine.SpeakVoiceAsync(_projectId, _narratorKey, row.Dialogue);
        if (!speak.Ok)
        {
            _error = speak.Error ?? "Speech failed";
            return MarkRevoiceFail(row, i, counters, "Fail · TTS");
        }
        if (speak.EstimatedUsd is { } u) counters.EstUsd = (counters.EstUsd ?? 0) + u;

        var audioUrl = await AudioUrlFromSpeakAsync(speak);
        if (string.IsNullOrWhiteSpace(audioUrl))
            return MarkRevoiceFail(row, i, counters, "Fail · audio");

        row.Status = "Muxing…";
        StateHasChanged();
        var mux = await Js.InvokeAsync<FfmpegResult>(
            "PageToMovieFfmpeg.replaceVideoAudioAsync", videoUrl, audioUrl);
        if (mux is not { Success: true } || string.IsNullOrWhiteSpace(mux.Url))
            return MarkMuxFail(row, i, counters, mux);

        var outUrl = mux.Url!;
        await TrySaveMuxedClipToMediaFolderAsync(row, outUrl);
        await TryRevokePreviewUrlAsync();
        _previewUrl = outUrl;
        _blobUrls.Add(outUrl);
        row.Status = "Done";
        counters.Ok++;
        return true;
    }

    private bool MarkRevoiceFail(ClipRow row, int i, RevoiceCounters counters, string status)
    {
        row.Status = status;
        counters.Fail++;
        _doneCount = i + 1;
        return false;
    }

    private bool MarkMuxFail(ClipRow row, int i, RevoiceCounters counters, FfmpegResult? mux)
    {
        if (!string.IsNullOrWhiteSpace(mux?.Error))
            _error = mux.Error;
        return MarkRevoiceFail(row, i, counters, "Fail · mux");
    }

    private async Task TrySaveMuxedClipToMediaFolderAsync(ClipRow row, string outUrl)
    {
        if (!MediaFolder.IsConnected) return;
        try
        {
            var clientPath = $"{_projectId}/{row.RelativePath}";
            var saved = await Js.InvokeAsync<SaveBlobResult>(
                "PageToMovieMedia.saveBlobUrlAsync", outUrl, clientPath);
            if (saved is { Success: true } && !string.IsNullOrWhiteSpace(saved.Sha256))
                await TryRegisterSavedMediaAsync(row, saved);
        }
        catch { /* keep going — blob still previewable */ }
    }

    private async Task TryRegisterSavedMediaAsync(ClipRow row, SaveBlobResult saved)
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

    private async Task TryRevokePreviewUrlAsync()
    {
        if (string.IsNullOrEmpty(_previewUrl) || !_previewUrl.StartsWith("blob:", StringComparison.Ordinal))
            return;
        try { await Js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", _previewUrl); }
        catch { /* blob URL may already be revoked */ }
    }

    private void ApplyFinishSummary(RevoiceCounters counters)
    {
        _done = true;
        _status = counters.Fail == 0
            ? $"Done — {counters.Ok} narrator clip(s) re-voiced · {counters.Keep} kept"
            : $"Finished with issues — {counters.Ok} re-voiced, {counters.Fail} failed, {counters.Keep} kept";
        _message = counters.Fail == 0
            ? "Narrator lines use your voice. Other speakers (and silent clips) are unchanged. Open Preview to stitch."
            : "Some narrator clips could not be re-voiced. Fix those (missing video or voice key) and run again.";
        if (counters.EstUsd is > 0)
            _message += $" · TTS ~${counters.EstUsd:0.0000}";
    }

    private sealed class RevoiceCounters
    {
        public int Ok;
        public int Fail;
        public int Keep;
        public double? EstUsd = 0;
    }

    private async Task<string?> ResolveVideoUrlAsync(ClipRow row)
    {
        // 1) Client media folder
        if (MediaFolder.IsConnected)
        {
            try
            {
                var local = await MediaFolder.GetCurrentBlobUrlAsync(_projectId, row.RelativePath, null);
                if (!string.IsNullOrWhiteSpace(local))
                    return local;
            }
            catch { /* fall through */ }
        }

        // 2) Server clip URL
        if (!string.IsNullOrWhiteSpace(row.VideoUrl))
            return Engine.BrowserMediaPath(row.VideoUrl);

        return Engine.BrowserMediaPath(Engine.ClipVideoUrl(_projectId, row.Scene, row.Clip));
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
        foreach (var u in _blobUrls.Distinct().Where(u => u.StartsWith("blob:", StringComparison.Ordinal)))
        {
            try { await Js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", u); } catch { /* blob URL may already be revoked */ }
        }
    }

    private sealed class FfmpegResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public string? Error { get; set; } = null;
    }

    private sealed class SaveBlobResult
    {
        public bool Success { get; set; } = false;
        public string? Sha256 { get; set; } = null;
        public long SizeBytes { get; set; } = 0;
        public string? RelativePath { get; set; } = null;
        public string? Error { get; set; } = null;
    }
}
