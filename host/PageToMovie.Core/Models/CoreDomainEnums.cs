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
    PdfPigGrok,
    Text
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
