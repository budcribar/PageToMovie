using Microsoft.AspNetCore.Components;
using PageToMovie.Cut.Services;
using PageToMovie.Web.Services;
using PageToMovie.Web.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ReviewFinishTab : PageSliceComponent
{
    [CascadingParameter] public Review.ReviewListState? List { get; set; }

    [Inject] public CutFolderService Folder { get; set; } = default!;

    [Inject] public ClientMediaFolderService MediaFolder { get; set; } = default!;

    private bool _subscribed;
    private bool _downloading;
    private string? _downloadStatus;

    /// <summary>
    /// Pulls the project's finished clips into this computer's media folder, then re-reads the
    /// folder so the timeline picks them up. The clips play on the Film step straight off the
    /// server, which is why their absence here is easy to miss - the cut is the one place that can
    /// only use what is on this machine.
    /// </summary>
    internal async Task DownloadMissingClipsAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _downloadStatus = null;
        try
        {
            await MediaFolder.SyncProjectMediaToClientAsync(ActiveProject.ProjectId ?? "");
            await Folder.RefreshClipsAsync();
        }
        catch (Exception ex)
        {
            _downloadStatus = ex.Message;
        }
        finally
        {
            _downloading = false;
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Folder.ClipsChanged += OnFolderClipsChanged;
        _subscribed = true;
    }

    // The folder attaches after this renders, so without this the notice would be decided against
    // an empty clip list and never revisited - right only by accident, on whatever redraw came next.
    private void OnFolderClipsChanged() => InvokeAsync(StateHasChanged);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _subscribed)
        {
            Folder.ClipsChanged -= OnFolderClipsChanged;
            _subscribed = false;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The cut is assembled from whatever video files are in this browser's media folder. Nothing
    /// in it knows the shot plan, so a project with seventeen scenes and two files on hand quietly
    /// produces a nine-second movie — the timeline looks finished because it is showing everything
    /// it has. This reconciles the three counts the page already knows and says which of the two
    /// very different things went wrong: the clips were never made, or they were made somewhere
    /// this browser cannot see.
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
    /// Splits the shortfall into the two causes that need different answers from the operator.
    /// Clips finished but absent here are a folder problem; clips absent everywhere are unfinished
    /// work. Note the server count can legitimately sit <b>below</b> the local one - it prunes clip
    /// bytes once a browser has synced them - so it is only ever evidence that something exists,
    /// never that something is missing.
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
