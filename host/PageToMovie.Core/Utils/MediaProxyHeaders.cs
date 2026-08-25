namespace PageToMovie.Core.Utils;

/// <summary>
/// Headers and LastStatus copy for media-proxy recovery. Same-origin fetch reads
/// <see cref="FileIdError"/> when Files content GET failed but <c>source_url</c> still streamed.
/// </summary>
public static class MediaProxyHeaders
{
    public const string FileIdError = "X-PTM-File-Id-Error";

    public const string RecoveredViaSourceUrlPrefix = "recovered via source_url after file content ";

    /// <summary>Longest header value we emit. The consumer only needs the "HTTP nnn" prefix.</summary>
    public const int MaxHeaderValueLength = 200;

    /// <summary>
    /// Header-safe form of a provider exception message. The value carries a raw provider
    /// response body, which can hold newlines (pretty-printed JSON, an HTML gateway page) or
    /// non-ASCII bytes — Kestrel rejects both, and the throw would turn a successful
    /// <c>source_url</c> recovery into a 500. Collapses whitespace, drops everything outside
    /// printable ASCII, and truncates on a whole character. Empty when nothing usable is left.
    /// </summary>
    public static string SanitizeHeaderValue(string? detail, int maxLength = MaxHeaderValueLength)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";
        if (maxLength <= 0)
            maxLength = MaxHeaderValueLength;

        var sb = new System.Text.StringBuilder(Math.Min(detail.Length, maxLength));
        var pendingSpace = false;
        foreach (var ch in detail)
        {
            if (sb.Length >= maxLength)
                break;
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            // Printable ASCII only: anything else is what Kestrel would throw on.
            if (ch < ' ' || ch > '~')
                continue;
            if (pendingSpace && sb.Length < maxLength)
            {
                sb.Append(' ');
                pendingSpace = false;
                if (sb.Length >= maxLength)
                    break;
            }
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    public static string RecoveredViaSourceUrlStatus(string? fileIdError)
    {
        var code = TryHttpStatus(fileIdError);
        return RecoveredViaSourceUrlPrefix + (code is { } n ? "HTTP " + n : "GET failed");
    }

    public static int? TryHttpStatus(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;
        var i = detail.IndexOf("HTTP ", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > detail.Length)
            return null;
        return int.TryParse(detail.AsSpan(i + 5, 3), out var n) ? n : null;
    }
}
