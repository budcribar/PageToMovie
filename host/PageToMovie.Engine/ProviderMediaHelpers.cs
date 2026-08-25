using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Small provider-agnostic helpers shared by the concrete image/vision/video clients: reading a
/// local file into a base64 payload with an extension-derived MIME type, and resolving the
/// catalog-backed reference-image cap for a paid image-generation call.
/// </summary>
internal static class ProviderMediaHelpers
{
    /// <summary>
    /// Reads a local file and returns its (mime, base64). MIME is derived from the extension:
    /// png/webp/gif/jpeg are recognized; anything else falls back to <c>image/jpeg</c>. When
    /// <paramref name="allowVideo"/> is set, <c>.mp4</c> maps to <c>video/mp4</c> (chat multimodal);
    /// otherwise it falls through to the image default — matching each client's original switch.
    /// </summary>
    public static async Task<(string Mime, string Base64)> FileToBase64Async(
        string path, CancellationToken ct, bool allowVideo = false)
    {
        // Size-check before ReadAllBytes — a combined extend hop inlined during the
        // next clip's download is how a two-clip same-scene regen OOMs Railway.
        ClipInlineMedia.EnsureFitsInline(path);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".mp4" when allowVideo => "video/mp4",
                _ => "image/jpeg",
            };
            return (mime, Convert.ToBase64String(bytes));
        }
        catch (OutOfMemoryException ex)
        {
            // The size check above is the actual guard. Rethrowing as a job-level error keeps this
            // one call from looking like a provider fault; it does NOT make the process healthy.
            throw new InvalidOperationException(
                $"Ran out of memory inlining {Path.GetFileName(path)}. The clip was not loaded.",
                ex);
        }
    }

    /// <summary>
    /// Resolves the reference-image cap for an image model. The catalog's <c>maxReferenceImages</c>
    /// is the source of truth; there is no silent fallback — an image generation call costs real
    /// money, so an unverified limit throws (fail loud) rather than guessing. A positive
    /// <paramref name="maxRefs"/> caller hint is clamped into <c>[1, catalogCap]</c>.
    /// </summary>
    public static int ResolveReferenceImageCap(string modelName, int maxRefs)
    {
        if (SupportedModelCatalog.Find(modelName, ModelCapability.Image)?.MaxReferenceImages is not { } catalogCap)
        {
            throw new InvalidOperationException(
                $"No catalog maxReferenceImages for image model '{modelName}' — refusing to start " +
                "a paid image generation call with an unverified reference-image limit. Populate " +
                "models_catalog.json for this model before using it.");
        }
        return maxRefs > 0
            ? Math.Clamp(maxRefs, 1, catalogCap)
            : catalogCap;
    }
}
