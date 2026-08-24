using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Caps video resolution when a generate request is reference-to-video
/// (image refs and/or preset voices). Catalog <c>maxResolutionWithReferences</c>
/// is the only cap source — no hardcoded model ids.
/// </summary>
public static class VideoReferenceResolution
{
    public static string Cap(
        string? requested,
        SupportedModelEntry? model,
        bool hasImageRefs,
        bool hasVoiceRefs)
    {
        var resolution = string.IsNullOrWhiteSpace(requested) ? "720p" : requested.Trim();
        if (!hasImageRefs && !hasVoiceRefs)
            return resolution;
        var cap = model?.MaxResolutionWithReferences;
        if (string.IsNullOrWhiteSpace(cap))
            return resolution;
        return Rank(resolution) <= Rank(cap) ? Normalize(resolution) : Normalize(cap);
    }

    public static bool IsReferenceToVideo(bool hasImageRefs, bool hasVoiceRefs) =>
        hasImageRefs || hasVoiceRefs;

    private static string Normalize(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "480" or "480p" => "480p",
            "720" or "720p" => "720p",
            "1080" or "1080p" => "1080p",
            _ => v,
        };
    }

    private static int Rank(string value) => Normalize(value) switch
    {
        "480p" => 480,
        "720p" => 720,
        "1080p" => 1080,
        _ => 0,
    };
}
