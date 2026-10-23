using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Speech bytes returned by <see cref="Abstractions.IVoiceCloneClient.SynthesizeSpeechAsync"/>.
/// Fal returns an https URL. xAI TTS returns the audio bytes inline, carried as a data URI so
/// both providers share one string result.
/// </summary>
public static class VoiceSpeechPayload
{
    public static string ToDataUri(ReadOnlyMemory<byte> audio, string? contentType)
    {
        var mime = Mime(contentType);
        return "data:" + mime + ";base64," + Convert.ToBase64String(audio.Span);
    }

    public static bool TryDecodeDataUri(string? value, out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = "audio/mpeg";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = value.IndexOf(',');
        if (comma <= "data:".Length)
            return false;

        var header = value["data:".Length..comma];
        var semi = header.IndexOf(';');
        var mime = (semi >= 0 ? header[..semi] : header).Trim();
        if (mime.Length > 0)
            contentType = mime;

        try
        {
            bytes = Convert.FromBase64String(value[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length > 0;
    }

    /// <summary>Decode a data URI or GET an https URL. Logs and returns null on transport failure.</summary>
    public static async Task<byte[]?> TryReadAsync(
        IHttpClientFactory httpFactory,
        ILogger logger,
        string url,
        CancellationToken ct)
    {
        if (TryDecodeDataUri(url, out var inline, out _))
            return inline;

        try
        {
            var http = httpFactory.CreateClient();
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Failed to download TTS audio");
            return null;
        }
    }

    private static string Mime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "audio/mpeg";
        var semi = contentType.IndexOf(';');
        var mime = (semi >= 0 ? contentType[..semi] : contentType).Trim();
        return mime.Length == 0 ? "audio/mpeg" : mime;
    }
}
