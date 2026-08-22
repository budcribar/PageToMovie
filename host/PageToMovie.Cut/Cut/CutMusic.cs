namespace PageToMovie.Cut.Cut;

/// <summary>
/// One background music track: place it on the timeline and trim
/// head/tail. Mix still sits under native clip VO.
/// </summary>
public sealed class CutMusic
{
    public const double MinSpanSeconds = 0.3;

    public string? FileName { get; set; }
    public string? DisplayName { get; set; }
    public double StartSec { get; set; }
    public double MarkIn { get; set; }
    public double MarkOut { get; set; }
    public double DurationSec { get; private set; }
    public int VolumePercent { get; set; } = CutMusicMix.DefaultVolumePercent;
    public double FadeInSec { get; set; } = CutMusicMix.DefaultFadeSec;
    public double FadeOutSec { get; set; } = CutMusicMix.DefaultFadeSec;

    public bool HasMixEdits =>
        VolumePercent != CutMusicMix.DefaultVolumePercent
        || FadeInSec > 0.001
        || FadeOutSec > 0.001;

    public bool HasFile => !string.IsNullOrWhiteSpace(FileName);
    public string Label => CutMusicEdit.Label(this);
    public bool HasDuration => DurationSec > 0.05;
    public double SlicedDurationSec
    {
        get
        {
            var (inn, outt) = ResolvedInOut();
            return Math.Max(0, outt - inn);
        }
    }

    public void Clear()
    {
        FileName = null;
        DisplayName = null;
        StartSec = 0;
        MarkIn = 0;
        MarkOut = 0;
        DurationSec = 0;
        VolumePercent = CutMusicMix.DefaultVolumePercent;
        FadeInSec = CutMusicMix.DefaultFadeSec;
        FadeOutSec = CutMusicMix.DefaultFadeSec;
    }

    public void SetFile(string? fileName)
    {
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : CutClipNaming.FileNameOnly(fileName);
        DisplayName = null;
        StartSec = 0;
        MarkIn = 0;
        MarkOut = 0;
        DurationSec = 0;
        VolumePercent = CutMusicMix.DefaultVolumePercent;
        FadeInSec = CutMusicMix.DefaultFadeSec;
        FadeOutSec = CutMusicMix.DefaultFadeSec;
    }

    public void SetVolumePercent(int percent) =>
        VolumePercent = CutMusicMix.ClampVolume(percent);

    public void SetFadeIn(double seconds) =>
        FadeInSec = CutMusicMix.ClampFade(seconds);

    public void SetFadeOut(double seconds) =>
        FadeOutSec = CutMusicMix.ClampFade(seconds);

    public void SetDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            seconds = 0;
        DurationSec = seconds;
        if (seconds <= 0)
            return;
        if (MarkOut <= MarkIn)
            MarkOut = seconds;
        ApplyInOut(MarkIn, MarkOut);
    }

    public void SetStart(double startSec) =>
        StartSec = SanitizeStart(startSec);

    public void ApplyInOut(double markIn, double markOut)
    {
        if (!HasDuration)
        {
            MarkIn = SanitizeStart(markIn);
            MarkOut = Math.Max(MarkIn, SanitizeStart(markOut));
            return;
        }

        var (inn, outt) = ClipInOut.Clamp(markIn, markOut, DurationSec);
        if (outt - inn < MinSpanSeconds)
        {
            if (inn + MinSpanSeconds <= DurationSec)
                outt = inn + MinSpanSeconds;
            else
            {
                inn = Math.Max(0, DurationSec - MinSpanSeconds);
                outt = DurationSec;
            }
        }

        MarkIn = inn;
        MarkOut = outt;
    }

    public void TrimIn(double markIn) => ApplyInOut(markIn, MarkOut);

    public void TrimOut(double markOut) => ApplyInOut(MarkIn, markOut);

    public void Move(double startSec) => SetStart(startSec);

    public (double MarkIn, double MarkOut) ResolvedInOut()
    {
        if (!HasDuration)
            return (Math.Max(0, MarkIn), Math.Max(MarkIn, MarkOut));
        return ClipInOut.Clamp(MarkIn, MarkOut > MarkIn ? MarkOut : DurationSec, DurationSec);
    }

    public static double SanitizeStart(double startSec)
    {
        if (double.IsNaN(startSec) || double.IsInfinity(startSec) || startSec < 0)
            return 0;
        return startSec;
    }
}
