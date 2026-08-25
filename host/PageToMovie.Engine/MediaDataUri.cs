namespace PageToMovie.Engine;

/// <summary>
/// Shared base64 data-URI encoding and media path resolution for local media files sent inline
/// to provider APIs — Grok video generation, extend, edit, and Fal/Gemini clients all use this.
/// Single source of truth for size cap, mime-type mapping, and client-offloaded asset resolution.
/// </summary>
internal static class MediaDataUri
{
    /// <summary>
    /// Same ceiling as every other in-process inline — <see cref="ClipInlineMedia.MaxInlineBytes"/>
    /// is the single source of truth. A data URI costs ~5x the file on the heap (byte[] + base64
    /// string + request body), so a separate, larger cap here is how the host still OOMs on the
    /// video-extend path after the per-file guard was added.
    /// </summary>
    public const int MaxBytes = ClipInlineMedia.MaxInlineBytes;

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

        // Size-check before ReadAllBytes — a leftover combined hop (C1+C2) used as a
        // data-URI fallback would otherwise OOM the API host.
        ClipInlineMedia.EnsureFitsInline(resolvedPath, MaxBytes);
        try
        {
            var bytes = await File.ReadAllBytesAsync(resolvedPath, ct).ConfigureAwait(false);
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
        catch (OutOfMemoryException ex)
        {
            // The size check above is the actual guard. Rethrowing as a job-level error keeps this
            // one call from looking like a provider fault; it does NOT make the process healthy.
            throw new InvalidOperationException(
                $"Ran out of memory inlining {Path.GetFileName(resolvedPath)}. The clip was not loaded.",
                ex);
        }
    }
}

