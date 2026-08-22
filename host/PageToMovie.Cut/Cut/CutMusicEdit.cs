namespace PageToMovie.Cut.Cut;

/// <summary>
/// One-track music menu ops. Delete / rename / place / hold stay on
/// the existing start + in/out model — never a second song file.
/// </summary>
public static class CutMusicEdit
{
    public static string Label(CutMusic? music, string? fileFallback = null)
    {
        if (music is not null)
        {
            var custom = (music.DisplayName ?? "").Trim();
            if (custom.Length > 0)
                return custom;
            var file = (music.FileName ?? "").Trim();
            if (file.Length > 0)
                return file;
        }

        var fallback = CutClipNaming.FileNameOnly(fileFallback ?? "");
        return fallback.Length > 0 ? fallback : "Music";
    }

    public static void Rename(CutMusic music, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(music);
        var value = (displayName ?? "").Trim();
        music.DisplayName = value.Length > 0 && !SameFile(value, music.FileName)
            ? value
            : null;
    }

    public static void Delete(CutMusic music)
    {
        ArgumentNullException.ThrowIfNull(music);
        music.Clear();
    }

    public static CutMusicPlacement Copy(CutMusic music)
    {
        ArgumentNullException.ThrowIfNull(music);
        var (inn, outt) = music.ResolvedInOut();
        return new CutMusicPlacement(music.StartSec, inn, outt);
    }

    /// <summary>
    /// Move the same in/out slice to the playhead. Does not add a track.
    /// </summary>
    public static void Paste(CutMusic music, CutMusicPlacement placement, double playheadSec)
    {
        ArgumentNullException.ThrowIfNull(music);
        music.SetStart(playheadSec);
        music.ApplyInOut(placement.MarkIn, placement.MarkOut);
    }

    /// <summary>
    /// Stretch the existing slice from MarkIn. Same in/out model as the handles.
    /// </summary>
    public static void SetHold(CutMusic music, double seconds)
    {
        ArgumentNullException.ThrowIfNull(music);
        var (inn, _) = music.ResolvedInOut();
        var hold = double.IsNaN(seconds) || double.IsInfinity(seconds)
            ? CutMusic.MinSpanSeconds
            : seconds;
        music.TrimOut(inn + hold);
    }

    /// <summary>
    /// One score on one row — scissors would be a second file. Keep trim.
    /// </summary>
    public static bool CanSplit(CutMusic? music, double playheadSec) => false;

    public static bool Contains(CutMusic music, double playheadSec)
    {
        var start = Math.Max(0, music.StartSec);
        var end = start + music.SlicedDurationSec;
        return playheadSec >= start && playheadSec < end;
    }

    private static bool SameFile(string display, string? fileName) =>
        string.Equals(display, (fileName ?? "").Trim(), StringComparison.Ordinal);
}

public readonly record struct CutMusicPlacement(double StartSec, double MarkIn, double MarkOut);
