using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeMode
{
    Natural,
    Reduced,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextEngineKind
{
    PdfPig,
    /// <summary>Legacy persisted name for catalog Vision OCR. Prefer <see cref="Vision"/>.</summary>
    PdfPigGrok,
    Text,
    /// <summary>Catalog Vision capability (page OCR / transcribe).</summary>
    Vision
}

public static class TextEngineKindExtensions
{
    /// <summary>
    /// Parse extract_meta <c>text_engine</c>. Capability label <c>vision</c> plus
    /// legacy <c>grok_vision</c> / <c>PdfPigGrok</c> map to <see cref="TextEngineKind.Vision"/>.
    /// </summary>
    public static TextEngineKind? TryParse(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Equals("vision", StringComparison.OrdinalIgnoreCase)
            || v.Equals("grok_vision", StringComparison.OrdinalIgnoreCase)
            || v.Equals("grok", StringComparison.OrdinalIgnoreCase))
            return TextEngineKind.Vision;
        if (!Enum.TryParse<TextEngineKind>(v, ignoreCase: true, out var parsed))
            return null;
        return parsed == TextEngineKind.PdfPigGrok ? TextEngineKind.Vision : parsed;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechSubstitutionMode
{
    Narrator,
    Dialogue,
    All,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceCloneStatus
{
    Pending,
    Processing,
    Ready,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceGender
{
    Male,
    Female,
    Neutral
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceAgeBand
{
    Child,
    YoungAdult,
    Adult,
    Elderly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiAuthLocation
{
    Bearer,
    Header,
    Query
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetryBackoffKind
{
    Linear,
    Exponential,
    Quadratic
}
