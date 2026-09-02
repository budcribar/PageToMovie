using Microsoft.AspNetCore.Components;
using PageToMovie.Cut.Services;
using PageToMovie.Web.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class ReviewFinishTab : PageSliceComponent
{
    [CascadingParameter] public Review.ReviewListState? List { get; set; }

    [Inject] public CutFolderService Folder { get; set; } = default!;

    [Inject] public ClientMediaFolderService MediaFolder { get; set; } = default!;

    private bool _subscribed;
    private bool _fetching;
    private bool _autoFetchTried;
    private string? _fetchError;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Folder.ClipsChanged += OnFolderClipsChanged;
        MediaFolder.Changed += OnSyncProgress;
        _subscribed = true;
    }

    // The folder attaches after this renders, so without this the gap would be measured against an
    // empty clip list and never revisited - right only by accident, on whatever redraw came next.
    private void OnFolderClipsChanged() => InvokeAsync(async () =>
    {
        StateHasChanged();
        await AutoFetchOnceAsync();
    });

    private void OnSyncProgress() => InvokeAsync(StateHasChanged);

    // The folder scan and the page's scene list land in either order, and the scan raises its event
    // exactly once. Checking again after each render means whichever arrives second still starts
    // the fetch, instead of the attempt depending on which race won.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        await AutoFetchOnceAsync();
    }

    /// <summary>
    /// Fetches the finished clips this computer does not have, without being asked. Every other
    /// surface plays the server's copy, so nothing before this point ever needed the files locally
    /// and the operator has no reason to suspect they are absent - they only find out by getting a
    /// short movie. Once per visit: a failure or a genuinely unfinished project must not turn into
    /// a retry loop, and the notice keeps a manual retry either way.
    /// </summary>
    private async Task AutoFetchOnceAsync()
    {
        if (_autoFetchTried || _fetching)
            return;
        if (Missing() is not { NotHere: > 0 })
            return;
        _autoFetchTried = true;
        await FetchMissingClipsAsync();
    }

    internal async Task FetchMissingClipsAsync()
    {
        if (_fetching) return;
        _fetching = true;
        _fetchError = null;
        try
        {
            await MediaFolder.SyncProjectMediaToClientAsync(ActiveProject.ProjectId ?? "");
            await Folder.RefreshClipsAsync();
        }
        catch (Exception ex)
        {
            _fetchError = ex.Message;
        }
        finally
        {
            _fetching = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _subscribed)
        {
            Folder.ClipsChanged -= OnFolderClipsChanged;
            MediaFolder.Changed -= OnSyncProgress;
            _subscribed = false;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The cut is assembled from whatever video files are on this computer. Nothing in it knows the
    /// shot plan, so a project with seventeen scenes and two files on hand quietly produces a
    /// nine-second movie - the timeline looks finished because it is showing everything it has.
    /// This reconciles the three counts the page already knows.
    /// </summary>
    internal MissingClipsNotice? Missing()
    {
        var scenes = List?._scenes;
        if (scenes is null || scenes.Count == 0)
            return null;

        var planned = scenes.Sum(s => s.ClipCount);
        if (planned <= 0)
            return null;

        // Attaching the folder is async and may not have happened yet; an empty folder before the
        // attach lands is not evidence of anything.
        if (!Folder.HasFolder)
            return null;

        return Reconcile(planned, scenes.Sum(s => s.ClipsOnDisk), Folder.Clips.Count);
    }

    /// <summary>
    /// Splits the shortfall into the two causes that need different answers. Clips finished but
    /// absent here can simply be fetched; clips absent everywhere are unfinished work. Note the
    /// server count can legitimately sit <b>below</b> the local one - it prunes clip bytes once a
    /// browser has synced them - so it is only ever evidence that something exists, never that
    /// something is missing.
    /// </summary>
    internal static MissingClipsNotice? Reconcile(int planned, int onServer, int local)
    {
        if (planned <= 0 || local >= planned)
            return null;
        return new MissingClipsNotice(
            Planned: planned,
            Local: local,
            NotHere: Math.Max(0, Math.Min(onServer, planned) - local),
            NotMade: Math.Max(0, planned - Math.Max(onServer, local)));
    }

    internal sealed record MissingClipsNotice(int Planned, int Local, int NotHere, int NotMade);
}
