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

public partial class Scenes : IAsyncDisposable, IPageSliceHost
{
    private CancellationTokenSource? _mediaFolderChangedDebounce;
    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the Scenes_* pieces are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }

    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ScenesHistory? _history;
    internal ScenesHistory History => _history ??= new ScenesHistory(this);
    private ScenesMusic? _music;
    internal ScenesMusic Music => _music ??= new ScenesMusic(this);
    private ScenesDialogueVerify? _dialogue;
    internal ScenesDialogueVerify Dialogue => _dialogue ??= new ScenesDialogueVerify(this);
    private ScenesPlayback? _playback;
    internal ScenesPlayback Playback => _playback ??= new ScenesPlayback(this);
    private ScenesGeneration? _gen;
    internal ScenesGeneration Gen => _gen ??= new ScenesGeneration(this);
    private ScenesListState? _list;
    internal ScenesListState List => _list ??= new ScenesListState(this);
    private ScenesClipSelection? _clipSel;
    internal ScenesClipSelection ClipSel => _clipSel ??= new ScenesClipSelection(this);
    private ScenesClipForm? _clipForm;
    internal ScenesClipForm ClipForm => _clipForm ??= new ScenesClipForm(this);
    private ScenesClipVersions? _clipVer;
    internal ScenesClipVersions ClipVer => _clipVer ??= new ScenesClipVersions(this);
    private ScenesClipRegen? _clipRegen;
    internal ScenesClipRegen ClipRegen => _clipRegen ??= new ScenesClipRegen(this);

    /// <summary>Eagerly construct all domain modules (optional; lazy props also work).</summary>
    internal void EnsureDomains()
    {
        _ = List; _ = ClipSel; _ = ClipForm; _ = ClipVer; _ = ClipRegen; _ = Gen; _ = Playback; _ = Dialogue; _ = Music; _ = History;
    }



    internal bool IsSimpleFilm =>
        ActiveProject.IsSimpleVoice
        || (Nav.ToAbsoluteUri(Nav.Uri).Query?.Contains("simple=1", StringComparison.OrdinalIgnoreCase) ?? false);



    internal bool _busy;


    internal bool _gateChecked;


    internal string? _error;


    internal string? _message;


    internal string _projectId = "";


    internal List<string> _projectIds = new();



    internal bool DetailLockedByOther =>
        List._detail is not null &&
        (List._scenes?.FirstOrDefault(s => s.SceneNumber == List._detail.SceneNumber)?.LockedByOther ?? false);



    internal string? _detailLockOwner =>
        List._detail is null
            ? null
            : List._scenes?.FirstOrDefault(s => s.SceneNumber == List._detail.SceneNumber)?.LockOwnerUserId;



    internal List<string> GetCharacterOptions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (List._scenes is not null)
        {
            foreach (var s in List._scenes)
                AddNonEmptyNames(set, s.CharactersOnScreen);
        }
        if (List._detail is not null)
        {
            AddNonEmptyNames(set, List._detail.CharactersOnScreen);
            if (List._detail.Clips is not null)
            {
                foreach (var cl in List._detail.Clips)
                    AddNonEmptyNames(set, cl.CharactersOnScreen);
            }
        }
        AddNonEmptyNames(set, ClipForm._clipEditorCast);
        AddNonEmptyNames(set, List._castMissing);
        return set.OrderBy(c => ShortChar(c), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddNonEmptyNames(HashSet<string> set, IEnumerable<string>? names)
    {
        if (names is null) return;
        foreach (var c in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            set.Add(c);
    }



    internal List<string> GetLocationOptions() =>
        List._scenes is null
            ? new List<string>()
            : List._scenes
                .SelectMany(s =>
                {
                    var list = new List<string>(s.LocationIds);
                    if (!string.IsNullOrWhiteSpace(s.PrimaryLocationId))
                        list.Add(s.PrimaryLocationId);
                    return list;
                })
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ShortLoc, StringComparer.OrdinalIgnoreCase)
                .ToList();



    internal Task AdjustFitLengthAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/screenplay?tool=fit");



    internal Task AdjustEmbellishAsync() => ConfirmScreenplayAdjustAndNavigateAsync("adaptation/screenplay?tool=enrich");



    // Read-only: just open the screenplay view. Unlike Fit length / Embellish it does not re-open
    // (un-approve) the screenplay, so no confirm — the user asked to "go back and see" it from Film.
    internal void ViewScreenplay() => Nav.NavigateTo("adaptation/screenplay");



    /// <summary>
    /// Navigate to a screenplay-shaping step (Fit length / Enrich). Those edit the screenplay, which
    /// un-approves it and re-gates cast, so confirm first rather than surprising the user mid-Film.
    /// </summary>
    internal async Task ConfirmScreenplayAdjustAndNavigateAsync(string route)
    {
        if (Gen.JobRunning) return;
        var ok = await JS.InvokeAsync<bool>(
            "confirm",
            "This opens the screenplay to change it. You'll re-approve the screenplay afterward, " +
            "and the cast will re-check against the updated script. Continue?");
        if (ok)
            Nav.NavigateTo(route);
    }



    internal void ResetPickers()
    {
        List._pickSetting = "";
        List._pickCharacter = "";
        List._pickLocation = "";
    }



    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        EnsureDomains();
        await ActiveProject.EnsureLoadedAsync(Engine);
        Hub.JobUpdated += Gen.OnJobUpdated;
        Hub.JobLog += Gen.OnJobLog;
        MediaFolder.Changed += OnMediaFolderChanged;
        try
        {
            var loaded = await StudioPageBootstrap.LoadActiveProjectAsync(
                Engine, Session, ActiveProject, Caps, () => _gateChecked = true);
            _projectId = loaded.ProjectId;
            _projectIds = loaded.ProjectIds;

            await List.LoadGenResolutionFromConfigAsync();
            await Music.LoadAudioModelsAsync();
            if (Session.IsAdmin)
                await Gen.LoadVideoModelsAsync();

            try
            {
                await Hub.StartAsync();
                await MediaFolder.EnsureHubHookAsync();
                // Contextual sync: pull this project's media to the local folder now that we're
                // actually in it (this replaces the old sync-on-every-page-load behaviour).
                if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
                MediaFolder.TriggerAutoSyncIfConnected();
            }
            catch { /* SignalR / media folder optional for browse */ }

            var jobs = await Engine.GetJobAsync();
            Gen._job = jobs?.Job;
            if (Session.IsAdmin)
                await Gen.RefreshMyJobsAsync();

            await List.ReloadListAsync();
            // Folder connected and the server is missing clips it once had: push the sidecars back.
            await TryRestoreSidecarsOnceAsync();

            // If the shot plan hasn't been built yet on this project, automatically kick off building the shot plan.
            if (!IsSimpleFilm && (List._scenes is null || List._scenes.Count == 0) && !Gen.JobRunning)
            {
                await List.RebuildShotPlanAsync();
            }

            // Deep-link from screenplay outline: /scenes?scene=12&play=1
            await TryOpenSceneFromQueryAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }

    /// <summary>Open a scene (and optionally play video) from ?scene=&play= query.</summary>
    internal async Task TryOpenSceneFromQueryAsync()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (!q.TryGetValue("scene", out var sceneVals)
                || !int.TryParse(sceneVals.FirstOrDefault(), out var sn)
                || sn <= 0)
                return;

            await List.OpenSceneAsync(sn);

            var play = q.TryGetValue("play", out var playVals)
                       && playVals.Any(v => string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
            if (play)
                await Playback.PlaySceneCompositeAsync(sn);
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
    }

    private bool _sidecarRestoreRan;

    /// <summary>Folder connected (or re-granted) after the page loaded: push any clip sidecars the
    /// server is missing — the init-time pass could not run without the folder. Once per page.</summary>
    private async Task TryRestoreSidecarsOnceAsync()
    {
        if (_sidecarRestoreRan || !MediaFolder.IsConnected || string.IsNullOrEmpty(_projectId)) return;
        _sidecarRestoreRan = true;
        try
        {
            // Renames first: a reorder done on another machine (or before this folder connected)
            // must land before the sidecar pass looks files up by their new names.
            var renamed = await MediaFolder.ApplyServerRenamesAsync(_projectId);
            var restored = await MediaFolder.RestoreMissingClipSidecarsAsync(_projectId, List._scenes);
            if (renamed > 0 || restored > 0)
                await List.ReloadListAsync();
        }
        catch { /* best effort */ }
    }

    internal void OnMediaFolderChanged()
    {
        _mediaFolderChangedDebounce?.Cancel();
        _mediaFolderChangedDebounce?.Dispose();
        var debounce = _mediaFolderChangedDebounce = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            if (MediaFolder.IsSyncing)
                return;
            try
            {
                await Task.Delay(100, debounce.Token);
                await TryRestoreSidecarsOnceAsync();
                await Playback.RefreshLocalPlayableAsync();
                if (Playback._showScenePlayer && Playback._playingScene is int sn && string.IsNullOrEmpty(Playback._clientSceneUrl))
                    await Playback.PlaySceneCompositeAsync(sn);
                StateHasChanged();
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested) { }
        });
    }



    internal void DismissLocalSaveWarning() => MediaFolder.DismissLocalSaveWarning();



    internal async Task ConnectMediaFolderFromWarningAsync()
    {
        try
        {
            if (MediaFolder.NeedsReconnect)
                await MediaFolder.ReconnectAsync();
            else
                await MediaFolder.ConnectFolderAsync();
            await MediaFolder.EnsureHubHookAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }



    internal async Task OnProjectChangedAsync()
    {
        List._selectedScene = null;
        List._detail = null;
        ClipForm._selectedClip = null;
        ClipForm._clip = null;
        List._selected.Clear();
        ResetPickers();
        await List.LoadGenResolutionFromConfigAsync();
        await List.ReloadListAsync();
    }



    internal static string StatusBadge(string status) => status switch
    {
        "complete" => "bg-success",
        "partial" => "bg-warning text-dark",
        _ => "bg-secondary",
    };



    internal static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }



    internal static string ShortChar(string key) => KeyFormatting.ShortChar(key);



    internal static string ShortLoc(string key) => KeyFormatting.ShortLoc(key);



    internal static string ShortDelivery(string? key) => KeyFormatting.ShortDelivery(key);



    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024):0.#} MB";
    }



    internal static string CacheBust(string url) => KeyFormatting.CacheBust(url);



    /// <summary>Format seconds as m:ss or plain seconds when under a minute.</summary>
    internal static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var whole = (int)Math.Round(seconds);
        if (whole < 60) return $"{whole}s";
        var m = whole / 60;
        var s = whole % 60;
        return $"{m}:{s:D2}";
    }


    /// <summary>
    /// xAI's /v1/videos/edits input cap (grok-imagine-video-edit's maxEditInputDurationSeconds).
    /// A client-side UX hint only — RunVideoEditAsync re-checks the real catalog value
    /// server-side and is the authoritative gate; this just disables the button early.
    /// </summary>
    internal const double MaxVideoEditInputSeconds = 8.7;


    internal Dictionary<string, List<string>> _musicCompareUrls = new(StringComparer.OrdinalIgnoreCase);



    internal UncommittedStatusDto? _uncommittedStatus;



    internal async Task RefreshUncommittedStatusAsync()
    {
        try
        {
            var res = await Engine.GetProjectUncommittedStatusAsync(_projectId);
            _uncommittedStatus = res?.Status;
        }
        catch { /* best effort */ }
    }



    internal async Task CommitCurrentChangesAsync()
    {
        _busy = true;
        _message = null;
        _error = null;
        StateHasChanged();

        try
        {
            var res = await Engine.CommitProjectChangesAsync(_projectId, "Manual scene/clip commit");
            if (res.Ok)
            {
                _message = "Successfully committed project changes.";
                await RefreshUncommittedStatusAsync();
            }
            else
            {
                _error = res.Error ?? "Failed to commit changes.";
            }
        }
        catch (Exception ex)
        {
            _error = $"Commit failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }



    internal void OpenVerificationModal(int sceneNumber, int clipNumber, ClipDialogueVerificationResult ver)
    {
        Dialogue._verifModalSceneNumber = sceneNumber;
        Dialogue._verifModalClipNumber = clipNumber;
        Dialogue._verifModalResult = ver;
        Dialogue._showVerificationModal = true;
    }



    internal void CloseVerificationModal()
    {
        Dialogue._showVerificationModal = false;
        Dialogue._verifModalResult = null;
    }



    public async ValueTask DisposeAsync()
    {
        Hub.JobUpdated -= Gen.OnJobUpdated;
        Hub.JobLog -= Gen.OnJobLog;
        MediaFolder.Changed -= OnMediaFolderChanged;
        _mediaFolderChangedDebounce?.Cancel();
        _mediaFolderChangedDebounce?.Dispose();
        Playback._clientPreviewUrl = null;
        Playback._clientSceneUrl = null;
        await Stitch.RevokePreviewUrlAsync();
    }
}
