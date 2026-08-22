using System.Text.Json.Serialization;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Live title/card cues on the preview player. Native Play shows these
/// over the take; a composed movie already has them burned in.
/// </summary>
public static class CutPlayOverlay
{
    public static bool UseLiveOverlay(bool showingComposedMovie) => !showingComposedMovie;

    public static IReadOnlyList<CutPlayOverlayCue> Cues(
        IReadOnlyList<CutClip> clips,
        IReadOnlyList<CutTextClip> titles)
    {
        var blocks = CutTextTrack.Build(clips, titles, CutTimelineLayout.DefaultPxPerSec);
        var cues = new List<CutPlayOverlayCue>(blocks.Count);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text))
                continue;
            var look = CutTextTrack.StyleOf(block);
            cues.Add(new CutPlayOverlayCue
            {
                StartSec = block.StartSec,
                EndSec = block.StartSec + block.Seconds,
                Text = block.Text,
                FontPx = look.FontPx,
                ColorHex = look.ColorHex,
                Y = look.Y,
                Bar = look.HasBar,
                FadeSec = look.FadeSec(block.Seconds),
            });
        }

        return cues;
    }

    public static CutPlayOverlayCue? ActiveAt(IReadOnlyList<CutPlayOverlayCue> cues, double timelineSec)
    {
        if (cues.Count == 0)
            return null;
        for (var i = cues.Count - 1; i >= 0; i--)
        {
            var cue = cues[i];
            if (timelineSec >= cue.StartSec && timelineSec < cue.EndSec)
                return cue;
        }

        return null;
    }

    public static double Opacity(CutPlayOverlayCue cue, double timelineSec)
    {
        if (timelineSec < cue.StartSec || timelineSec >= cue.EndSec)
            return 0;
        var fade = cue.FadeSec;
        if (fade <= 0.05)
            return 1;
        var hold = Math.Max(0, cue.EndSec - cue.StartSec);
        var edge = Math.Min(fade, hold / 3);
        if (edge <= 0)
            return 1;
        if (timelineSec < cue.StartSec + edge)
            return Math.Clamp((timelineSec - cue.StartSec) / edge, 0, 1);
        if (timelineSec > cue.EndSec - edge)
            return Math.Clamp((cue.EndSec - timelineSec) / edge, 0, 1);
        return 1;
    }
}

public sealed class CutPlayOverlayCue
{
    [JsonPropertyName("startSec")]
    public double StartSec { get; init; }

    [JsonPropertyName("endSec")]
    public double EndSec { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("fontPx")]
    public int FontPx { get; init; } = CutTextStyle.DefaultFontPx;

    [JsonPropertyName("colorHex")]
    public string ColorHex { get; init; } = CutTextStyle.DefaultColorHex;

    [JsonPropertyName("y")]
    public int Y { get; init; } = CutTextStyle.CenterY;

    [JsonPropertyName("bar")]
    public bool Bar { get; init; }

    [JsonPropertyName("fadeSec")]
    public double FadeSec { get; init; }
}
