using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Play file policy: one merged movie. First Play may start on the
/// hop-sliced take at the playhead; once a merge/prefix covers playback,
/// switch to that file and stay there. Clip/scene edges are times on
/// that file — not take-MP4 hops.
/// </summary>
public static class CutPlayMerge
{
    public const string MovieFileName = "movie.mp4";

    /// <summary>Long-term Play never swaps take MP4s at clip edges.</summary>
    public static bool ShouldHopTakeFiles => false;

    /// <summary>Prefix growth must not replace the playing merge mid-file.</summary>
    public static bool ShouldReplaceMergeSrcWhilePlaying => false;

    /// <summary>The one allowed src change: first-start take → merge.</summary>
    public static bool HoldOutgoingUntilMergeHasFrame => true;

    public static bool CanShowMerge(bool mergeHasFrame) => mergeHasFrame;

    public static bool ShouldPrimeMerge => true;

    public static bool IsMovieFileName(string? fileName) =>
        string.Equals(CutClipNaming.FileNameOnly(fileName), MovieFileName, StringComparison.OrdinalIgnoreCase);

    public static double MergeReadyThroughSec(IReadOnlyList<CutClip> clips, int prefixClipCount)
    {
        if (prefixClipCount <= 0 || clips.Count == 0)
            return 0;
        return CutJitPlay.TimelineEndOf(clips, Math.Min(prefixClipCount, clips.Count) - 1);
    }

    public static bool MergeCovers(IReadOnlyList<CutClip> clips, int prefixClipCount, double playhead) =>
        MergeReadyThroughSec(clips, prefixClipCount) >= playhead - 0.001;

    public static bool MergeCoversTimeline(IReadOnlyList<CutClip> clips, int prefixClipCount)
    {
        if (clips.Count <= 0 || prefixClipCount <= 0)
            return false;
        if (prefixClipCount >= clips.Count)
            return true;
        return MergeReadyThroughSec(clips, prefixClipCount) >= CutJitPlay.TotalSec(clips) - 0.05;
    }

    /// <summary>
    /// A completed movie URL covers every clip. A JIT prefix only covers
    /// the clips that have been joined so far.
    /// </summary>
    public static int CoveredClipCount(
        string? playingUrl,
        string? fullPreviewUrl,
        int prefixClipCount,
        int clipCount)
    {
        if (clipCount <= 0)
            return 0;
        if (!string.IsNullOrWhiteSpace(playingUrl)
            && !string.IsNullOrWhiteSpace(fullPreviewUrl)
            && string.Equals(playingUrl, fullPreviewUrl, StringComparison.Ordinal))
            return clipCount;
        return Math.Clamp(prefixClipCount, 0, clipCount);
    }

    /// <summary>
    /// Prefix / hop EOF is Stop only at the real timeline end. An S01-only
    /// hop file must wait at that edge until the merge is swapped in —
    /// not Stop, not seek back into the last hops of that scene.
    /// Covered-clip count must not decide Stop: a failed hop→merge swap
    /// can make C# think the full movie is showing while the video
    /// element is still the first take.
    /// </summary>
    public static bool ShouldContinueAfterPrefixEnded => false;

    public static bool EndedIsStop(double playhead, double totalSec) =>
        CutJitPlay.IsTimelineEnd(playhead, totalSec);

    public static bool PrefixEndedIsStop(
        double readyEdge,
        double totalSec,
        int coveredClipCount,
        int clipCount) =>
        PrefixEndedIsStop(readyEdge, readyEdge, totalSec, coveredClipCount, clipCount);

    public static bool PrefixEndedIsStop(
        double playhead,
        double readyEdge,
        double totalSec,
        int coveredClipCount,
        int clipCount)
    {
        _ = readyEdge;
        _ = coveredClipCount;
        return clipCount <= 0 || EndedIsStop(playhead, totalSec);
    }

    public static bool TryWaitEdgeAfterPrefixEnded(
        IReadOnlyList<CutClip> clips,
        int coveredClipCount,
        out double waitEdge) =>
        TryWaitEdgeAfterPrefixEnded(clips, coveredClipCount, MergeReadyThroughSec(clips, coveredClipCount), out waitEdge);

    public static bool TryWaitEdgeAfterPrefixEnded(
        IReadOnlyList<CutClip> clips,
        int coveredClipCount,
        double playhead,
        out double waitEdge)
    {
        _ = coveredClipCount;
        waitEdge = playhead;
        if (EndedIsStop(playhead, CutJitPlay.TotalSec(clips)))
            return false;
        return true;
    }

    /// <summary>
    /// C# must not treat a merge URL as on-screen until JS swapped and
    /// decoded a frame. Reusing a "playing" merge we never showed leaves
    /// Play stuck on the hop file at Ready 100%.
    /// </summary>
    public static bool ShouldReusePlayingMovie(
        bool samePlayer,
        string? boundUrl,
        string url,
        bool mergeHasFrame,
        double playhead,
        double playingMergeEnd,
        double totalSec)
    {
        if (!samePlayer || !mergeHasFrame || string.IsNullOrWhiteSpace(url))
            return false;
        if (PlayingFileEndedBeforeTimeline(playhead, playingMergeEnd, totalSec))
            return false;
        if (string.Equals(boundUrl, url, StringComparison.Ordinal))
            return true;
        return !ShouldReplaceMergeSrcWhilePlaying
            && playhead < playingMergeEnd - 0.05;
    }

    /// <summary>
    /// The file on the player ended, but the timeline still has scenes.
    /// Same-URL reuse here leaves Play stuck on Stop at that join.
    /// </summary>
    public static bool PlayingFileEndedBeforeTimeline(
        double playhead, double playingMergeEnd, double totalSec) =>
        !EndedIsStop(playhead, totalSec) && playhead >= playingMergeEnd - 0.05;

    public static double PlayheadAfterMovieEnded(
        double playhead, double playingMergeEnd, double totalSec)
    {
        var edge = Math.Max(playhead, playingMergeEnd);
        if (totalSec <= 0)
            return Math.Max(0, edge);
        return Math.Clamp(edge, 0, totalSec);
    }

    public static bool ShouldRetryMergeSwap(
        bool wantPlay,
        bool showingMerge,
        string? mergeUrl) =>
        ShouldRetryMergeSwap(wantPlay, showingMerge, mergeUrl, 0, 0, 0, 0);

    public static bool ShouldRetryMergeSwap(
        bool wantPlay,
        bool showingMerge,
        string? mergeUrl,
        double playhead,
        double playingMergeEnd,
        double newMergeEnd,
        double totalSec)
    {
        if (!wantPlay || string.IsNullOrWhiteSpace(mergeUrl))
            return false;
        if (!showingMerge)
            return true;
        return PlayingFileEndedBeforeTimeline(playhead, playingMergeEnd, totalSec)
            && newMergeEnd > playingMergeEnd + 0.05;
    }

    /// <summary>
    /// Play/seek time is the playhead. Never the scene's first clip or
    /// scene start — last-scene markers stay where they were placed.
    /// </summary>
    public static double PlaySeekSec(IReadOnlyList<CutClip> clips, double playhead)
    {
        if (clips.Count == 0)
            return 0;
        var total = CutJitPlay.TotalSec(clips);
        if (total <= 0)
            return 0;
        return Math.Clamp(playhead, 0, total);
    }

    /// <summary>
    /// Hop→merge and later-join swaps at a Fade to white / Dissolve / Dip
    /// must start a fade-length before the join. Timeline time has no
    /// overlap; the composed file xfades the last
    /// <see cref="CutComposeContract.XfadeSeconds"/> of the outgoing scene.
    /// Seeking the merge to the join lands after that look.
    /// </summary>
    public static double HandoffSeekSec(
        IReadOnlyList<CutClip> clips, double playhead, bool applyJoinLeadIn)
    {
        var seek = PlaySeekSec(clips, playhead);
        if (!applyJoinLeadIn)
            return seek;
        return PlaySeekSec(clips, seek - JoinLeadInAt(clips, seek));
    }

    /// <summary>
    /// Swap onto a longer merge as the current file reaches a scene join.
    /// Mid-scene prefix growth still does not replace src.
    /// </summary>
    public static bool ShouldHandoffAtJoin(
        double playhead,
        double playingMergeEnd,
        double newMergeEnd,
        double totalSec,
        IReadOnlyList<CutClip> clips)
    {
        if (newMergeEnd <= playingMergeEnd + 0.05)
            return false;
        if (EndedIsStop(playhead, totalSec))
            return false;
        var lead = JoinLeadInAt(clips, playingMergeEnd);
        return playhead >= playingMergeEnd - lead - 0.05;
    }

    public static double JoinLeadInAt(IReadOnlyList<CutClip> clips, double playhead)
    {
        for (var i = 0; i < clips.Count - 1; i++)
        {
            var join = clips[i].JoinToNext(clips[i + 1]);
            if (!CutComposeContract.JoinIsXfade(join))
                continue;
            var joinAt = CutJitPlay.TimelineEndOf(clips, i);
            var leftSec = joinAt - CutJitPlay.TimelineStartOf(clips, i);
            var fade = CutComposeContract.XfadeSecondsFor(leftSec);
            if (playhead >= joinAt - fade - 0.05 && playhead <= joinAt + 0.05)
                return fade;
        }

        return 0;
    }

    public static bool WouldRewindMerge(double currentPlayhead, double targetPlayhead) =>
        targetPlayhead < currentPlayhead - 0.05;

    public static bool ShouldSeekMergeWhilePlaying(bool userSeek) => userSeek;

    /// <summary>Stop pauses in place. Never seek the playhead to 0.</summary>
    public static bool ShouldResetPlayheadOnStop => false;

    public static double PlayheadAfterStop(double playhead) => Math.Max(0, playhead);

    /// <summary>
    /// Changing Dissolve / Fade to white / Dip / Cut-to-black / Cut
    /// rebuilds the merge. The needle stays put — not scene start, not 0.
    /// </summary>
    public static bool ShouldResetPlayheadOnJoinChange => false;

    public static bool ShouldSeekToSceneStartOnJoinChange => false;

    public static double PlayheadAfterJoinChange(IReadOnlyList<CutClip> clips, double playhead) =>
        PlaySeekSec(clips, playhead);

    /// <summary>
    /// A stale JIT prefix must not resume Play or replace the playing
    /// file after a newer rebuild has started.
    /// </summary>
    public static bool ShouldLoopPrefixWhileRebuilding => false;

    public static bool AcceptPrefix(int prefixGen, int playGen) =>
        prefixGen == playGen;

    public static bool ComposeRunOwnsFlag(int runGen, int playGen) =>
        runGen == playGen;

    public static bool ShouldClearProgressWhenComposeEnds => true;

    /// <summary>Mouseup/touchend keeps the drop time — not the prior playhead, scene start, or 0.</summary>
    public static bool ShouldSnapPlayheadOnScrubEnd => false;

    public static double ScrubCommitSec(IReadOnlyList<CutClip> clips, double dropSec) =>
        PlaySeekSec(clips, dropSec);

    public static bool ShouldPlayMerge(
        string? mergeUrl,
        IReadOnlyList<CutClip> clips,
        int prefixClipCount,
        double playhead,
        CutJitPlay.Window? firstStart)
    {
        if (string.IsNullOrWhiteSpace(mergeUrl) || prefixClipCount <= 0)
            return false;
        var mergeEnd = MergeReadyThroughSec(clips, prefixClipCount);
        if (playhead > mergeEnd + 0.05)
            return false;
        if (MergeCoversTimeline(clips, prefixClipCount))
            return true;
        var total = CutJitPlay.TotalSec(clips);
        if (total > mergeEnd + 0.05 && playhead >= mergeEnd - 0.001)
            return false;
        if (firstStart is not { } start)
            return true;
        if (playhead >= start.TimelineEnd - 0.001)
            return mergeEnd >= playhead - 0.001;
        return mergeEnd > start.TimelineEnd + 0.05;
    }

    public static bool ShouldPlayFirstStart(CutJitPlay.Window? firstStart, double playhead, bool playMerge) =>
        !playMerge
        && firstStart is { } window
        && playhead < window.TimelineEnd - 0.001;

    public static bool ShouldSwitchToMergeOnPrefix(bool wantPlay, bool waiting, bool playingFirstStart) =>
        ShouldSwitchToMergeOnPrefix(wantPlay, waiting, playingFirstStart, atPlayingFileEnd: false);

    public static bool ShouldSwitchToMergeOnPrefix(
        bool wantPlay, bool waiting, bool playingFirstStart, bool atPlayingFileEnd) =>
        wantPlay && (waiting || playingFirstStart || atPlayingFileEnd);

    public readonly record struct PreviewLengthCaption(double TrackSec, double FileSec, bool FromMerge);

    /// <summary>
    /// Once Play is on the merge, the caption is that file's coverage —
    /// not the leftover hop take (5.04s) still selected on the timeline.
    /// </summary>
    public static PreviewLengthCaption PreviewCaption(
        bool showingMerge,
        double mergeSec,
        double selectedTrackSec,
        double selectedFileSec)
    {
        if (showingMerge && mergeSec > 0.05)
            return new PreviewLengthCaption(mergeSec, mergeSec, true);
        return new PreviewLengthCaption(selectedTrackSec, selectedFileSec, false);
    }

    public static bool IsFreshMerge(
        string? savedFingerprint,
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        string? audioFileName,
        CutMusic? music = null) =>
        !string.IsNullOrWhiteSpace(savedFingerprint)
        && string.Equals(savedFingerprint, Fingerprint(clips, texts, audioFileName, music), StringComparison.Ordinal);

    public static string Fingerprint(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip>? texts,
        string? audioFileName,
        CutMusic? music = null)
    {
        var sb = new StringBuilder();
        AppendMusicMix(sb, audioFileName, music);
        foreach (var clip in clips)
            AppendClip(sb, clip);
        AppendTitles(sb, texts);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void AppendMusicMix(StringBuilder sb, string? audioFileName, CutMusic? music)
    {
        sb.Append(audioFileName ?? music?.FileName ?? "");
        if (music is not null)
        {
            sb.Append("M").Append(Num(music.StartSec))
                .Append('/').Append(Num(music.MarkIn))
                .Append('-').Append(Num(music.MarkOut));
            if (music.HasMixEdits)
            {
                sb.Append('V').Append(music.VolumePercent)
                    .Append('I').Append(Num(music.FadeInSec))
                    .Append('O').Append(Num(music.FadeOutSec));
            }
        }
    }

    private static void AppendClip(StringBuilder sb, CutClip clip)
    {
        sb.Append('|').Append(clip.Scene).Append(':').Append(clip.Clip);
        sb.Append('@').Append(Num(clip.MarkIn)).Append('-').Append(Num(clip.MarkOut));
        foreach (var span in clip.RangeDeletes)
            sb.Append('~').Append(Num(span.Start)).Append('-').Append(Num(span.End));
        sb.Append('J').Append(clip.JoinOverride is { } join
            ? CutTransitionMap.WireName(join)
            : clip.FountainTransition ?? "");
        if (clip.Card.Enabled)
        {
            sb.Append("C").Append(clip.Card.Text).Append('/').Append(Num(clip.Card.HoldSeconds));
            if (!clip.Card.Style.IsDefault)
                sb.Append('L').Append(CutTextStyle.WireLook(clip.Card.Style));
        }
    }

    private static void AppendTitles(StringBuilder sb, IReadOnlyList<CutTextClip>? texts)
    {
        foreach (var title in texts ?? [])
        {
            sb.Append("#").Append(title.Text)
                .Append('@').Append(Num(title.StartSec))
                .Append('x').Append(Num(title.HoldSeconds));
            if (!title.Style.IsDefault)
                sb.Append('L').Append(CutTextStyle.WireLook(title.Style));
        }
    }

    private static string Num(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
