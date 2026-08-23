using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Services;

public sealed class JsResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("folderName")]
    public string? FolderName { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("files")]
    public List<JsFileEntry> Files { get; set; } = [];

    [JsonPropertyName("pictureUrl")]
    public string? PictureUrl { get; set; }

    [JsonPropertyName("stitched")]
    public bool Stitched { get; set; }

    [JsonPropertyName("scenes")]
    public List<JsCachedSeg> Scenes { get; set; } = [];

    [JsonPropertyName("joins")]
    public List<JsCachedSeg> Joins { get; set; } = [];

    [JsonPropertyName("rebuiltScenes")]
    public List<int> RebuiltScenes { get; set; } = [];

    [JsonPropertyName("rebuiltJoins")]
    public List<int> RebuiltJoins { get; set; } = [];
}

public sealed class JsCachedSeg
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class JsComposePlan
{
    [JsonPropertyName("clips")]
    public List<JsExportClip> Clips { get; set; } = [];

    [JsonPropertyName("scenes")]
    public List<JsComposeScene> Scenes { get; set; } = [];

    [JsonPropertyName("joins")]
    public List<JsComposeJoin> Joins { get; set; } = [];

    [JsonPropertyName("reuseMovieUrl")]
    public string? ReuseMovieUrl { get; set; }

    [JsonPropertyName("reusePictureUrl")]
    public string? ReusePictureUrl { get; set; }
}

public sealed class JsComposeScene
{
    [JsonPropertyName("scene")]
    public int Scene { get; set; }

    [JsonPropertyName("first")]
    public int First { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("seconds")]
    public double Seconds { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class JsComposeJoin
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "cut";

    [JsonPropertyName("hold")]
    public double Hold { get; set; }

    [JsonPropertyName("fade")]
    public double Fade { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("encodes")]
    public bool Encodes { get; set; }
}

public sealed class JsFileEntry
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class JsMusicMix
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("markIn")]
    public double MarkIn { get; set; }

    [JsonPropertyName("markOut")]
    public double MarkOut { get; set; }

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1;

    [JsonPropertyName("fadeIn")]
    public double FadeIn { get; set; }

    [JsonPropertyName("fadeOut")]
    public double FadeOut { get; set; }

    [JsonPropertyName("playbackRate")]
    public double PlaybackRate { get; set; } = 1;

    [JsonPropertyName("noiseSuppression")]
    public bool NoiseSuppression { get; set; }

    [JsonPropertyName("prepareFilter")]
    public string? PrepareFilter { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("fallbackFilter")]
    public string? FallbackFilter { get; set; }
}

public sealed class JsExportClip
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("markIn")]
    public double MarkIn { get; set; }

    [JsonPropertyName("markOut")]
    public double MarkOut { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("hold")]
    public bool Hold { get; set; }

    [JsonPropertyName("windows")]
    public List<JsKeepWindow> Windows { get; set; } = [];

    [JsonPropertyName("joinOut")]
    public string JoinOut { get; set; } = "cut";

    [JsonPropertyName("joinHold")]
    public double JoinHold { get; set; }

    [JsonPropertyName("card")]
    public JsCard? Card { get; set; }

    [JsonPropertyName("texts")]
    public List<JsTextOverlay> Texts { get; set; } = [];
}

public sealed class JsTextOverlay
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("seconds")]
    public double Seconds { get; set; } = 2;

    [JsonPropertyName("style")]
    public JsTextStyle? Style { get; set; }
}

public sealed class JsKeepWindow
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }
}

public sealed class JsCard
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("seconds")]
    public double Seconds { get; set; } = 2;

    [JsonPropertyName("style")]
    public JsTextStyle? Style { get; set; }
}

public sealed class JsTextStyle
{
    [JsonPropertyName("fontPx")]
    public int FontPx { get; set; } = CutTextStyle.DefaultFontPx;

    [JsonPropertyName("color")]
    public string Color { get; set; } = CutTextStyle.DefaultColorHex;

    [JsonPropertyName("y")]
    public int Y { get; set; } = CutTextStyle.CenterY;

    [JsonPropertyName("x")]
    public int X { get; set; } = CutTextStyle.CenterX;

    [JsonPropertyName("bar")]
    public bool Bar { get; set; }

    [JsonPropertyName("fadeSec")]
    public double FadeSec { get; set; }

    [JsonPropertyName("font")]
    public string Font { get; set; } = "sans";

    [JsonPropertyName("align")]
    public string Align { get; set; } = "center";

    [JsonPropertyName("cssFont")]
    public string CssFont { get; set; } = CutTextStyle.DefaultCssFont;
}

public sealed class ExportProgressSink : IDisposable
{
    private Action<int, string>? _report;

    public ExportProgressSink(Action<int, string> report) => _report = report;

    [JSInvokable]
    public void Report(int percent, string? message)
    {
        try
        {
            _report?.Invoke(percent, message ?? "");
        }
        catch (ObjectDisposedException)
        {
            // Page or circuit gone — progress is optional.
        }
    }

    public void Dispose() => _report = null;
}

public sealed class MediaTimeSink
{
    private readonly Action<double> _report;
    private readonly Action? _ended;

    public MediaTimeSink(Action<double> report, Action? ended = null)
    {
        _report = report;
        _ended = ended;
    }

    [JSInvokable]
    public void OnTime(double seconds) => _report(seconds);

    [JSInvokable]
    public void OnEnded() => _ended?.Invoke();
}

public sealed class JitPreviewSink : IDisposable
{
    private Action<int, string>? _report;
    private Action<string, int>? _prefix;

    public JitPreviewSink(Action<int, string> report, Action<string, int> prefix)
    {
        _report = report;
        _prefix = prefix;
    }

    [JSInvokable]
    public void Report(int percent, string? message)
    {
        try
        {
            _report?.Invoke(percent, message ?? "");
        }
        catch (ObjectDisposedException)
        {
            // Page or circuit gone — progress is optional.
        }
    }

    [JSInvokable]
    public void OnPrefix(string? url, int clipCount)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            _prefix?.Invoke(url, clipCount);
        }
        catch (ObjectDisposedException)
        {
            // Page or circuit gone — prefix is optional.
        }
    }

    public void Dispose()
    {
        _report = null;
        _prefix = null;
    }
}

public sealed class JsFilmstrip
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("frames")]
    public List<string> Frames { get; set; } = [];
}

public sealed class JsRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}
