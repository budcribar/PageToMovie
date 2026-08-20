namespace PageToMovie.Engine;

/// <summary>
/// Shared MIME + log-body helpers for the ElevenLabs voice and scribe clients.
/// </summary>
internal static class ElevenLabsClientHelpers
{
    public static string GuessAudioMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };

    public static string Trunc(string? s, int max = 240)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
