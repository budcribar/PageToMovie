using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Services;

/// <summary>
/// Local folder (File System Access API) — clips stay in the browser; nothing is uploaded.
/// </summary>
public sealed class CutFolderService : IAsyncDisposable
{
    private const string WriteBlobUrlFileJs = "PageToMovieCut.writeBlobUrlFileAsync";

    private readonly IJSRuntime _js;

    public CutFolderService(IJSRuntime js) => _js = js;

    public bool HasFolder { get; private set; }
    public bool CanWrite { get; private set; }
    public string? FolderName { get; private set; }
    public string? FolderError { get; private set; }
    public string? PendingMusicFileName { get; private set; }
    public string? MusicFileOnDisk { get; private set; }
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
        ResetSavedFinish();
        var list = files.ToList();
        ApplySavedMoviePath(list);
        ApplySavedCacheFiles(list);
        ApplySavedProject(list, clips);
    }

    private void ResetSavedFinish()
    {
        PendingMusicFileName = null;
        MusicFileOnDisk = null;
        PendingMusic.Clear();
        SavedMovieFingerprint = null;
        SavedMergeCache = new CutMergeManifest();
        MovieMp4Path = null;
        PictureMp4Path = null;
        SceneCacheFiles.Clear();
        JoinCacheFiles.Clear();
        TextClips.Clear();
    }

    private void ApplySavedMoviePath(IReadOnlyList<JsFileEntry> list)
    {
        var movie = list.FirstOrDefault(f =>
            CutPlayMerge.IsMovieFileName(f.FileName) || CutPlayMerge.IsMovieFileName(f.RelativePath));
        if (movie is not null && movie.SizeBytes > 0)
            MovieMp4Path = RelativeOrFileName(movie);
    }

    private void ApplySavedCacheFiles(IReadOnlyList<JsFileEntry> list)
    {
        foreach (var file in list)
        {
            if (file.SizeBytes <= 0)
                continue;
            RememberCacheFile(RelativeOrFileName(file));
        }
    }

    private void RememberCacheFile(string path)
    {
        if (CutMergeCache.IsPictureFileName(path))
            PictureMp4Path = path;
        else if (CutMergeCache.TryParseSceneFile(path, out var scene))
            SceneCacheFiles[scene] = path;
        else if (CutMergeCache.TryParseJoinFile(path, out var from))
            JoinCacheFiles[from] = path;
    }

    private static string RelativeOrFileName(JsFileEntry file) =>
        string.IsNullOrWhiteSpace(file.RelativePath) ? file.FileName : file.RelativePath;

    private static bool ListedAudioMatches(IReadOnlyList<JsFileEntry> list, string? musicFileName)
    {
        var name = CutMusicPersist.FileNameOf(musicFileName);
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return list.Any(file =>
            file.SizeBytes > 0
            && (string.Equals(CutClipNaming.FileNameOnly(file.FileName), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(CutClipNaming.FileNameOnly(file.RelativePath), name, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplySavedProject(IReadOnlyList<JsFileEntry> list, List<CutClip> clips)
    {
        var project = list.FirstOrDefault(f =>
            CutClipNaming.IsProjectFileName(f.FileName) || CutClipNaming.IsProjectFileName(f.RelativePath));
        if (project is null
            || !CutProjectFile.TryApply(
                clips, project.Text, out var music, out var texts, out var fp, out var track, out var cache))
            return;

        PendingMusicFileName = music;
        if (ListedAudioMatches(list, music))
            MusicFileOnDisk = CutMusicPersist.FileNameOf(music);
        PendingMusic.FileName = track.FileName;
        PendingMusic.DisplayName = track.DisplayName;
        PendingMusic.SetStart(track.StartSec);
        PendingMusic.ApplyInOut(track.MarkIn, track.MarkOut);
        PendingMusic.SetVolumePercent(track.VolumePercent);
        PendingMusic.SetFadeIn(track.FadeInSec);
        PendingMusic.SetFadeOut(track.FadeOutSec);
        SavedMovieFingerprint = fp;
        SavedMergeCache = cache;
        TextClips.AddRange(texts);
    }

    public async Task<bool> WriteMusicFileAsync(string? fileName, string? url)
    {
        if (!CutMusicPersist.TryPlanWrite(CanWrite, fileName, url, fileOnDisk: null, force: true, out var write))
            return false;
        if (!await TryWriteBlobUrlFileAsync(write.FileName, write.Url))
            return false;
        MusicFileOnDisk = write.FileName;
        PendingMusicFileName = write.FileName;
        return true;
    }

    public async Task<bool> SaveFinishAsync(
        string? musicFileName,
        string? movieFingerprint = null,
        CutMusic? music = null,
        CutMergeManifest? mergeCache = null,
        string? musicUrl = null)
    {
        if (string.IsNullOrWhiteSpace(musicFileName))
            MusicFileOnDisk = null;
        else if (CutMusicPersist.NeedsFlushOnSave(musicFileName, MusicFileOnDisk, musicUrl)
            && !await WriteMusicFileAsync(musicFileName, musicUrl))
        {
            FolderError = "Could not save the music file.";
            return false;
        }

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
        if (!await TryWriteBlobUrlFileAsync(CutPlayMerge.MovieFileName, url))
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
        var rebuiltScenes = compose.LastRebuiltScenes.ToHashSet();
        var rebuiltJoins = compose.LastRebuiltJoins.ToHashSet();
        var wroteScenes = await PersistCacheFilesAsync(
            compose.CurrentPlan.Scenes.Select(s => (s.Scene, s.FileName)),
            compose.Cache.SceneUrls,
            SceneCacheFiles,
            rebuiltScenes);
        var wroteJoins = await PersistCacheFilesAsync(
            compose.CurrentPlan.Joins.Where(j => j.Encodes).Select(j => (j.FromScene, j.FileName)),
            compose.Cache.JoinUrls,
            JoinCacheFiles,
            rebuiltJoins);
        var wrotePicture = await PersistPictureCacheAsync(
            compose.Cache.PictureUrl,
            rebuiltScenes.Count > 0 || rebuiltJoins.Count > 0);
        return wroteScenes || wroteJoins || wrotePicture;
    }

    private async Task<bool> PersistCacheFilesAsync(
        IEnumerable<(int Id, string FileName)> items,
        IReadOnlyDictionary<int, string> urls,
        Dictionary<int, string> dest,
        HashSet<int> rebuilt)
    {
        var wroteAny = false;
        foreach (var (id, fileName) in items)
        {
            if (!urls.TryGetValue(id, out var url) || string.IsNullOrWhiteSpace(url))
                continue;
            if (ShouldSkipUnchangedCache(rebuilt, id, dest.ContainsKey(id)))
                continue;
            if (!await TryWriteBlobUrlFileAsync(fileName, url))
                continue;
            dest[id] = fileName;
            wroteAny = true;
        }

        return wroteAny;
    }

    private static bool ShouldSkipUnchangedCache(HashSet<int> rebuilt, int id, bool alreadyOnDisk) =>
        rebuilt.Count > 0 && !rebuilt.Contains(id) && alreadyOnDisk;

    private async Task<bool> PersistPictureCacheAsync(string? pictureUrl, bool rebuiltAny)
    {
        if (string.IsNullOrWhiteSpace(pictureUrl))
            return false;
        if (!rebuiltAny && !string.IsNullOrWhiteSpace(PictureMp4Path))
            return false;
        if (!await TryWriteBlobUrlFileAsync(CutMergeCache.PictureFileName, pictureUrl))
            return false;
        PictureMp4Path = CutMergeCache.PictureFileName;
        return true;
    }

    private async Task<bool> TryWriteBlobUrlFileAsync(string fileName, string url)
    {
        var wrote = await _js.InvokeAsync<JsResult>(WriteBlobUrlFileJs, fileName, url);
        return wrote.Success;
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
