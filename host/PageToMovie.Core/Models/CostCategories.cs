namespace PageToMovie.Core.Models;

/// <summary>
/// User-visible cost buckets (Estimate & cost pie / Home spend).
/// API telemetry and ledger rows should map 1:1 into these ids — not raw provider "kind".
/// </summary>
public static class CostCategories
{
    public const string Screenplay = "screenplay";
    public const string Characters = "characters";
    public const string Video = "video";
    public const string Voice = "voice";
    public const string Music = "music";
    /// <summary>
    /// Auto QA / pickers after generation: best portrait, Gemini clip/movie review, dialogue checks.
    /// </summary>
    public const string Review = "review";
    public const string Other = "other";

    /// <summary>Stable display order for pies and category cards.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Screenplay, "Screenplay & planning"),
        (Characters, "Character generation"),
        (Video, "Video generation"),
        (Voice, "Voice & dialogue audio"),
        (Music, "Music generation"),
        (Review, "Automated review"),
        (Other, "Other"),
    ];

    public static string Label(string? id) =>
        All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)).Label
        ?? "Other";

    public static bool IsKnown(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        All.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Map API transport <paramref name="kind"/> + purpose <paramref name="mode"/> → user category.
    /// Prefer an explicit category when callers already set one.
    /// </summary>
    public static string Resolve(string? kind, string? mode, string? explicitCategory = null)
    {
        if (IsKnown(explicitCategory) && explicitCategory is { } known)
            return known.Trim().ToLowerInvariant();

        var m = (mode ?? "").Trim().ToLowerInvariant();
        var k = (kind ?? "").Trim().ToLowerInvariant();

        // Purpose tags win over transport kind when we know them.
        if (m.Length > 0)
        {
            // Automated review / QA (post-generation pickers and Gemini video reviews).
            if (m.Contains("review", StringComparison.Ordinal) ||
                m is "plate_rank_classify" or "dialogue_verify" or "dialogue_verification"
                or "clip_auto_review" or "clip-auto-review" or "movie_auto_review"
                or "movie_review_synthesis" or "auto_review")
                return Review;

            if (m.StartsWith("book_to_fountain", StringComparison.Ordinal) ||
                m is "vision_meta_adaptation")
                return Screenplay;

            if (m is "learning_propose")
                return Other;

            if (m is "cast_from_screenplay" or "cast_visual_literalize" or "species_kind_classify"
                or "character_emotion_arc_classify" or "wardrobe_continuity_classify")
                return Characters;

            // Shot-plan / style classifiers while planning the film (not post-hoc QA).
            if (m is "shot_plan_refine_classify" or "beat_pacing_classify" or "camera_director_classify"
                or "cinematic_lighting_classify" or "negative_prompt_classify" or "depth_of_field_classify"
                or "color_palette_grading_classify" or "silent_beat_classify" or "onscreen_cast_classify"
                or "extend_cut_classify")
                return Screenplay;

            if (m is "ambient_sfx_classify" or "sound_design_composer_classify")
                return Music;

            if (m is "voice_clone" or "voice_preview" or "tts" or "dialogue_tts")
                return Voice;

            if (m is "music" or "bgm" or "score" or "scene_music")
                return Music;

            // Video generation modes (not review).
            if (m is "fresh" or "video-extend" or "video_extend" or "reseed" or "extend"
                or "done" or "failed" or "running" or "queued")
                return Video;
        }

        return k switch
        {
            // Post-gen QA jobs often use these kinds (see FilmJobService).
            "clip-auto-review" or "clip-auto-review-batch" or "movie-auto-review"
                or "video_review" or "dialogue_verification" or "auto_review" => Review,
            "video" or "video_extend" or "video_poll" or "film" or "clip" or "lip_sync" => Video,
            "image" or "image_edit" or "character" or "portrait" or "plates" => Characters,
            "music" or "bgm" or "score" => Music,
            "audio" or "voice" or "tts" or "voice-preview" or "voice_clone" or "speech" => Voice,
            "chat" or "planning" or "script" or "screenplay" or "ocr"
                or "import" or "cast_extract" or "shot_plan" => Screenplay,
            // Generic vision: often review frames/clips; import OCR uses chat/ocr kinds above.
            "vision" => Review,
            _ => Other,
        };
    }
}
