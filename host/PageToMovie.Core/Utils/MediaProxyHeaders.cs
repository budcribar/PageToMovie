namespace PageToMovie.Core.Utils;

/// <summary>
/// Headers and LastStatus copy for media-proxy recovery. Same-origin fetch reads
/// <see cref="FileIdError"/> when Files content GET failed but <c>source_url</c> still streamed.
/// </summary>
public static class MediaProxyHeaders
{
    public const string FileIdError = "X-PTM-File-Id-Error";

    public const string RecoveredViaSourceUrlPrefix = "recovered via source_url after file content ";

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
