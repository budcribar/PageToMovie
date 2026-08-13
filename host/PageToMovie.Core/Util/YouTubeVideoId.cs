namespace PageToMovie.Core.Util;

/// <summary>Parse watch / youtu.be / embed / raw 11-char ids for links and thumbs.</summary>
public static class YouTubeVideoId
{
    private static readonly HashSet<string> YoutubePathPrefixes = new(StringComparer.Ordinal)
    {
        "embed", "shorts", "v", "live",
    };

    public static string? Extract(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (TryExtractBareId(s, out var bare))
            return bare;
        if (!TryCreateUri(s, out var uri))
            return null;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return ElevenCharOrNull(uri.AbsolutePath.Trim('/').Split('/')[0]);
        if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase))
            return ExtractFromYoutubeUri(uri);
        return null;
    }

    /// <summary>
    /// True when the input is a 10–12 char id-shaped token (handled, even if not exactly 11).
    /// False means fall through to URI parsing.
    /// </summary>
    private static bool TryExtractBareId(string s, out string? id)
    {
        id = null;
        if (s.Length is < 10 or > 12) return false;
        if (!s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return false;
        id = ElevenCharOrNull(s);
        return true;
    }

    private static bool TryCreateUri(string s, out Uri uri)
    {
        if (Uri.TryCreate(s, UriKind.Absolute, out uri!)) return true;
        return Uri.TryCreate("https://" + s, UriKind.Absolute, out uri!);
    }

    private static string? ExtractFromYoutubeUri(Uri uri)
    {
        var fromQuery = QueryParamV(uri);
        if (!string.IsNullOrWhiteSpace(fromQuery) && fromQuery.Length == 11)
            return fromQuery;
        return ExtractFromYoutubePath(uri);
    }

    private static string? QueryParamV(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static string? ExtractFromYoutubePath(Uri uri)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!YoutubePathPrefixes.Contains(parts[i])) continue;
            var id = ElevenCharOrNull(parts[i + 1]);
            if (id is not null) return id;
        }
        return null;
    }

    private static string? ElevenCharOrNull(string id) => id.Length == 11 ? id : null;

    public static string WatchUrl(string videoId) =>
        $"https://www.youtube.com/watch?v={videoId.Trim()}";

    public static string ThumbnailUrl(string videoId, string quality = "hqdefault") =>
        $"https://img.youtube.com/vi/{videoId.Trim()}/{quality}.jpg";
}
