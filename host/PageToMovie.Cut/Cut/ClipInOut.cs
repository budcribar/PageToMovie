namespace PageToMovie.Cut.Cut;

/// <summary>Clamp mark-in / mark-out to a clip's duration.</summary>
public static class ClipInOut
{
    public const double MinSpanSeconds = 0.1;

    public static (double MarkIn, double MarkOut) Clamp(double markIn, double markOut, double durationSec)
    {
        if (double.IsNaN(durationSec) || double.IsInfinity(durationSec) || durationSec < 0)
            durationSec = 0;
        if (durationSec <= 0)
            return (0, 0);

        var inn = Sanitize(markIn, fallback: 0);
        var outt = Sanitize(markOut, fallback: durationSec);

        if (inn < 0) inn = 0;
        if (inn > durationSec) inn = durationSec;
        if (outt < 0) outt = 0;
        if (outt > durationSec) outt = durationSec;
        if (outt < inn) outt = inn;

        var span = outt - inn;
        if (span < MinSpanSeconds && durationSec >= MinSpanSeconds)
        {
            if (inn + MinSpanSeconds <= durationSec)
                outt = inn + MinSpanSeconds;
            else
            {
                inn = Math.Max(0, durationSec - MinSpanSeconds);
                outt = durationSec;
            }
        }

        return (inn, outt);
    }

    private static double Sanitize(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;
        return value;
    }
}
