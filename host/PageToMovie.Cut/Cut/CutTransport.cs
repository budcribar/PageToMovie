namespace PageToMovie.Cut.Cut;

/// <summary>
/// Transport chrome SSoT: white playhead, Play ↔ Stop while preview/JIT runs.
/// </summary>
public static class CutTransport
{
    public const string PlayheadClass = "cut-tl-playhead";
    public const string PlayheadColor = "#ffffff";
    public const string PlayClass = "cut-tl-play";
    public const string StopClass = "is-stop";
    public const string ScissorsClass = "cut-tl-scissors";
    public const string TextAddClass = "cut-tl-text-add";
    public const string TextClipClass = "cut-tl-text-clip";
    public const string TextMenuClass = CutTextMenu.PanelClass;

    /// <summary>
    /// Add text stays above title/card tiles (handles are 3).
    /// Delete lives on the inspector, not on the trim handle.
    /// Live video overlay never uses this stacking context.
    /// </summary>
    public const int TextAddZIndex = 6;
    public const int TextClipZIndex = 1;

    /// <summary>
    /// Play is enabled once any current-take clip is loaded. Do not wait
    /// for movie.mp4 or a finished merge.
    /// </summary>
    public static bool IsPlayable(CutClip clip) =>
        !clip.Missing && !string.IsNullOrWhiteSpace(clip.PreviewUrl);

    public static bool CanPlay(IEnumerable<CutClip> clips) =>
        clips.Any(IsPlayable);

    public static List<CutClip> PlayableClips(IEnumerable<CutClip> clips) =>
        clips.Where(IsPlayable).ToList();

    public static string PlayTitle(bool isPlaying) => isPlaying ? "Stop" : "Play";

    public static string PlayGlyph(bool isPlaying) => isPlaying ? "⏹" : "▶";

    public static string PlayButtonClass(bool isPlaying) =>
        isPlaying ? $"{PlayClass} {StopClass}" : PlayClass;
}
