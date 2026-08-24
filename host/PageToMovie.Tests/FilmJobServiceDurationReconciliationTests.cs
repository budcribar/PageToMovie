using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Phase 8 (action-timing plan): within a continuation chain, a clip's measured duration overrun
/// carries forward as padding for the next clip in the same scene, since that next clip already
/// can't start before this one is on disk — reconciliation costs no extra wall-clock time.
/// </summary>
public class FilmJobServiceDurationReconciliationTests
{
    [Fact]
    public void GeneratedSidecarTiming_preserves_uncapped_fresh_clip_duration_and_window()
    {
        var timing = FilmJobService.GeneratedSidecarTiming(12.0, providerLeadInSeconds: null);

        Assert.Equal(12.0, timing.Duration);
        Assert.Equal(0.0, timing.Start);
        Assert.Equal(12.0, timing.Stop);
        Assert.Equal(12.0, ClipExtendSource.ProviderInputDurationSeconds(
            leadInSeconds: 0, durationSeconds: timing.Duration, clipStopSeconds: timing.Stop));
    }

    [Fact]
    public void ComputeCarryoverOverrunSec_ZeroForNonContinuationModels()
    {
        // Constraint 3: only continuation-chain models get free same-scene reconciliation.
        var overrun = FilmJobService.ComputeCarryoverOverrunSec(
            supportsContinue: false, probedDurationSec: 10.0, requestedDurationSec: 6);
        Assert.Equal(0.0, overrun);
    }

    [Fact]
    public void ComputeCarryoverOverrunSec_ZeroWhenClipRanAtOrUnderRequestedDuration()
    {
        var atBudget = FilmJobService.ComputeCarryoverOverrunSec(
            supportsContinue: true, probedDurationSec: 6.0, requestedDurationSec: 6);
        var underBudget = FilmJobService.ComputeCarryoverOverrunSec(
            supportsContinue: true, probedDurationSec: 4.0, requestedDurationSec: 6);
        Assert.Equal(0.0, atBudget);
        Assert.Equal(0.0, underBudget);
    }

    [Fact]
    public void ComputeCarryoverOverrunSec_ReturnsRawOverrunWithinCap()
    {
        var overrun = FilmJobService.ComputeCarryoverOverrunSec(
            supportsContinue: true, probedDurationSec: 7.5, requestedDurationSec: 6);
        Assert.Equal(1.5, overrun, precision: 5);
    }

    [Fact]
    public void ComputeCarryoverOverrunSec_ClampsToMaxCeilingForAnomalousMeasurement()
    {
        // A single wildly-overrun clip must not balloon every later clip's request unboundedly.
        var overrun = FilmJobService.ComputeCarryoverOverrunSec(
            supportsContinue: true, probedDurationSec: 20.0, requestedDurationSec: 6);
        Assert.Equal(2.0, overrun); // MaxCarryoverDurationPaddingSec
    }

    [Fact]
    public void ResolveIncomingDurationPadding_CarriesForwardForImmediatelyAdjacentClip()
    {
        var padding = FilmJobService.ResolveIncomingDurationPadding(
            clipNum: 3, lastGeneratedClipNum: 2, lastOverrunSec: 1.2);
        Assert.Equal(1.2, padding, precision: 5);
    }

    [Fact]
    public void ResolveIncomingDurationPadding_ZeroWhenThereIsAGap()
    {
        // Clip 2 was skipped (e.g. only-missing regen) — clip 4 has no real adjacency to clip 2.
        var padding = FilmJobService.ResolveIncomingDurationPadding(
            clipNum: 4, lastGeneratedClipNum: 2, lastOverrunSec: 1.2);
        Assert.Equal(0.0, padding);
    }

    [Fact]
    public void ResolveIncomingDurationPadding_ZeroForFirstClip()
    {
        var padding = FilmJobService.ResolveIncomingDurationPadding(
            clipNum: 1, lastGeneratedClipNum: 0, lastOverrunSec: 0.0);
        Assert.Equal(0.0, padding);
    }

    [Fact]
    public void ApplyIncomingDurationPadding_AddsPaddingRoundedUp()
    {
        var padded = FilmJobService.ApplyIncomingDurationPadding(
            durationSeconds: 6, incomingDurationPaddingSec: 1.2, absMaxSeconds: 12);
        Assert.Equal(8, padded); // 6 + ceil(1.2) = 8
    }

    [Fact]
    public void ApplyIncomingDurationPadding_NeverExceedsAbsoluteModelCeiling()
    {
        var padded = FilmJobService.ApplyIncomingDurationPadding(
            durationSeconds: 11, incomingDurationPaddingSec: 2.0, absMaxSeconds: 12);
        Assert.Equal(12, padded);
    }

    [Fact]
    public void ApplyIncomingDurationPadding_IgnoresProbeNoiseBelowHalfASecond()
    {
        // A 0.04 s measured overrun is not a reason to bill a whole extra second.
        Assert.Equal(5, FilmJobService.ApplyIncomingDurationPadding(durationSeconds: 5, incomingDurationPaddingSec: 0.04, absMaxSeconds: 12));
        Assert.Equal(5, FilmJobService.ApplyIncomingDurationPadding(durationSeconds: 5, incomingDurationPaddingSec: 0.49, absMaxSeconds: 12));
        Assert.Equal(6, FilmJobService.ApplyIncomingDurationPadding(durationSeconds: 5, incomingDurationPaddingSec: 0.5, absMaxSeconds: 12));
    }

    [Fact]
    public void ApplyIncomingDurationPadding_NoOpWhenNoPaddingCarried()
    {
        var unchanged = FilmJobService.ApplyIncomingDurationPadding(
            durationSeconds: 6, incomingDurationPaddingSec: 0.0, absMaxSeconds: 12);
        Assert.Equal(6, unchanged);
    }
}
