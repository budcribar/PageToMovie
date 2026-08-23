using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutMusicPersistTests
{
    [Fact]
    public void Save_cut_writes_the_audio_file_not_just_json()
    {
        var name = "Mary Had A Little Lamb (vocal) - The Green Orbs.mp3";
        var url = "blob:cut-music";
        var folder = new CutMusicFolderProbe();
        var music = new CutMusic { FileName = name };
        music.SetStart(0);
        music.ApplyInOut(0, 12);

        Assert.True(folder.SaveFinish(name, music, url, canWrite: true));
        var write = Assert.Single(folder.Writes);
        Assert.Equal(name, write.FileName);
        Assert.Equal(url, write.Url);
        Assert.Equal(name, folder.FileOnDisk);
        Assert.Contains(name, folder.SavedJson, StringComparison.Ordinal);
        Assert.Contains("musicFileName", folder.SavedJson, StringComparison.OrdinalIgnoreCase);

        var clip = NewClip(1, 1, 8);
        Assert.True(CutProjectFile.TryApply([clip], folder.SavedJson, out var loadedName, out _, out _, out var loaded));
        Assert.Equal(name, loadedName);
        Assert.Equal(name, loaded.FileName);
        Assert.True(CutMusicPersist.IsAudioFileName(loadedName));
    }

    [Fact]
    public void Save_cut_flushes_bytes_if_drop_did_not()
    {
        var name = "score.wav";
        var url = "blob:cut-score";
        Assert.True(CutMusicPersist.NeedsFlushOnSave(name, fileOnDisk: null, url));
        Assert.True(CutMusicPersist.TryPlanWrite(
            canWrite: true, name, url, fileOnDisk: null, force: false, out var write));
        Assert.Equal(name, write.FileName);
        Assert.Equal(url, write.Url);

        var folder = new CutMusicFolderProbe();
        Assert.True(folder.SaveFinish(name, new CutMusic { FileName = name }, url, canWrite: true));
        Assert.Equal(name, Assert.Single(folder.Writes).FileName);

        Assert.False(CutMusicPersist.NeedsFlushOnSave(name, folder.FileOnDisk, url));
        Assert.True(folder.SaveFinish(name, new CutMusic { FileName = name }, url, canWrite: true));
        Assert.Single(folder.Writes);
    }

    [Fact]
    public void Reopen_applies_the_saved_file_name()
    {
        var clip = NewClip(1, 1, 8);
        var name = "ocean-sunrise.m4a";
        var json = CutProjectFile.Serialize([clip], name, music: new CutMusic { FileName = name });
        var reload = NewClip(1, 1, 8);
        Assert.True(CutProjectFile.TryApply([reload], json, out var file, out _, out _, out var track));
        Assert.Equal(name, file);
        Assert.Equal(name, track.FileName);
        Assert.True(CutMusicPersist.IsAudioFileName(file));
    }

    [Fact]
    public void Adding_music_while_composing_still_does_not_clear_the_merge()
    {
        var queue = new CutMusicQueue();
        var forgot = false;
        var name = "score.mp3";
        var url = "blob:cut-score";
        var folder = new CutMusicFolderProbe();

        queue.AttachFile(composing: true, () => forgot = true);
        Assert.True(folder.SaveFinish(name, new CutMusic { FileName = name }, url, canWrite: true));

        Assert.False(forgot);
        Assert.False(CutMusicQueue.ShouldForgetPreview(composing: true));
        Assert.False(CutMusicQueue.ShouldClearMerge(composing: true));
        Assert.True(queue.IsQueued);
        Assert.Equal(name, Assert.Single(folder.Writes).FileName);
        Assert.True(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: true));
    }

    [Fact]
    public void Folder_write_is_skipped_when_there_is_no_blob()
    {
        Assert.False(CutMusicPersist.ShouldWriteToFolder(canWrite: true, "score.mp3", audioUrl: null));
        Assert.False(CutMusicPersist.NeedsFlushOnSave("score.mp3", fileOnDisk: null, audioUrl: null));
        Assert.False(CutMusicPersist.TryPlanWrite(
            canWrite: true, "score.mp3", audioUrl: null, fileOnDisk: null, force: true, out _));
    }

    private static CutClip NewClip(int scene, int clip, double duration)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        c.SetDuration(duration);
        return c;
    }

    /// <summary>Test double for File System Access writes of the one music track.</summary>
    private sealed class CutMusicFolderProbe
    {
        public List<CutMusicWrite> Writes { get; } = [];
        public string? FileOnDisk { get; private set; }
        public string? SavedJson { get; private set; }

        public bool SaveFinish(string? musicFileName, CutMusic? music, string? audioUrl, bool canWrite)
        {
            if (string.IsNullOrWhiteSpace(musicFileName))
                FileOnDisk = null;
            else if (CutMusicPersist.TryPlanWrite(
                canWrite, musicFileName, audioUrl, FileOnDisk, force: false, out var write))
            {
                Writes.Add(write);
                FileOnDisk = write.FileName;
            }
            else if (CutMusicPersist.NeedsFlushOnSave(musicFileName, FileOnDisk, audioUrl))
                return false;

            var clip = NewClip(1, 1, 8);
            SavedJson = CutProjectFile.Serialize([clip], musicFileName, music: music);
            return true;
        }
    }
}
