namespace PageToMovie.Cut.Cut;

public sealed class CutTake
{
    /// <summary>
    /// Film leftover stubs on Mary19 were ~45 KB. A real take (including a
    /// credits card) is larger. Cut must not treat those bytes as a picture.
    /// </summary>
    public const long MinPlayableTakeBytes = 50_000;

    public static bool IsPlayableFile(long sizeBytes) => sizeBytes >= MinPlayableTakeBytes;

    public required int Take { get; init; }
    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public long SizeBytes { get; init; }
    public string? PreviewUrl { get; set; }
    public bool Missing { get; set; }
    public string? MissingReason { get; set; }
    public double DurationSec { get; private set; }
    public double MarkIn { get; private set; }
    public double MarkOut { get; private set; }
    public double ProviderLeadInSeconds { get; private set; }
    public double? ProviderClipStartSeconds { get; private set; }
    public double? ProviderClipStopSeconds { get; private set; }
    public List<string> Filmstrip { get; } = [];

    internal CutHop Hop { get; private set; } = CutHop.None;

    public bool HasDuration => DurationSec > 0;

    public string Label => $"Take {Take}";

    /// <summary>File-time floor for trim handles (hop start / lead-in, else 0).</summary>
    public double TrimMinSec => CutHop.SliceStart(Hop);

    /// <summary>File-time ceiling for trim handles (hop stop, else file duration).</summary>
    public double TrimMaxSec =>
        HasDuration ? CutHop.SliceEnd(Hop, DurationSec) : CutHop.SliceEnd(Hop, 0);

    public void SetHop(CutHop hop)
    {
        Hop = hop;
        ProviderLeadInSeconds = hop.LeadInSeconds;
        ProviderClipStartSeconds = hop.ClipStartSeconds;
        ProviderClipStopSeconds = hop.ClipStopSeconds;
        if (CutHop.TrySlice(hop, DurationSec, out var inn, out var outt))
            ApplyInOut(inn, outt);
    }

    public void SetDuration(double seconds)
    {
        DurationSec = seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? seconds
            : 0;
        if (DurationSec <= 0)
        {
            if (!Hop.HasSlice)
            {
                MarkIn = 0;
                MarkOut = 0;
            }

            return;
        }

        if (MarkOut <= 0)
        {
            var (inn, outt) = CutHop.SeedInOut(Hop, DurationSec);
            ApplyInOut(inn, outt);
        }
        else
            ApplyInOut(MarkIn, MarkOut);
    }

    public void ApplyInOut(double markIn, double markOut)
    {
        if (!HasDuration)
        {
            MarkIn = markIn;
            MarkOut = markOut;
            return;
        }

        var (a, b) = ClipInOut.Clamp(markIn, markOut, DurationSec);
        MarkIn = a;
        MarkOut = b;
    }

    /// <summary>
    /// Same take file and hop — independent in/out for a scissors split.
    /// </summary>
    public CutTake CloneIdentity()
    {
        var copy = new CutTake
        {
            Take = Take,
            FileName = FileName,
            RelativePath = RelativePath,
            SizeBytes = SizeBytes,
            PreviewUrl = PreviewUrl,
            Missing = Missing,
            MissingReason = MissingReason,
        };
        copy.Hop = Hop;
        copy.ProviderLeadInSeconds = ProviderLeadInSeconds;
        copy.ProviderClipStartSeconds = ProviderClipStartSeconds;
        copy.ProviderClipStopSeconds = ProviderClipStopSeconds;
        if (HasDuration)
            copy.SetDuration(DurationSec);
        copy.ApplyInOut(MarkIn, MarkOut);
        copy.Filmstrip.AddRange(Filmstrip);
        return copy;
    }
}
