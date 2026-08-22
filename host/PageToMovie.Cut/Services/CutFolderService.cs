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
    public bool CanWrite { get; private set; }
    public string? FolderName { get; private set; }
    public string? FolderError { get; private set; }
    public string? PendingMusicFileName { get; private set; }
    public CutMusic PendingMusic { get; } = new();
    public string? SavedMovieFingerprint { get; private set; }
    public string? MovieMp4Path { get; private set; }
    public List<CutClip> Clips { get; private set; } = [];
    public List<CutTextClip> TextClips { get; } = [];

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
        CanWrite = true;
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
        CanWrite = false;
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
        await AttachTakeUrlsAsync(clips);
        Clips = clips;
        ApplySavedFinish(files, clips);
        if (clips.Count == 0)
            FolderError = "No takes named scene_SS_clip_CC_take_NN.mp4 in that folder.";
    }

    private async Task AttachTakeUrlsAsync(List<CutClip> clips)
    {
        foreach (var take in clips.Select(c => c.SelectedTake).OfType<CutTake>().Where(t => !t.Missing))
        {
            var blob = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", take.RelativePath);
            if (blob.Success && !string.IsNullOrWhiteSpace(blob.Url))
            {
                take.PreviewUrl = blob.Url;
                continue;
            }

            take.Missing = true;
            take.MissingReason = blob.Error ?? "Clip is missing.";
        }
    }

    private void ApplySavedFinish(IEnumerable<JsFileEntry> files, List<CutClip> clips)
    {
        PendingMusicFileName = null;
        PendingMusic.Clear();
        SavedMovieFingerprint = null;
        MovieMp4Path = null;
        TextClips.Clear();
        var list = files.ToList();
        var movie = list.FirstOrDefault(f =>
            CutPlayMerge.IsMovieFileName(f.FileName) || CutPlayMerge.IsMovieFileName(f.RelativePath));
        if (movie is not null && movie.SizeBytes > 0)
            MovieMp4Path = string.IsNullOrWhiteSpace(movie.RelativePath) ? movie.FileName : movie.RelativePath;
        var project = list.FirstOrDefault(f =>
            CutClipNaming.IsProjectFileName(f.FileName) || CutClipNaming.IsProjectFileName(f.RelativePath));
        if (project is not null
            && CutProjectFile.TryApply(clips, project.Text, out var music, out var texts, out var fp, out var track))
        {
            PendingMusicFileName = music;
            PendingMusic.FileName = track.FileName;
            PendingMusic.SetStart(track.StartSec);
            PendingMusic.ApplyInOut(track.MarkIn, track.MarkOut);
            SavedMovieFingerprint = fp;
            TextClips.AddRange(texts);
        }
    }

    public async Task<bool> SaveFinishAsync(
        string? musicFileName,
        string? movieFingerprint = null,
        CutMusic? music = null)
    {
        var json = CutProjectFile.Serialize(Clips, musicFileName, TextClips, movieFingerprint, music);
        var wrote = await _js.InvokeAsync<JsResult>(
            "PageToMovieCut.writeTextFileAsync", CutClipNaming.ProjectFileName, json);
        if (!wrote.Success)
        {
            FolderError = wrote.Error ?? "Could not save the cut.";
            return false;
        }

        FolderError = null;
        SavedMovieFingerprint = movieFingerprint;
        return true;
    }

    public async Task<string?> TryOpenMovieMp4Async()
    {
        if (string.IsNullOrWhiteSpace(MovieMp4Path))
            return null;
        var blob = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", MovieMp4Path);
        return blob.Success ? blob.Url : null;
    }

    public async Task<bool> WriteMovieMp4Async(string url)
    {
        if (!CanWrite || string.IsNullOrWhiteSpace(url))
            return false;
        var wrote = await _js.InvokeAsync<JsResult>(
            "PageToMovieCut.writeBlobUrlFileAsync", CutPlayMerge.MovieFileName, url);
        if (!wrote.Success)
            return false;
        MovieMp4Path = CutPlayMerge.MovieFileName;
        return true;
    }

    private async Task RevokePreviewUrlsAsync()
    {
        var urls = Clips.SelectMany(c => c.Takes)
            .Select(t => t.PreviewUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u));
        foreach (var url in urls)
        {
            try
            {
                await _js.InvokeVoidAsync("PageToMovieCut.revokeBlobUrl", url);
            }
            catch (JSException)
            {
                // Best-effort revoke on folder change / dispose.
            }
        }
    }

    public async ValueTask DisposeAsync() => await RevokePreviewUrlsAsync();
}
