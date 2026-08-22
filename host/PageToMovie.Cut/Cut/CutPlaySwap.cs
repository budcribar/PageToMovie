namespace PageToMovie.Cut.Cut;

/// <summary>
/// Picture continuity at Play hops. Setting <c>video.src</c> (or seeking)
/// on the visible element blanks a frame before the next decode. Prime a
/// standby, hold the outgoing last frame, swap only when the incoming
/// frame is ready.
/// </summary>
public static class CutPlaySwap
{
    public const double ReadyEpsilonSec = 0.05;

    /// <summary>Never assign src on the visible player at a hop.</summary>
    public static bool ShouldSetSrcOnVisible => false;

    /// <summary>Same-file scissors hops still seek on the standby.</summary>
    public static bool UseStandbyForHop(bool sameUrl)
    {
        _ = sameUrl;
        return true;
    }

    public static bool HoldOutgoingUntilIncomingHasFrame => true;

    public static bool CanShowIncoming(bool incomingHasFrame) => incomingHasFrame;

    public static bool ShouldPrimeNextHop => true;

    public static bool PrimedMatches(string? primedUrl, double primedAt, string? url, double at) =>
        !string.IsNullOrWhiteSpace(primedUrl)
        && primedUrl == url
        && Math.Abs(primedAt - at) <= ReadyEpsilonSec;

    public static CutJitPlay.Window? NextHardHop(IReadOnlyList<CutClip> clips, CutJitPlay.Window current)
    {
        if (!CutJitPlay.IsHardPlayJoin(clips, current.Index))
            return null;
        var next = CutJitPlay.At(clips, current.TimelineEnd);
        if (next is null || next.Value.Index <= current.Index)
            return null;
        return next;
    }

    public static bool ShouldPrimeMovie(IReadOnlyList<CutClip> clips, CutJitPlay.Window current) =>
        current.Index < clips.Count - 1 && !CutJitPlay.IsHardPlayJoin(clips, current.Index);
}
