using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A clip's recorded duration_seconds is what was REQUESTED, and is always a whole number; an
/// actual encode essentially never is. When the next clip extends this one the provider returns
/// [this clip + new footage] combined, and the seam sits at this clip's TRUE length — so slicing
/// at the requested length cuts a fraction off the front of the new footage, which is where the
/// first spoken word lives. Every Mary19 extend sidecar reads lead_in=6, start=6, stop=12, and
/// both rebuilt clips lost their opening word (2026-08-25).
/// </summary>
public class MeasuredClipDurationTests
{
    [Fact]
    public void A_measured_length_wins_over_the_requested_one()
    {
        var seam = ClipExtendSource.ProviderInputDurationSeconds(
            leadInSeconds: 0, durationSeconds: 6, clipStopSeconds: null, measuredDurationSeconds: 5.76);
        Assert.Equal(5.76, seam, 3);
    }

    /// <summary>
    /// The recorded stop was itself computed from the requested duration, so it carries the same
    /// rounding. Preferring it would keep the bug while looking like it had been fixed.
    /// </summary>
    [Fact]
    public void A_measured_length_also_wins_over_a_recorded_stop()
    {
        var seam = ClipExtendSource.ProviderInputDurationSeconds(
            leadInSeconds: 0, durationSeconds: 6, clipStopSeconds: 6, measuredDurationSeconds: 5.76);
        Assert.Equal(5.76, seam, 3);
    }

    /// <summary>Chaining a third clip: the seam is everything before it in the combined file.</summary>
    [Fact]
    public void A_combined_predecessor_adds_its_lead_in()
    {
        var seam = ClipExtendSource.ProviderInputDurationSeconds(
            leadInSeconds: 5.76, durationSeconds: 6, clipStopSeconds: 12, measuredDurationSeconds: 5.9);
        Assert.Equal(11.66, seam, 3);
    }

    /// <summary>Old sidecars have no measurement; they must behave exactly as before.</summary>
    [Fact]
    public void Without_a_measurement_the_old_math_is_unchanged()
    {
        Assert.Equal(12, ClipExtendSource.ProviderInputDurationSeconds(6, 6, 12));
        Assert.Equal(6, ClipExtendSource.ProviderInputDurationSeconds(0, 6));
        Assert.Equal(12, ClipExtendSource.ProviderInputDurationSeconds(6, 6));
    }

    /// <summary>A nonsense measurement must not become the seam.</summary>
    [Fact]
    public void An_unusable_measurement_is_ignored()
    {
        Assert.Equal(6, ClipExtendSource.ProviderInputDurationSeconds(0, 6, null, 0));
        Assert.Equal(6, ClipExtendSource.ProviderInputDurationSeconds(0, 6, null, 0.05));
    }

    [Fact]
    public void The_sidecar_prefers_a_measurement_when_it_has_one()
    {
        var withMeasure = new ClipProviderSource(
            null, "file_x", 0, DurationSeconds: 6, MeasuredDurationSeconds: 5.76);
        Assert.Equal(5.76, withMeasure.EffectiveDurationSeconds);

        var without = new ClipProviderSource(null, "file_x", 0, DurationSeconds: 6);
        Assert.Equal(6, without.EffectiveDurationSeconds);
    }

    [Fact]
    public void Stamping_writes_the_measurement_and_keeps_every_other_field()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-measure-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "scene_02_clip_01_take_02.clip.json");
            File.WriteAllText(path, """
                {
                  "scene": 2,
                  "clip": 1,
                  "take": 2,
                  "duration_seconds": 6,
                  "source_file_id": "file_abc",
                  "some_future_field": "keep me"
                }
                """);

            Assert.True(ClipProviderSource.TryStampMeasuredDuration(dir, 2, 1, 5.7612));

            var src = ClipProviderSource.ReadForClip(dir, 2, 1);
            Assert.NotNull(src);
            Assert.Equal(5.761, src!.MeasuredDurationSeconds);
            Assert.Equal(5.761, src.EffectiveDurationSeconds);
            Assert.Equal(6, src.DurationSeconds);
            Assert.Equal("file_abc", src.SourceFileId);
            // A field this type does not model must survive the merge.
            Assert.Contains("some_future_field", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Stamping_is_a_no_op_when_there_is_nothing_to_stamp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-measure-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(ClipProviderSource.TryStampMeasuredDuration(dir, 2, 1, 5.5));

            var path = Path.Combine(dir, "scene_02_clip_01_take_01.clip.json");
            File.WriteAllText(path, "{\"scene\":2}");
            Assert.False(ClipProviderSource.TryStampMeasuredDuration(dir, 2, 1, 0));
            Assert.False(ClipProviderSource.TryStampMeasuredDuration(dir, 2, 1, double.NaN));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
