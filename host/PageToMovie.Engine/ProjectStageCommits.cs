namespace PageToMovie.Engine;

/// <summary>
/// Canonical project-git commit subjects for stage boundaries.
/// Greppable trajectory: <c>ptm:stage=…</c> — keeps learning-package extraction simple.
/// </summary>
public static class ProjectStageCommits
{
    public const string BookPrepared = "ptm:stage=book_prepared";
    public const string ScreenplayCreated = "ptm:stage=screenplay_created";
    public const string CastBuilt = "ptm:stage=cast_built";
    public const string Stage2Blueprint = "ptm:stage=stage2_blueprint";
    public const string FilmJobFinished = "ptm:stage=film_job_finished";
    public const string MusicJobFinished = "ptm:stage=music_job_finished";

    public static string FilmStitched(string filmId) =>
        string.IsNullOrWhiteSpace(filmId)
            ? "ptm:stage=film_stitched"
            : $"ptm:stage=film_stitched film_id={filmId.Trim()}";

    /// <summary>Map job/kind strings (FilmJobService) onto canonical subjects.</summary>
    public static string? FromJobKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "book_prepare" => BookPrepared,
        "book_import" or "stage1" => ScreenplayCreated,
        "cast" or "cast_extract" or "cast-extract" or "characters" or "character" => CastBuilt,
        "stage2" => Stage2Blueprint,
        "film" or "film_job" or "generate" or "video" or "clips" => FilmJobFinished,
        "music" => MusicJobFinished,
        _ => null,
    };
}
