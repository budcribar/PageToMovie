namespace PageToMovie.Cut.Cut;

/// <summary>
/// Read-only: should Review Play use a saved <c>movie.mp4</c> instead of
/// restitching current takes? Cut JIT/compose is unchanged.
/// </summary>
public static class CutFinishedMovie
{
    /// <summary>
    /// True only when the file is present and <c>cut.project.json</c> still
    /// matches the fingerprint written with that merge. Missing or stale
    /// fingerprint → caller keeps today's WIP stitch.
    /// </summary>
    public static bool ShouldPlay(string? projectJson, bool movieFilePresent)
    {
        if (!movieFilePresent)
            return false;
        if (!CutProjectFile.TryRead(projectJson, out var clips, out var texts, out var fingerprint, out var music))
            return false;
        return CutPlayMerge.IsFreshMerge(fingerprint, clips, texts, music.FileName, music);
    }
}
