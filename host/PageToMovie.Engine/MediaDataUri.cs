namespace PageToMovie.Engine;

/// <summary>
/// Shared base64 data-URI encoding for local media files sent inline to a provider API — Grok
/// video generation, extend, and edit all use this, the only difference being which request field
/// the URI lands in. Single source of truth for the size cap and mime-type mapping.
/// </summary>
internal static class MediaDataUri
{
    /// <summary>Guard huge uploads — short clips are fine; multi-MB is ok for a 6-10s mp4.</summary>
    public const int MaxBytes = 40 * 1024 * 1024;

    public static async Task<string> FileToDataUriAsync(string path, CancellationToken ct)
    {
        var resolvedPath = path;
        if (!File.Exists(resolvedPath))
        {
            var dir = Path.GetDirectoryName(path);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var candidate = Directory.EnumerateFiles(dir, $"{nameWithoutExt}*")
                    .Where(f => !f.EndsWith(".client.json", StringComparison.OrdinalIgnoreCase) &&
                                new FileInfo(f).Length >= 64)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    resolvedPath = candidate;
                }
            }
        }

        var bytes = await File.ReadAllBytesAsync(resolvedPath, ct);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException(
                $"Video/image too large for data URI ({bytes.Length / (1024 * 1024)} MB). Max {MaxBytes / (1024 * 1024)} MB.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
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
