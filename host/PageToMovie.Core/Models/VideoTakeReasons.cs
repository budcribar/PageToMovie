namespace PageToMovie.Core.Models;

/// <summary>H3 — optional one-click reason after user regen (not a form wall).</summary>
public static class VideoTakeReasons
{
    public const string Dialogue = "dialogue";
    public const string Look = "look";
    public const string Motion = "motion";
    public const string Audio = "audio";
    public const string Other = "other";

    public static readonly string[] All =
    {
        Dialogue, Look, Motion, Audio, Other,
    };

    public static string? NormalizeOptional(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var r = reason.Trim().ToLowerInvariant();
        return r switch
        {
            Dialogue or "dialog" or "line" or "speech" => Dialogue,
            Look or "face" or "appearance" or "visual" or "cast" => Look,
            Motion or "action" or "camera" or "move" => Motion,
            Audio or "sound" or "voice" or "music" => Audio,
            Other or "misc" => Other,
            _ => null,
        };
    }

    public static string Display(string? reason) => reason switch
    {
        Dialogue => "Dialogue",
        Look => "Look",
        Motion => "Motion",
        Audio => "Audio",
        Other => "Other",
        _ => reason ?? "",
    };
}
