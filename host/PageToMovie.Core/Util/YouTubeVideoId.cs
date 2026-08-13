namespace PageToMovie.Core.Util;

/// <summary>Parse watch / youtu.be / embed / raw 11-char ids for links and thumbs.</summary>
public static class YouTubeVideoId
{
    public static string? Extract(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (s.Length is >= 10 and <= 12 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return s.Length == 11 ? s : null;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            !Uri.TryCreate("https://" + s, UriKind.Absolute, out uri))
            return null;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/').Split('/')[0];
            return id.Length == 11 ? id : null;
        }

        if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            string? v = null;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    v = Uri.UnescapeDataString(kv[1]);
                    break;
                }
            }
            if (!string.IsNullOrWhiteSpace(v) && v.Length == 11) return v;

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] is "embed" or "shorts" or "v" or "live")
                {
                    var id = parts[i + 1];
                    if (id.Length == 11) return id;
                }
            }
        }

        return null;
    }

    public static string WatchUrl(string videoId) =>
        $"https://www.youtube.com/watch?v={videoId.Trim()}";

    public static string ThumbnailUrl(string videoId, string quality = "hqdefault") =>
        $"https://img.youtube.com/vi/{videoId.Trim()}/{quality}.jpg";
}
