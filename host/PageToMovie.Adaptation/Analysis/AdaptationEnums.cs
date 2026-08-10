using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookKind
{
    PictureBook,
    Short,
    Novel
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextDensity
{
    Normal,
    Sparse
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextQuality
{
    Good,
    Poor,
    Empty
}

public static class AdaptationEnumExtensions
{
    public static string ToApiString(this BookKind kind) => kind switch
    {
        BookKind.PictureBook => "picture_book",
        BookKind.Short => "short",
        BookKind.Novel => "novel",
        _ => "short"
    };

    public static string ToApiString(this TextDensity density) => density switch
    {
        TextDensity.Normal => "normal",
        TextDensity.Sparse => "sparse",
        _ => "normal"
    };

    public static string ToApiString(this TextQuality quality) => quality switch
    {
        TextQuality.Good => "good",
        TextQuality.Poor => "poor",
        TextQuality.Empty => "empty",
        _ => "empty"
    };

    public static BookKind ParseBookKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "picture_book" or "picturebook" or "picture" => BookKind.PictureBook,
            "novel" => BookKind.Novel,
            _ => BookKind.Short,
        };

    public static TextDensity ParseTextDensity(string? value) =>
        string.Equals(value?.Trim(), "sparse", StringComparison.OrdinalIgnoreCase)
            ? TextDensity.Sparse
            : TextDensity.Normal;

    public static TextQuality ParseTextQuality(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "good" => TextQuality.Good,
            "poor" => TextQuality.Poor,
            "empty" => TextQuality.Empty,
            _ => TextQuality.Empty,
        };
}
