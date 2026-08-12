namespace PageToMovie.Core.Utils;

/// <summary>
/// Canonical utility for sanitizing filenames and path segments against invalid filesystem characters.
/// Uses a <b>portable</b> invalid set (Windows + Unix) so names written on Linux stay extractable on Windows.
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>
    /// Characters illegal on Windows file names, plus path separators.
    /// Do not use <see cref="Path.GetInvalidFileNameChars"/> alone — on Linux it omits <c>:</c> etc.,
    /// so lease keys like <c>loc:Hall</c> land in zips that fail extract (0x80070057) on Windows.
    /// </summary>
    private static readonly HashSet<char> InvalidChars = new(
    [
        '"', '<', '>', '|', '\0',
        ':', '*', '?',
        '/', '\\',
        // C0 controls (Windows rejects these too)
        '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
        '\u0008', '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u000E', '\u000F',
        '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017',
        '\u0018', '\u0019', '\u001A', '\u001B', '\u001C', '\u001D', '\u001E', '\u001F',
    ]);

    /// <summary>
    /// Replaces invalid filename characters with the specified replacement char (default '_').
    /// Also strips directory separators Unix '/' and Windows '\'.
    /// </summary>
    public static string SanitizeFileName(string? name, char replacement = '_')
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (InvalidChars.Contains(chars[i]))
                chars[i] = replacement;
        }
        var s = new string(chars).Trim().TrimEnd('.');
        // Windows reserved device names
        if (s is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            s = "_" + s;
        return string.IsNullOrEmpty(s) ? "_" : s;
    }

    /// <summary>
    /// Sanitize a relative path for zip entry / cross-platform extract (each segment separately).
    /// Keeps directory structure; never allows <c>..</c> segments.
    /// </summary>
    public static string SanitizeRelativePath(string? relativePath, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p is "." or "..") continue;
            var s = SanitizeFileName(p, replacement);
            if (!string.IsNullOrEmpty(s))
                safe.Add(s);
        }
        return string.Join('/', safe);
    }
}
