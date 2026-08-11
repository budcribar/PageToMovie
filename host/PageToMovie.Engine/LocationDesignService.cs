using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Location set plates: generate / edit variants via Grok image, lock to
/// <c>assets/locations/{loc_key}_ref.png</c>. Mirrors CharacterDesignService (simpler — no book plates / wardrobe).
/// </summary>
public sealed class LocationDesignService
{
    private readonly ProjectStore _projects;
    private readonly IImageClient _images;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<LocationDesignService> _log;

    public LocationDesignService(
        ProjectStore projects,
        IImageClient images,
        IOptions<PageToMovieOptions> opts,
        ILogger<LocationDesignService> log)
    {
        _projects = projects;
        _images = images;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<LocationDesignResult> GenerateVariantsAsync(
        string projectId,
        string locKey,
        int n = 3,
        string? descriptionOverride = null,
        string? visualLockOverride = null,
        string? imageEditInstruction = null,
        bool persistDescription = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_images.IsConfigured)
            throw new InvalidOperationException("Image API key is not set (required for location plates).");
        if (string.IsNullOrWhiteSpace(locKey))
            throw new InvalidOperationException("locKey required");

        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var locDir = _projects.GetLocationAssetsDir(projectId);

        var row = _projects.ListLocations(projectId)
            .FirstOrDefault(l => string.Equals(l.Key, locKey, StringComparison.OrdinalIgnoreCase));
        var desc = descriptionOverride ?? row?.Description ?? "";
        var vlock = visualLockOverride ?? row?.VisualLock ?? "";
        if (string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(vlock))
            throw new InvalidOperationException("Location needs a description or visual lock before generating a plate.");

        if (persistDescription && (descriptionOverride is not null || visualLockOverride is not null))
        {
            _projects.UpdateLocationLook(projectId, locKey, descriptionOverride ?? desc, visualLockOverride ?? vlock);
            onProgress?.Invoke("Saved description / visual lock");
        }

        n = Math.Clamp(n <= 0 ? 3 : n, 1, 6);
        var preferred = _projects.ResolveLocationRefPath(projectId, locKey);
        var hasEdit = !string.IsNullOrWhiteSpace(imageEditInstruction) && preferred is not null;
        var imageModel = ProjectModelSelection.RequireImage(
            await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false),
            "Location plate generation");

        string prompt;
        IReadOnlyList<byte[]> blobs;
        string mode;

        if (hasEdit)
        {
            prompt = BuildEditPrompt(locKey, desc, vlock, imageEditInstruction!);
            onProgress?.Invoke($"Grok image edit of locked set plate ({Path.GetFileName(preferred)})…");
            mode = "preferred_edit";
            blobs = await _images.EditVariantsAsync(
                prompt,
                new[] { preferred! },
                n,
                aspectRatio: "16:9",
                model: imageModel,
                maxRefs: 1,
                costumeRefPath: null,
                illustratedMedium: false,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
        }
        else if (preferred is not null)
        {
            // Re-generate variants guided by existing lock + text (set continuity)
            prompt = BuildGeneratePrompt(locKey, desc, vlock, seedFromExisting: true);
            onProgress?.Invoke($"Grok image edit from existing plate ({Path.GetFileName(preferred)})…");
            mode = "preferred_or_text";
            try
            {
                blobs = await _images.EditVariantsAsync(
                    prompt,
                    new[] { preferred },
                    n,
                    aspectRatio: "16:9",
                    model: imageModel,
                    maxRefs: 1,
                    costumeRefPath: null,
                    illustratedMedium: false,
                    onProgress: onProgress,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Location preferred-edit failed; falling back to text-only");
                onProgress?.Invoke($"Edit failed ({ex.Message}); text-only generate…");
                blobs = await _images.GenerateVariantsAsync(
                    BuildGeneratePrompt(locKey, desc, vlock, seedFromExisting: false),
                    n,
                    aspectRatio: "16:9",
                    model: imageModel,
                    ct: ct).ConfigureAwait(false);
                mode = "text_only_fallback";
            }
        }
        else
        {
            prompt = BuildGeneratePrompt(locKey, desc, vlock, seedFromExisting: false);
            onProgress?.Invoke($"generating {n} variant(s) (text-only set plate)…");
            mode = "text_only";
            blobs = await _images.GenerateVariantsAsync(
                prompt, n, aspectRatio: "16:9", model: imageModel, ct: ct).ConfigureAwait(false);
        }

        var paths = new List<string>();
        for (var i = 0; i < blobs.Count && i < n; i++)
        {
            var idx = i + 1;
            var fileName = ProjectStore.LocationVariantFileName(locKey, idx);
            var full = Path.Combine(locDir, fileName);
            await File.WriteAllBytesAsync(full, blobs[i], ct).ConfigureAwait(false);
            paths.Add(full);
            onProgress?.Invoke($"saved variant {idx}/{n} → {fileName}");
        }

        return new LocationDesignResult
        {
            Mode = mode,
            Paths = paths,
            LocKey = locKey,
        };
    }

    public async Task<string> LockVariantAsync(
        string projectId,
        string locKey,
        int variantIndex,
        CancellationToken ct = default)
    {
        if (variantIndex is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(variantIndex), "variant index must be 1..6");
        await _projects.RequireProjectAsync(projectId, ct).ConfigureAwait(false);
        var locDir = _projects.GetLocationAssetsDir(projectId);
        var fileName = ProjectStore.LocationVariantFileName(locKey, variantIndex);
        var variantPath = Path.Combine(locDir, fileName);
        if (!File.Exists(variantPath) || new FileInfo(variantPath).Length < 64)
            throw new InvalidOperationException($"Variant not found: {fileName}");
        var bytes = await File.ReadAllBytesAsync(variantPath, ct).ConfigureAwait(false);
        return _projects.LockLocationRefFromBytes(projectId, locKey, bytes);
    }

    public static string BuildGeneratePrompt(string locKey, string description, string visualLock, bool seedFromExisting)
    {
        var name = locKey.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase)
            ? locKey["Loc_".Length..].Replace('_', ' ')
            : locKey.Replace('_', ' ');
        var sb = new System.Text.StringBuilder();
        if (seedFromExisting)
        {
            sb.Append("Edit this film set / location still. Keep the same place identity, architecture, and era. ");
            sb.Append("Produce a clean cinematic establishing still suitable as a video reference plate. ");
        }
        else
        {
            sb.Append("Create a cinematic film location still (establishing plate) for a movie set. ");
            sb.Append("No people, no faces, no text overlays, no watermark. Photoreal live-action. ");
        }
        sb.Append("Location: ").Append(name).Append(". ");
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append("Description: ").Append(description.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(visualLock))
            sb.Append("Visual lock (must not drift): ").Append(visualLock.Trim()).Append(". ");
        sb.Append("Wide cinematic composition, consistent lighting, clear architecture and materials.");
        return sb.ToString();
    }

    public static string BuildEditPrompt(string locKey, string description, string visualLock, string instruction)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Edit this film location / set reference image. Keep the same place identity and era. ");
        sb.Append("Change only what the instruction asks. No people, no text. ");
        sb.Append("Instruction: ").Append(instruction.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(visualLock))
            sb.Append("Visual lock: ").Append(visualLock.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append("Base description: ").Append(description.Trim()).Append('.');
        return sb.ToString();
    }
}

public sealed class LocationDesignResult
{
    public string Mode { get; init; } = "";
    public string LocKey { get; init; } = "";
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
}
