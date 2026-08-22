namespace PageToMovie.Cut.Cut;

/// <summary>
/// Play-clock policy: timeupdate and same-scene hops stay off the Blazor
/// render path. JS paints the white playhead and live text overlay.
/// </summary>
public static class CutPlayClock
{
    public const double AdvanceEpsilonSec = 0.04;

    public static bool ShouldRenderOnTimeUpdate => false;

    public static bool ShouldRenderOnNativeAdvance => false;

    public static bool ShouldRebindPlayback(bool samePlayer) => !samePlayer;

    public static bool ShouldResumeOnPrefix(bool wantPlay, bool waiting) =>
        wantPlay && waiting;

    public static bool ShouldRestartNativeOnPrefixGrow => false;

    public static bool ShouldRenderOnPrefix(bool waiting, bool playing) =>
        waiting || !playing;

    public static bool ShouldRenderOnProgress(bool overlayVisible) => overlayVisible;

    public static bool BlazorOwnsVideoSrc(bool isPlaying) => !isPlaying;

    public static bool ShouldAdvanceNative(double localSec, double localEnd) =>
        localSec >= localEnd - AdvanceEpsilonSec;

    public static double PlayheadLeftPx(double timelineSec, double pxPerSec) =>
        Math.Max(0, timelineSec) * Math.Max(0, pxPerSec);

    public static string Clock(double seconds) => CutTimelineLayout.Clock(seconds);
}
