namespace PageToMovie.Engine;

/// <summary>
/// Caps in-process media inlining (base64 / ReadAllBytes). A clip MP4 on disk or
/// rematerialized from the provider is tens of MB; two overlapping inlines during a
/// same-scene C1+C2 regen can OOM the API host. Check size first, then read.
/// </summary>
public static class ClipInlineMedia
{
    /// <summary>
    /// Hard ceiling for a single inlined file. Gemini inline parts and Railway RAM
    /// both fail well before a combined extend hop (C1+C2) is fully buffered.
    /// </summary>
    public const int MaxInlineBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Throws when <paramref name="path"/> is missing or larger than
    /// <paramref name="maxBytes"/>. Does not read file contents.
    /// </summary>
    public static void EnsureFitsInline(string path, int maxBytes = MaxInlineBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Media path is required to inline.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Media file is missing: {Path.GetFileName(path)}");
        if (maxBytes <= 0)
            maxBytes = MaxInlineBytes;

        var length = new FileInfo(path).Length;
        if (length <= maxBytes)
            return;

        throw new InvalidOperationException(
            $"Refusing to load {Path.GetFileName(path)} ({length} bytes) into API memory. " +
            $"Inline media is capped at {maxBytes} bytes so a large clip cannot take down the host.");
    }
}
