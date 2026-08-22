using System.Text.Json.Serialization;
using Microsoft.JSInterop;

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

    [JsonPropertyName("windows")]
    public List<JsKeepWindow> Windows { get; set; } = [];

    [JsonPropertyName("joinOut")]
    public string JoinOut { get; set; } = "cut";

    [JsonPropertyName("card")]
    public JsCard? Card { get; set; }
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
}

public sealed class ExportProgressSink
{
    private readonly Action<int, string> _report;

    public ExportProgressSink(Action<int, string> report) => _report = report;

    [JSInvokable]
    public void Report(int percent, string? message) => _report(percent, message ?? "");
}

public sealed class MediaTimeSink
{
    private readonly Action<double> _report;

    public MediaTimeSink(Action<double> report) => _report = report;

    [JSInvokable]
    public void OnTime(double seconds) => _report(seconds);
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
