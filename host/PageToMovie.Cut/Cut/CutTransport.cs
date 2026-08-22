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

    /// <summary>
    /// Add text stays above title/card tiles (handles are 3, delete is 4).
    /// Live video overlay never uses this stacking context.
    /// </summary>
    public const int TextAddZIndex = 6;
    public const int TextClipZIndex = 1;

    public static string PlayTitle(bool isPlaying) => isPlaying ? "Stop" : "Play";

    public static string PlayGlyph(bool isPlaying) => isPlaying ? "⏹" : "▶";

    public static string PlayButtonClass(bool isPlaying) =>
        isPlaying ? $"{PlayClass} {StopClass}" : PlayClass;
}
