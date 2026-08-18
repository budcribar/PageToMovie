namespace PageToMovie.Engine;

/// <summary>
/// Shared base64 data-URI encoding and media path resolution for local media files sent inline
/// to provider APIs — Grok video generation, extend, edit, and Fal/Gemini clients all use this.
/// Single source of truth for size cap, mime-type mapping, and client-offloaded asset resolution.
/// </summary>
internal static class MediaDataUri
{
    /// <summary>Guard huge uploads — short clips are fine; multi-MB is ok for a 6-10s mp4.</summary>
    public const int MaxBytes = 40 * 1024 * 1024;

    /// <summary>
    /// Checks if a media path exists as physical bytes, a candidate variant, or a client marker.
    /// </summary>
    public static bool IsExistingMediaPath(string? path) =>
        !string.IsNullOrWhiteSpace(ResolveExistingMediaPath(path));

    /// <summary>
    /// Resolves the actual readable file path for a media asset, checking exact path, candidate
    /// files, variants, and client markers (handling offloaded / client-marked assets). Returns null if not found.
    /// </summary>
    public static string? ResolveExistingMediaPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (File.Exists(path) && new FileInfo(path).Length >= 64)
            return path;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            if (File.Exists(path + ProjectStore.ClientMarkerExtension))
                return path;
            return null;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(path);

        // 1. Direct name match with any extension (excluding .client.json)
        var candidate = Directory.EnumerateFiles(dir, $"{nameWithoutExt}*")
            .FirstOrDefault(f => !f.EndsWith(ProjectStore.ClientMarkerExtension, StringComparison.OrdinalIgnoreCase) &&
                                 new FileInfo(f).Length >= 64);
        if (candidate is not null)
            return candidate;

        // 2. Variant fallback for _ref paths (e.g. loc_country_lane_variant_01.png for loc_country_lane_ref.png)
        var prefix = nameWithoutExt.EndsWith("_ref", StringComparison.OrdinalIgnoreCase)
            ? nameWithoutExt[..^"_ref".Length]
            : nameWithoutExt;
        var variantCandidate = Directory.EnumerateFiles(dir, $"{prefix}*")
            .FirstOrDefault(f => !f.EndsWith(ProjectStore.ClientMarkerExtension, StringComparison.OrdinalIgnoreCase) &&
                                 new FileInfo(f).Length >= 64);
        if (variantCandidate is not null)
            return variantCandidate;

        // 3. Client marker fallback (e.g. path.client.json exists)
        if (File.Exists(path + ProjectStore.ClientMarkerExtension))
            return path;

        return null;
    }

    public static async Task<string> FileToDataUriAsync(string path, CancellationToken ct)
    {
        var resolvedPath = ResolveExistingMediaPath(path) ?? path;
        if (!File.Exists(resolvedPath) || new FileInfo(resolvedPath).Length <= 0)
        {
            throw new FileNotFoundException(
                $"Media file '{Path.GetFileName(path)}' not found on server at '{resolvedPath}'. " +
                "Ensure client media folder is connected so required reference plates are uploaded to the server before generation.",
                resolvedPath);
        }

        var bytes = await File.ReadAllBytesAsync(resolvedPath, ct).ConfigureAwait(false);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException(
                $"Video/image too large for data URI ({bytes.Length / (1024 * 1024)} MB). Max {MaxBytes / (1024 * 1024)} MB.");
        var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "image/jpeg",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }
}

