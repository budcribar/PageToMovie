using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class Review : IAsyncDisposable, IPageSliceHost
{
    /// <summary>Slice host (see <see cref="IPageSliceHost"/>): the Review/Play/Share/Finish tabs are slices.</summary>
    public event Action? Rendered;

    public void RenderRequestedBySlice() => StateHasChanged();

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        Rendered?.Invoke();
    }

    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ReviewJobs? _jobs;
    internal ReviewJobs Jobs => _jobs ??= new ReviewJobs(this);
    private ReviewShare? _share;
    internal ReviewShare Share => _share ??= new ReviewShare(this);
    private ReviewAutoReview? _autoReview;
    internal ReviewAutoReview AutoReview => _autoReview ??= new ReviewAutoReview(this);
    private ReviewPlayback? _playback;
    internal ReviewPlayback Playback => _playback ??= new ReviewPlayback(this);
    private ReviewListState? _list;
    internal ReviewListState List => _list ??= new ReviewListState(this);

    internal void EnsureDomains()
    {
        _ = List; _ = Playback; _ = AutoReview; _ = Share; _ = Jobs;
    }


    internal bool _busy;

    internal bool _gateChecked;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";


    internal static string FormatClock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "—";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    internal const string passStatus = "pass";

    internal const string failStatus = "fail";


    internal sealed class EditRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Layer { get; set; } = "clip";
        public string Field { get; set; } = "";
        public string? CharKey { get; set; }
        public string Label { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Rationale { get; set; }
        public bool Include { get; set; } = true;
    }


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        Hub.JobUpdated += Jobs.OnJobUpdated;
        Hub.JobLog += Jobs.OnJobLog;
        MediaFolder.Changed += OnMediaFolderChanged;
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
            if (!ActiveProject.HasProject)
                await ActiveProject.RefreshFromServerAsync(Engine);
            await ActiveProject.RefreshReadinessAsync(Engine);
            await Caps.RefreshAsync(Engine);
            _projectId = ActiveProject.ProjectId ?? "";
            _gateChecked = true;
            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanReview)
            {
                Share.HandleYouTubeOAuthRedirect();
                return;
            }

            try { await Hub.StartAsync(); } catch { /* optional */ }
            await List.LoadAsync();
            await List.ApplyQueryTabAsync();

            // Contextual sync: Review plays this project's media, so pull it to the local folder now
            // (replaces the old sync-on-every-page-load behaviour).
            try
            {
                await MediaFolder.EnsureHubHookAsync();
                if (!MediaFolder.IsConnected) await MediaFolder.TryReconnectAsync();
                MediaFolder.TriggerAutoSyncIfConnected();
            }
            catch { /* media folder optional for browse */ }

            Share.HandleYouTubeOAuthRedirect();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }


    internal (int Scene, int Clip)? _clipServerSrcKey;


    internal static string FormatBytes(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#} MB";
        if (n >= 1_000) return $"{n / 1_000.0:0.#} KB";
        return $"{n} B";
    }


    internal async Task ConfirmSaveAsync()
    {
        Share.CheckIncompleteMovieState();
        if ((Share._incompleteScenesCount > 0 || Share._missingClipsCount > 0) && !Share._confirmedIncompletePublish)
        {
            Share._showIncompleteWarning = true;
            StateHasChanged();
            return;
        }

        Share._showIncompleteWarning = false;
        await Share.PublishDemoAsync();
    }


    internal void OnMediaFolderChanged()
    {
        _ = _gateChecked; // instance-bound for S2325 (Blazor partial hides StateHasChanged)
        _ = InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        Hub.JobUpdated -= Jobs.OnJobUpdated;
        Hub.JobLog -= Jobs.OnJobLog;
        MediaFolder.Changed -= OnMediaFolderChanged;
        Playback._clientWipUrl = null;
        Playback._playingFinishedCut = false;
        Playback._clientSceneUrl = null;
        await Stitch.RevokePreviewUrlAsync();
        Share._dotNetRef?.Dispose();
    }

    // JS interop targets the page type (DotNetObjectReference.Create(this)).
    [JSInvokable]
    public void ReportPublishProgress(int pct, string status) =>
        Share.ReportPublishProgress(pct, status);

}
