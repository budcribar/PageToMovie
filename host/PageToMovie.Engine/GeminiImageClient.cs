using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Google Gemini image generate + edit client for character portraits, via
/// <c>generateContent</c> with an image response modality (the "Gemini 3 image" / Imagen
/// family — max refs from catalog <c>maxReferenceImages</c>). Response-shape notes
/// below are built from Gemini's public documented format; verify against a live call before
/// relying on this in production.
/// </summary>
public sealed class GeminiImageClient : IImageClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;

    public GeminiImageClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiImageClient> log)
    {
        _http = http;
        _telemetry = telemetry;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Gemini image: model is required. Open Settings and choose an Image generation model.");
        var trimmed = model.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["models/".Length..];
        // Catalog/Settings ids only — never rewrite to a different model.
        return trimmed;
    }

    /// <summary>Text-only portrait generation → n image blobs (one call per variant).</summary>
    public async Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default)
    {
        var modelName = NormalizeModelName(model);
        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var one = await GenerateOneAsync(modelName, prompt, aspectRatio, null, ct).ConfigureAwait(false);
            if (one is not null)
                images.Add(one);
        }
        if (images.Count == 0)
            throw new InvalidOperationException("Gemini image API returned 0 usable images");
        return images;
    }

    /// <summary>Reference-guided edits (character continuity). One call per variant.</summary>
    /// <param name="costumeRefPath">
    /// Optional shared wardrobe-only reference (see <see cref="CharacterDesignService"/>
    /// uniform-lock flow) — attached last with an instruction to copy wardrobe only and
    /// ignore its face/identity.
    /// </param>
    public async Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var modelName = NormalizeModelName(model);
        // Catalog-backed cap, no silent fallback (same "fail loud" principle as Veo's MaxReferenceImages=0).
        var cap = ProviderMediaHelpers.ResolveReferenceImageCap(modelName, maxRefs);

        var (allRefs, costumeClause) = ResolveEditReferences(referenceImagePaths, costumeRefPath, cap);
        var mediumClause = illustratedMedium
            ? " Keep the illustrated/picture-book medium from the refs — not photoreal photography."
            : " Keep the photoreal live-action medium from the refs — not illustration, not cartoon.";

        var images = await GenerateEditVariantsAsync(
            modelName, prompt, costumeClause, mediumClause, aspectRatio, allRefs, n, onProgress, ct)
            .ConfigureAwait(false);
        if (images.Count == 0)
            throw new InvalidOperationException("Gemini image edit returned 0 usable images");
        return images;
    }

    private async Task<byte[]?> GenerateOneAsync(
        string model,
        string prompt,
        string aspectRatio,
        IReadOnlyList<string>? referenceImagePaths,
        CancellationToken ct)
    {
        var parts = new List<object?>();
        if (referenceImagePaths is { Count: > 0 })
            await AddReferenceImagePartsAsync(parts, referenceImagePaths, ct).ConfigureAwait(false);
        parts.Add(new Dictionary<string, object?> { ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["responseModalities"] = new[] { "IMAGE" },
                ["imageConfig"] = new Dictionary<string, object?> { ["aspectRatio"] = aspectRatio },
            },
        };

        var endpoint = $"models/{Uri.EscapeDataString(model)}:generateContent";
        var sw = Stopwatch.StartNew();
        var refNames = (referenceImagePaths ?? Array.Empty<string>())
            .Select(Path.GetFileName).OfType<string>().ToList();
        var kind = ImageCallKind(referenceImagePaths);
        var refNamesOrNull = refNames.Count > 0 ? refNames : null;
        var refsAttached = refNames.Count > 0;
        try
        {
            // Per-request API key — never mutate shared DefaultRequestHeaders (multi-user race).
            using var resp = await ProviderHttpHelpers.SendJsonAsync(
                _http, HttpMethod.Post, endpoint, payload, ct,
                req => ProviderHttpHelpers.ApplyGoogleApiKey(req, ResolveApiKey())).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = kind,
                    Endpoint = endpoint,
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt.Length,
                    ReferenceImagePaths = refNamesOrNull,
                    RefsAttached = refsAttached,
                    Error = ProviderHttpHelpers.Trim(body, 400),
                    Ok = false,
                }, ct);
                throw new InvalidOperationException(
                    $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {ProviderHttpHelpers.Trim(body, 400)}");
            }

            var image = ExtractInlineImage(body);
            var (imageCount, imageOk, imageError) = ImageResultTelemetry(image);
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = kind,
                Endpoint = endpoint,
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt.Length,
                ReferenceImagePaths = refNamesOrNull,
                RefsAttached = refsAttached,
                ImageCount = imageCount,
                Ok = imageOk,
                Error = imageError,
            }, ct);
            return image;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = kind,
                Endpoint = endpoint,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }
    }

    private static (List<string> AllRefs, string CostumeClause) ResolveEditReferences(
        IReadOnlyList<string> referenceImagePaths, string? costumeRefPath, int cap)
    {
        var costumeRef = !string.IsNullOrWhiteSpace(costumeRefPath) && File.Exists(costumeRefPath)
            ? costumeRefPath
            : null;
        var hasCostumeRef = costumeRef is not null;
        var identityCap = hasCostumeRef ? Math.Max(1, cap - 1) : cap;

        var refs = referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(identityCap)
            .ToList();
        if (refs.Count == 0 && !hasCostumeRef)
            throw new InvalidOperationException("No usable reference images for character edit.");

        var allRefs = costumeRef is not null ? refs.Append(costumeRef).ToList() : refs;
        return (allRefs, BuildCostumeClause(hasCostumeRef, refs.Count));
    }

    private static string BuildCostumeClause(bool hasCostumeRef, int identityRefCount)
    {
        if (!hasCostumeRef) return "";
        var clause = " The LAST reference image is a COSTUME REFERENCE ONLY (shared wardrobe design) — " +
              "copy its coat, hat, and badge exactly; completely ignore any face or person in it; " +
              "this character's own face/identity comes from the other reference(s)/text, never from it.";
        if (identityRefCount > 0)
        {
            clause += " Conversely, ignore any hat/coat/badge visible in the OTHER reference(s) — " +
                    "wardrobe comes ONLY from this last costume image, even if the others show " +
                    "different or older wardrobe.";
        }
        return clause;
    }

    private async Task<List<byte[]>> GenerateEditVariantsAsync(
        string modelName,
        string prompt,
        string costumeClause,
        string mediumClause,
        string aspectRatio,
        IReadOnlyList<string> allRefs,
        int n,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"edit variant {i + 1}/{n}");
            var variantPrompt = n > 1
                ? $"{prompt}{costumeClause}{mediumClause} Variation {i + 1} of {n}: tiny pose/expression change only; same identity."
                : $"{prompt}{costumeClause}{mediumClause}";
            var one = await GenerateOneAsync(modelName, variantPrompt, aspectRatio, allRefs, ct)
                .ConfigureAwait(false);
            if (one is not null)
                images.Add(one);
        }
        return images;
    }

    private static async Task AddReferenceImagePartsAsync(
        List<object?> parts, IReadOnlyList<string> referenceImagePaths, CancellationToken ct)
    {
        foreach (var path in referenceImagePaths)
        {
            var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(path, ct).ConfigureAwait(false);
            parts.Add(new Dictionary<string, object?>
            {
                ["inline_data"] = new Dictionary<string, object?> { ["mime_type"] = mime, ["data"] = b64 },
            });
        }
    }

    private static string ImageCallKind(IReadOnlyList<string>? referenceImagePaths) =>
        referenceImagePaths is { Count: > 0 } ? "image_edit" : "image";

    private static (int ImageCount, bool Ok, string? Error) ImageResultTelemetry(byte[]? image) =>
        image is null ? (0, false, "no inline image data in response") : (1, true, null);

    /// <summary>
    /// Gemini image response: <c>candidates[0].content.parts[].inlineData.data</c> (base64).
    /// Returns the first inline image part found, or null if the response was text-only.
    /// Public so tests can exercise it against sample payloads without a live API call.
    /// </summary>
    public static byte[]? ExtractInlineImage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return null;

        var c0 = candidates[0];
        if (!c0.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out var inline) &&
                inline.TryGetProperty("data", out var dataEl) &&
                dataEl.GetString() is { Length: > 0 } b64)
            {
                return Convert.FromBase64String(b64);
            }
            // Some responses use snake_case for this field depending on API version.
            if (part.TryGetProperty("inline_data", out var inlineSnake) &&
                inlineSnake.TryGetProperty("data", out var dataElSnake) &&
                dataElSnake.GetString() is { Length: > 0 } b64Snake)
            {
                return Convert.FromBase64String(b64Snake);
            }
        }
        return null;
    }

    private static string? ResolveApiKey() =>
        ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);
}
