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
        if (m.Length > 0 && TryResolveFromMode(m, out var fromMode))
            return fromMode;
        return ResolveFromKind(k);
    }

    private static readonly HashSet<string> ReviewModes = new(StringComparer.Ordinal)
    {
        "plate_rank_classify", "dialogue_verify", "dialogue_verification",
        "clip_auto_review", "clip-auto-review", "movie_auto_review",
        "movie_review_synthesis", "auto_review",
    };

    private static readonly HashSet<string> CharacterModes = new(StringComparer.Ordinal)
    {
        "cast_from_screenplay", "cast_visual_literalize", "species_kind_classify",
        "character_emotion_arc_classify", "wardrobe_continuity_classify",
    };

    private static readonly HashSet<string> ShotPlanModes = new(StringComparer.Ordinal)
    {
        "shot_plan_refine_classify", "beat_pacing_classify", "camera_director_classify",
        "cinematic_lighting_classify", "negative_prompt_classify", "depth_of_field_classify",
        "color_palette_grading_classify", "silent_beat_classify", "onscreen_cast_classify",
        "extend_cut_classify",
    };

    private static readonly HashSet<string> VoiceModes = new(StringComparer.Ordinal)
    {
        "voice_clone", "voice_preview", "tts", "dialogue_tts",
    };

    private static readonly HashSet<string> MusicModes = new(StringComparer.Ordinal)
    {
        "ambient_sfx_classify", "sound_design_composer_classify",
        "music", "bgm", "score", "scene_music",
    };

    private static readonly HashSet<string> VideoModes = new(StringComparer.Ordinal)
    {
        "fresh", "video-extend", "video_extend", "reseed", "extend",
        "done", "failed", "running", "queued",
    };

    private static readonly HashSet<string> ReviewKinds = new(StringComparer.Ordinal)
    {
        "clip-auto-review", "clip-auto-review-batch", "movie-auto-review",
        "video_review", "dialogue_verification", "auto_review", "vision",
    };

    private static readonly HashSet<string> VideoKinds = new(StringComparer.Ordinal)
    {
        "video", "video_extend", "video_poll", "film", "clip", "lip_sync",
    };

    private static readonly HashSet<string> CharacterKinds = new(StringComparer.Ordinal)
    {
        "image", "image_edit", "character", "portrait", "plates",
    };

    private static readonly HashSet<string> MusicKinds = new(StringComparer.Ordinal)
    {
        "music", "bgm", "score",
    };

    private static readonly HashSet<string> VoiceKinds = new(StringComparer.Ordinal)
    {
        "audio", "voice", "tts", "voice-preview", "voice_clone", "speech",
    };

    private static readonly HashSet<string> ScreenplayKinds = new(StringComparer.Ordinal)
    {
        "chat", "planning", "script", "screenplay", "ocr",
        "import", "cast_extract", "shot_plan",
    };

    /// <summary>Purpose tags win over transport kind when we know them. False = fall through to kind.</summary>
    private static bool TryResolveFromMode(string m, out string category)
    {
        // Automated review / QA (post-generation pickers and Gemini video reviews).
        if (m.Contains("review", StringComparison.Ordinal) || ReviewModes.Contains(m))
        {
            category = Review;
            return true;
        }

        if (m.StartsWith("book_to_fountain", StringComparison.Ordinal) ||
            m == "vision_meta_adaptation" ||
            ShotPlanModes.Contains(m))
        {
            category = Screenplay;
            return true;
        }

        if (m == "learning_propose")
        {
            category = Other;
            return true;
        }

        if (CharacterModes.Contains(m))
        {
            category = Characters;
            return true;
        }

        if (MusicModes.Contains(m))
        {
            category = Music;
            return true;
        }

        if (VoiceModes.Contains(m))
        {
            category = Voice;
            return true;
        }

        if (VideoModes.Contains(m))
        {
            category = Video;
            return true;
        }

        category = Other;
        return false;
    }

    private static string ResolveFromKind(string k)
    {
        if (ReviewKinds.Contains(k)) return Review;
        if (VideoKinds.Contains(k)) return Video;
        if (CharacterKinds.Contains(k)) return Characters;
        if (MusicKinds.Contains(k)) return Music;
        if (VoiceKinds.Contains(k)) return Voice;
        if (ScreenplayKinds.Contains(k)) return Screenplay;
        return Other;
    }
}
