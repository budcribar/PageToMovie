namespace PageToMovie.Cut.Cut;

internal static class CutDebugFolder
{
    internal const string Flag = "debugMaryTest";
    internal const string ManifestUrl = "debug-marytest.json";

    internal static bool TryManifestUrl(string? navigationUrl, out string manifestUrl)
    {
        manifestUrl = "";
#if !DEBUG
        return false;
#else
        if (!Uri.TryCreate(navigationUrl, UriKind.Absolute, out var uri))
            return false;
        var enabled = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(pair => Uri.UnescapeDataString(pair[0]).Equals(Flag, StringComparison.OrdinalIgnoreCase)
                && pair.Length == 2
                && Uri.UnescapeDataString(pair[1]) is "1" or "true");
        if (!enabled)
            return false;
        manifestUrl = new Uri(uri, ManifestUrl).AbsoluteUri;
        return true;
#endif
    }
}
