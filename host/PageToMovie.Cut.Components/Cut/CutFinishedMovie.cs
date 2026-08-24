namespace PageToMovie.Cut.Cut;

/// <summary>
/// Read-only: should Review Play / Share / editor / dub use a saved
/// <c>movie.mp4</c> instead of restitching current takes? Cut JIT/compose
/// is unchanged.
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

    /// <summary>
    /// Share / export / editor / dub source: the resolved Finish movie when
    /// <see cref="ShouldPlay"/> already accepted it, otherwise the caller stitch.
    /// Does not re-check the fingerprint.
    /// </summary>
    public static string? ChooseUrl(string? finishedMovieUrl, string? stitchUrl)
    {
        if (!string.IsNullOrWhiteSpace(finishedMovieUrl))
            return finishedMovieUrl;
        return string.IsNullOrWhiteSpace(stitchUrl) ? null : stitchUrl;
    }
}
