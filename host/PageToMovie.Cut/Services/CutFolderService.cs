using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Services;

/// <summary>
/// Local folder (File System Access API) — clips stay in the browser; nothing is uploaded.
/// </summary>
public sealed class CutFolderService : IAsyncDisposable
{
    private readonly IJSRuntime _js;

    public CutFolderService(IJSRuntime js) => _js = js;

    public bool HasFolder { get; private set; }
    public string? FolderName { get; private set; }
    public string? FolderError { get; private set; }
    public IReadOnlyList<CutClip> Clips { get; private set; } = [];

    public async Task<bool> BrowserSupportsFolderPickerAsync()
    {
        var r = await _js.InvokeAsync<JsResult>("PageToMovieCut.supportsDirectoryPicker");
        return r.Supported;
    }

    public async Task PickFolderAsync()
    {
        FolderError = null;
        var pick = await _js.InvokeAsync<JsResult>("PageToMovieCut.pickFolderAsync");
        if (!pick.Success)
        {
            FolderError = pick.Error ?? "Folder selection failed.";
            return;
        }

        FolderName = pick.FolderName;
        HasFolder = true;
        await LoadClipsFromCurrentFolderAsync();
    }

    public async Task PickMp4FilesFallbackAsync()
    {
        FolderError = null;
        var pick = await _js.InvokeAsync<JsResult>("PageToMovieCut.pickMp4FilesAsync");
        if (!pick.Success)
        {
            FolderError = pick.Error ?? "File selection failed.";
            return;
        }

        FolderName = pick.FolderName ?? "Selected files";
        HasFolder = true;
        await ApplyListedFilesAsync(pick.Files);
    }

    private async Task LoadClipsFromCurrentFolderAsync()
    {
        var listed = await _js.InvokeAsync<JsResult>("PageToMovieCut.listMediaFilesAsync");
        if (!listed.Success)
        {
            FolderError = listed.Error ?? "Could not read the folder.";
            Clips = [];
            return;
        }

        await ApplyListedFilesAsync(listed.Files);
    }

    private async Task ApplyListedFilesAsync(IEnumerable<JsFileEntry> files)
    {
        await RevokePreviewUrlsAsync();

        var found = files.Select(f => new FoundMediaFile(
            string.IsNullOrWhiteSpace(f.FileName) ? f.RelativePath : f.FileName,
            f.RelativePath,
            f.SizeBytes,
            f.Text));
        var clips = CutClipList.FromFiles(found).ToList();

        foreach (var clip in clips)
        {
            foreach (var take in clip.Takes)
            {
                if (take.Missing)
                    continue;
                var blob = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", take.RelativePath);
                if (!blob.Success || string.IsNullOrWhiteSpace(blob.Url))
                {
                    take.Missing = true;
                    take.MissingReason = blob.Error ?? "Clip is missing.";
                    continue;
                }

                take.PreviewUrl = blob.Url;
            }
        }

        Clips = clips;
        if (clips.Count == 0)
            FolderError = "No takes named scene_SS_clip_CC_take_NN.mp4 in that folder.";
    }

    /// <summary>
    /// Switch the in-memory current take and persist <c>.current.json</c>. Never writes an alias MP4.
    /// </summary>
    public async Task SetCurrentTakeAsync(CutClip clip, int take)
    {
        clip.SelectTake(take);
        if (string.IsNullOrWhiteSpace(clip.PointerRelativePath))
            clip.PointerRelativePath = CutClipNaming.PointerPathBeside(clip.RelativePath, clip.Scene, clip.Clip);
        if (string.IsNullOrWhiteSpace(clip.PointerRelativePath))
            return;
        var json = CutClipNaming.CurrentPointerJson(clip.Scene, clip.Clip, take);
        var wrote = await _js.InvokeAsync<JsResult>("PageToMovieCut.writeTextFileAsync", clip.PointerRelativePath, json);
        if (!wrote.Success && !string.IsNullOrWhiteSpace(wrote.Error))
            FolderError = wrote.Error;
    }

    private async Task RevokePreviewUrlsAsync()
    {
        foreach (var clip in Clips)
        {
            foreach (var take in clip.Takes)
            {
                if (string.IsNullOrWhiteSpace(take.PreviewUrl))
                    continue;
                try
                {
                    await _js.InvokeVoidAsync("PageToMovieCut.revokeBlobUrl", take.PreviewUrl);
                }
                catch
                {
                    // Best-effort revoke on folder change / dispose.
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await RevokePreviewUrlsAsync();
}
