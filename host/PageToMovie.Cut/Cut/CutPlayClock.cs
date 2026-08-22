namespace PageToMovie.Cut.Cut;

/// <summary>
/// Play-clock policy: timeupdate and the first-start→merge handoff stay
/// off the Blazor render path. JS paints the white playhead and live text overlay.
/// </summary>
public static class CutPlayClock
{
    public const double AdvanceEpsilonSec = 0.04;

    public static bool ShouldRenderOnTimeUpdate => false;

    public static bool ShouldRenderOnNativeAdvance => false;

    public static bool ShouldRebindPlayback(bool samePlayer) => !samePlayer;

    public static bool ShouldResumeOnPrefix(bool wantPlay, bool waiting) =>
        wantPlay && waiting;

    /// <summary>
    /// Prefix EOF is wait-at-edge, not ContinuePlay. Seeking the same
    /// file back to a stale playhead loops the last hops of the scene.
    /// </summary>
    public static bool ShouldContinuePlayOnPrefixEnded => CutPlayMerge.ShouldContinueAfterPrefixEnded;

    public static bool ShouldResetPlayheadOnStop => CutPlayMerge.ShouldResetPlayheadOnStop;

    public static bool ShouldSnapPlayheadOnScrubEnd => CutPlayMerge.ShouldSnapPlayheadOnScrubEnd;

    /// <summary>
    /// A paused player must not paint the needle. After Stop or a scrub
    /// release, timeupdate/seeked on a remounted video is often t=0.
    /// </summary>
    public static bool ShouldPaintPlayheadWhilePaused => false;

    public static bool ShouldSwitchToMergeOnPrefix(bool wantPlay, bool waiting, bool playingFirstStart) =>
        CutPlayMerge.ShouldSwitchToMergeOnPrefix(wantPlay, waiting, playingFirstStart);

    public static bool ShouldRestartNativeOnPrefixGrow => false;

    public static bool ShouldReplaceMergeSrcWhilePlaying => CutPlayMerge.ShouldReplaceMergeSrcWhilePlaying;

    public static bool ShouldRenderOnPrefix(bool waiting, bool playing) =>
        waiting || !playing;

    public static bool ShouldRenderOnProgress(bool overlayVisible) => overlayVisible;

    public static bool BlazorOwnsVideoSrc(bool isPlaying) => !isPlaying;

    /// <summary>
    /// Freeze preview markup while playing so a wait-overlay render
    /// cannot reset <c>video.src</c> and blank the picture.
    /// </summary>
    public static bool FreezePreviewMarkup(bool isPlaying) => isPlaying;

    public static bool ShouldAdvanceNative(double localSec, double localEnd) =>
        localSec >= localEnd - AdvanceEpsilonSec;

    public static double PlayheadLeftPx(double timelineSec, double pxPerSec) =>
        Math.Max(0, timelineSec) * Math.Max(0, pxPerSec);

    public static string Clock(double seconds) => CutTimelineLayout.Clock(seconds);
}
