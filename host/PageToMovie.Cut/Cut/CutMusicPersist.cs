namespace PageToMovie.Cut.Cut;

/// <summary>
/// One-track music bytes belong in the picked folder under the file name.
/// JSON alone cannot reopen the song.
/// </summary>
public static class CutMusicPersist
{
    public static string FileNameOf(string? pathOrName) =>
        CutClipNaming.FileNameOnly(pathOrName);

    public static bool IsAudioFileName(string? pathOrName)
    {
        var name = FileNameOf(pathOrName);
        return name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aac", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldWriteToFolder(bool canWrite, string? fileName, string? audioUrl) =>
        canWrite
        && !string.IsNullOrWhiteSpace(FileNameOf(fileName))
        && !string.IsNullOrWhiteSpace(audioUrl);

    public static bool NeedsFlushOnSave(string? fileName, string? fileOnDisk, string? audioUrl)
    {
        var name = FileNameOf(fileName);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(audioUrl))
            return false;
        return !string.Equals(fileOnDisk, name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryPlanWrite(
        bool canWrite,
        string? fileName,
        string? audioUrl,
        string? fileOnDisk,
        bool force,
        out CutMusicWrite write)
    {
        write = default;
        if (!ShouldWriteToFolder(canWrite, fileName, audioUrl))
            return false;
        var name = FileNameOf(fileName);
        if (!force && !NeedsFlushOnSave(name, fileOnDisk, audioUrl))
            return false;
        write = new CutMusicWrite(name, audioUrl!);
        return true;
    }
}

public readonly record struct CutMusicWrite(string FileName, string Url);
