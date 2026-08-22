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
    public CutMergeManifest SavedMergeCache { get; private set; } = new();
    public string? MovieMp4Path { get; private set; }
    public string? PictureMp4Path { get; private set; }
    public Dictionary<int, string> SceneCacheFiles { get; } = [];
    public Dictionary<int, string> JoinCacheFiles { get; } = [];
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
        SavedMergeCache = new CutMergeManifest();
        MovieMp4Path = null;
        PictureMp4Path = null;
        SceneCacheFiles.Clear();
        JoinCacheFiles.Clear();
        TextClips.Clear();
        var list = files.ToList();
        var movie = list.FirstOrDefault(f =>
            CutPlayMerge.IsMovieFileName(f.FileName) || CutPlayMerge.IsMovieFileName(f.RelativePath));
        if (movie is not null && movie.SizeBytes > 0)
            MovieMp4Path = string.IsNullOrWhiteSpace(movie.RelativePath) ? movie.FileName : movie.RelativePath;
        foreach (var file in list)
        {
            var path = string.IsNullOrWhiteSpace(file.RelativePath) ? file.FileName : file.RelativePath;
            if (file.SizeBytes <= 0)
                continue;
            if (CutMergeCache.IsPictureFileName(path))
                PictureMp4Path = path;
            else if (CutMergeCache.TryParseSceneFile(path, out var scene))
                SceneCacheFiles[scene] = path;
            else if (CutMergeCache.TryParseJoinFile(path, out var from))
                JoinCacheFiles[from] = path;
        }

        var project = list.FirstOrDefault(f =>
            CutClipNaming.IsProjectFileName(f.FileName) || CutClipNaming.IsProjectFileName(f.RelativePath));
        if (project is not null
            && CutProjectFile.TryApply(
                clips, project.Text, out var music, out var texts, out var fp, out var track, out var cache))
        {
            PendingMusicFileName = music;
            PendingMusic.FileName = track.FileName;
            PendingMusic.DisplayName = track.DisplayName;
            PendingMusic.SetStart(track.StartSec);
            PendingMusic.ApplyInOut(track.MarkIn, track.MarkOut);
            SavedMovieFingerprint = fp;
            SavedMergeCache = cache;
            TextClips.AddRange(texts);
        }
    }

    public async Task<bool> SaveFinishAsync(
        string? musicFileName,
        string? movieFingerprint = null,
        CutMusic? music = null,
        CutMergeManifest? mergeCache = null)
    {
        var json = CutProjectFile.Serialize(Clips, musicFileName, TextClips, movieFingerprint, music, mergeCache);
        var wrote = await _js.InvokeAsync<JsResult>(
            "PageToMovieCut.writeTextFileAsync", CutClipNaming.ProjectFileName, json);
        if (!wrote.Success)
        {
            FolderError = wrote.Error ?? "Could not save the cut.";
            return false;
        }

        FolderError = null;
        SavedMovieFingerprint = movieFingerprint;
        if (mergeCache is not null)
            SavedMergeCache = mergeCache;
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

    public async Task<string?> TryOpenPathAsync(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        var blob = await _js.InvokeAsync<JsResult>("PageToMovieCut.getFileBlobUrlAsync", relativePath);
        return blob.Success ? blob.Url : null;
    }

    public async Task AttachMergeCacheAsync(CutComposeService compose)
    {
        compose.Cache.Clear();
        compose.Cache.Built = CloneManifest(SavedMergeCache);
        foreach (var (scene, path) in SceneCacheFiles)
        {
            var row = SavedMergeCache.Scenes.FirstOrDefault(s => s.Id == scene);
            if (string.IsNullOrWhiteSpace(row.Fingerprint))
                continue;
            var url = await TryOpenPathAsync(path);
            if (!string.IsNullOrWhiteSpace(url))
                compose.Cache.RememberScene(scene, url, row.Fingerprint);
        }

        foreach (var (from, path) in JoinCacheFiles)
        {
            var row = SavedMergeCache.Joins.FirstOrDefault(s => s.Id == from);
            if (string.IsNullOrWhiteSpace(row.Fingerprint))
                continue;
            var url = await TryOpenPathAsync(path);
            if (!string.IsNullOrWhiteSpace(url))
                compose.Cache.RememberJoin(from, url, row.Fingerprint);
        }

        var picturePath = PictureMp4Path ?? SavedMergeCache.PictureFile;
        var picture = await TryOpenPathAsync(picturePath);
        if (!string.IsNullOrWhiteSpace(picture))
            compose.Cache.PictureUrl = picture;
    }

    public async Task<bool> PersistMergeCacheAsync(CutComposeService compose)
    {
        if (!CanWrite)
            return false;
        var wroteAny = false;
        var rebuiltScenes = compose.LastRebuiltScenes.ToHashSet();
        var rebuiltJoins = compose.LastRebuiltJoins.ToHashSet();
        foreach (var scene in compose.CurrentPlan.Scenes)
        {
            if (!compose.Cache.SceneUrls.TryGetValue(scene.Scene, out var url)
                || string.IsNullOrWhiteSpace(url))
                continue;
            if (rebuiltScenes.Count > 0 && !rebuiltScenes.Contains(scene.Scene)
                && SceneCacheFiles.ContainsKey(scene.Scene))
                continue;
            var wrote = await _js.InvokeAsync<JsResult>(
                "PageToMovieCut.writeBlobUrlFileAsync", scene.FileName, url);
            if (wrote.Success)
            {
                SceneCacheFiles[scene.Scene] = scene.FileName;
                wroteAny = true;
            }
        }

        foreach (var join in compose.CurrentPlan.Joins.Where(j => j.Encodes))
        {
            if (!compose.Cache.JoinUrls.TryGetValue(join.FromScene, out var url)
                || string.IsNullOrWhiteSpace(url))
                continue;
            if (rebuiltJoins.Count > 0 && !rebuiltJoins.Contains(join.FromScene)
                && JoinCacheFiles.ContainsKey(join.FromScene))
                continue;
            var wrote = await _js.InvokeAsync<JsResult>(
                "PageToMovieCut.writeBlobUrlFileAsync", join.FileName, url);
            if (wrote.Success)
            {
                JoinCacheFiles[join.FromScene] = join.FileName;
                wroteAny = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(compose.Cache.PictureUrl)
            && (rebuiltScenes.Count > 0 || rebuiltJoins.Count > 0 || string.IsNullOrWhiteSpace(PictureMp4Path)))
        {
            var wrote = await _js.InvokeAsync<JsResult>(
                "PageToMovieCut.writeBlobUrlFileAsync", CutMergeCache.PictureFileName, compose.Cache.PictureUrl);
            if (wrote.Success)
            {
                PictureMp4Path = CutMergeCache.PictureFileName;
                wroteAny = true;
            }
        }

        return wroteAny;
    }

    private static CutMergeManifest CloneManifest(CutMergeManifest src) =>
        new()
        {
            MovieFingerprint = src.MovieFingerprint,
            PictureFingerprint = src.PictureFingerprint,
            MusicFingerprint = src.MusicFingerprint,
            PictureFile = src.PictureFile,
            Scenes = src.Scenes.ToList(),
            Joins = src.Joins.ToList(),
        };

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
