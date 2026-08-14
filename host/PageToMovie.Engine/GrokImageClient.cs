using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>xAI Grok Imagine image generate + edit client for character portraits.</summary>
public sealed class GrokImageClient : IImageClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;
    private const string KindImage = "image";
    private const string EndpointGenerations = "images/generations";
    private const string EndpointEdits = "images/edits";

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokImageClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public GrokImageClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokImageClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>Text-only portrait generation → n image blobs.</summary>
    public async Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        AspectRatio aspectRatio = AspectRatio.Ratio1x1,
        string? model = null,
        CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model)
            ? throw new InvalidOperationException(
                "Image generation: model is required. Open Settings and choose an Image generation model.")
            : model;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["prompt"] = prompt,
            ["n"] = n,
            ["aspect_ratio"] = aspectRatio.ToApiString(),
            ["response_format"] = "b64_json",
        };

        var sw = Stopwatch.StartNew();
        try
        {
            // Image generation is cheap relative to video, so a whole-request retry on a transient
            // 429/5xx/network blip is worth the (small) risk of an extra generation — same retry
            // GrokChatClient/GrokVisionClient already get. Video/GeminiVideoClient submissions are
            // NOT wrapped this way: a lost response there could mean a duplicate expensive render.
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                attemptNum => GenerateOnceAsync(payload, modelName, prompt, n, sw, attemptNum, ct),
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_image_generate", modelName, $"promptChars={prompt?.Length ?? 0}; n={n}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ChatHttpStatusException && ex is not InvalidOperationException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = KindImage,
                Endpoint = EndpointGenerations,
                Model = modelName,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }
    }

    private async Task<IReadOnlyList<byte[]>> GenerateOnceAsync(
        Dictionary<string, object?> payload,
        string modelName,
        string prompt,
        int n,
        Stopwatch sw,
        int attemptNum,
        CancellationToken ct)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, EndpointGenerations, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = KindImage,
                Endpoint = EndpointGenerations,
                Model = modelName,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt?.Length ?? 0,
                ImageCount = n,
                Attempt = attemptNum,
                Error = Trim(body, 400),
                Ok = false,
            }, ct);
            throw ChatHttpStatusException.FromResponse(resp,
                $"Grok image generations HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");
        }

        var images = ParseImageResponse(body, n, "generations");
        if (images.Count < n)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = KindImage,
                Endpoint = EndpointGenerations,
                Model = modelName,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt?.Length ?? 0,
                ImageCount = images.Count,
                Attempt = attemptNum,
                Error = $"returned {images.Count}/{n} images",
                Ok = false,
            }, ct);
            // Not retried: a content shortfall, not a transport failure.
            throw new InvalidOperationException(
                $"Grok image API returned {images.Count}/{n} usable images");
        }

        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = KindImage,
            Endpoint = EndpointGenerations,
            Model = modelName,
            HttpStatus = (int)resp.StatusCode,
            DurationMs = sw.ElapsedMilliseconds,
            Prompt = prompt,
            PromptChars = prompt?.Length ?? 0,
            ImageCount = images.Count,
            Attempt = attemptNum,
            Ok = true,
        }, ct);
        return images;
    }

    /// <summary>
    /// Reference-guided edits (book plates). One API call per variant for reliability.
    /// </summary>
    /// <param name="costumeRefPath">
    /// Optional wardrobe-only reference (see <see cref="CharacterDesignService"/> uniform-lock
    /// flow): a shared, faceless/generic costume plate reused across several characters so their
    /// coat/hat/badge design stays pixel-identical. Attached as the LAST reference image with an
    /// instruction to copy wardrobe only and ignore its face — never treated as an identity ref.
    /// </param>
    public async Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        AspectRatio aspectRatio = AspectRatio.Ratio1x1,
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model)
            ? throw new InvalidOperationException(
                "Image edit: model is required. Open Settings and choose an Image generation model.")
            : model;
        // Catalog maxReferenceImages is SSoT — no silent fallback, no second invented client ceiling
        // (same "fail loud" principle as Veo's MaxReferenceImages=0).
        var cap = ProviderMediaHelpers.ResolveReferenceImageCap(modelName, maxRefs);

        var hasCostumeRef = HasUsableCostumeRef(costumeRefPath);
        // Reserve one slot for the costume ref so identity refs + costume ref never exceed cap
        var identityCap = hasCostumeRef ? Math.Max(1, cap - 1) : cap;

        var refs = SelectUsableReferencePaths(referenceImagePaths, identityCap);
        if (refs.Count == 0 && !hasCostumeRef)
            throw new InvalidOperationException("No usable reference images for character edit.");

        var (imageUris, identityCount, costumeIndex, refNames) =
            await LoadEditReferenceUrisAsync(refs, hasCostumeRef, costumeRefPath, ct).ConfigureAwait(false);

        using var throttle = new SemaphoreSlim(3, 3);
        var tasks = Enumerable.Range(0, n).Select(i => RunThrottledEditVariantAsync(
            throttle, i, n, prompt, identityCount, costumeIndex, illustratedMedium,
            modelName, aspectRatio, imageUris, refNames, onProgress, ct));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var images = results.Where(b => b is { Length: > 0 }).ToList();

        if (images.Count < 1)
            throw new InvalidOperationException("Image edit returned no variants.");
        return images.Take(n).ToList();
    }

    private static bool HasUsableCostumeRef(string? costumeRefPath) =>
        !string.IsNullOrWhiteSpace(costumeRefPath) && File.Exists(costumeRefPath);

    private static List<string> SelectUsableReferencePaths(IReadOnlyList<string> referenceImagePaths, int identityCap) =>
        referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(identityCap)
            .ToList();

    private async Task<(List<string> ImageUris, int IdentityCount, int CostumeIndex, List<string> RefNames)>
        LoadEditReferenceUrisAsync(
            IReadOnlyList<string> refs,
            bool hasCostumeRef,
            string? costumeRefPath,
            CancellationToken ct)
    {
        // Downscale book plates. Three full-page PNGs as data URIs often exceed the
        // request size limit; two images are more likely to succeed.
        var imageUris = new List<string>(refs.Count + (hasCostumeRef ? 1 : 0));
        foreach (var path in refs)
            imageUris.Add(await FileToDataUriAsync(path, ct, maxEdge: 1024, jpegQuality: 85)
                .ConfigureAwait(false));
        var identityCount = imageUris.Count;
        var costumeIndex = -1;
        if (hasCostumeRef)
        {
            imageUris.Add(await FileToDataUriAsync(costumeRefPath ?? "", ct, maxEdge: 1024, jpegQuality: 85)
                .ConfigureAwait(false));
            costumeIndex = imageUris.Count - 1;
        }

        var refNames = refs.Select(Path.GetFileName).OfType<string>().ToList();
        if (hasCostumeRef && Path.GetFileName(costumeRefPath) is { } costumeFileName)
            refNames.Add(costumeFileName);
        return (imageUris, identityCount, costumeIndex, refNames);
    }

    private async Task<byte[]> RunThrottledEditVariantAsync(
        SemaphoreSlim throttle,
        int i,
        int n,
        string prompt,
        int identityCount,
        int costumeIndex,
        bool illustratedMedium,
        string modelName,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        List<string> refNames,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        await throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EditOneVariantAsync(
                    i, n, prompt, identityCount, costumeIndex, illustratedMedium,
                    modelName, aspectRatio, imageUris, refNames, onProgress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            throttle.Release();
        }
    }

    private static string BuildEditVariantPrompt(
        string prompt,
        int identityCount,
        int costumeIndex,
        bool illustratedMedium,
        int i,
        int n)
    {
        var orderHint = BuildEditOrderHint(identityCount, costumeIndex);
        var variantTail = BuildEditVariantTail(illustratedMedium, i, n);
        var mediumClause = illustratedMedium
            ? "Keep the children's picture-book illustration style from the refs — not photoreal photography. "
            : "Keep the photoreal live-action look from the refs — NOT illustration, NOT cartoon, NOT painted/drawn medium. ";
        return orderHint +
            prompt +
            variantTail +
            mediumClause +
            "If refs show no clothing, do not invent costumes. " +
            "No labels, no redesign, no model sheet.";
    }

    private static string BuildEditOrderHint(int identityCount, int costumeIndex)
    {
        var orderHint = identityCount switch
        {
            > 1 => BuildMultiImageOrderHint(identityCount),
            1 => costumeIndex >= 0
                ? "<IMAGE_0> is the character identity AND art style reference (highest priority over text). "
                : "Match the attached reference identity AND illustration style (highest priority over text). ",
            _ => "",
        };
        if (costumeIndex >= 0)
            orderHint += BuildCostumeRefHint(identityCount, costumeIndex);
        return orderHint;
    }

    private static string BuildCostumeRefHint(int identityCount, int costumeIndex)
    {
        var identityLabel = identityCount switch
        {
            0 => "",
            1 => "<IMAGE_0>",
            _ => $"<IMAGE_0>..<IMAGE_{identityCount - 1}>",
        };
        return
            $"<IMAGE_{costumeIndex}> is a COSTUME REFERENCE ONLY (shared wardrobe design) — " +
            "copy its coat, hat/cap, badge, and garment details exactly. " +
            "COMPLETELY IGNORE any face, body, or person shown in that image — " +
            "this character's own face and identity must come from " +
            (identityCount > 0 ? "the other reference image(s) and " : "") +
            "the text description below, never from the costume reference. " +
            (identityCount > 0
                ? $"Conversely, IGNORE any hat/coat/badge visible in {identityLabel} — " +
                  $"wardrobe comes ONLY from <IMAGE_{costumeIndex}>, even if {identityLabel} shows " +
                  "different or older wardrobe. "
                : "");
    }

    private static string BuildEditVariantTail(bool illustratedMedium, int i, int n)
    {
        if (illustratedMedium)
        {
            return n > 1
                ? $" Variation {i + 1} of {n}: tiny pose/expression change only; " +
                  "same identity, markings, and illustrated medium as the book references. "
                : " Single refined continuity portrait in the book’s illustration style. ";
        }
        return n > 1
            ? $" Variation {i + 1} of {n}: tiny pose/expression change only; " +
              "same identity, markings, and photoreal medium as the reference(s). "
            : " Single refined photoreal continuity portrait matching the reference(s). ";
    }

    private async Task<byte[]> EditOneVariantAsync(
        int i,
        int n,
        string prompt,
        int identityCount,
        int costumeIndex,
        bool illustratedMedium,
        string modelName,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        List<string> refNames,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke($"edit variant {i + 1}/{n}");

        var variantPrompt = BuildEditVariantPrompt(
            prompt, identityCount, costumeIndex, illustratedMedium, i, n);

        var sw = Stopwatch.StartNew();
        // Attempt below is the retry-attempt count (1 = succeeded first try), matching
        // GrokChatClient/GrokVisionClient and what the analytics "retried" stat expects —
        // NOT the variant index (that's already implicit in which task this is; the prompt
        // itself says "Variation i+1 of n").
        var retryAttempt = 1;
        try
        {
            // Same whole-request transient retry as GenerateVariantsAsync — image gen is
            // cheap enough that an extra retried edit isn't a real cost concern.
            var body = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                _ => PostImageEditAsync(modelName, variantPrompt, aspectRatio, imageUris, onProgress, ct),
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) =>
                {
                    retryAttempt = attemptNum + 1;
                    return _errorLogger.LogRetryAttemptAsync("grok_image_edit", modelName, $"variant={i + 1}/{n}", attemptNum, ex, ct);
                },
                ct: ct).ConfigureAwait(false);
            if (body is null)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = "image_edit",
                    Endpoint = EndpointEdits,
                    Model = modelName,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = variantPrompt,
                    PromptChars = variantPrompt.Length,
                    ReferenceImagePaths = refNames,
                    RefsAttached = true,
                    Attempt = retryAttempt,
                    Error = "empty response",
                    Ok = false,
                }, ct);
                throw new InvalidOperationException(
                    $"Image edit failed (variant {i + 1}): empty response");
            }

            var batch = ParseImageResponse(body, 1, $"edits variant {i + 1}");
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "image_edit",
                Endpoint = EndpointEdits,
                Model = modelName,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = variantPrompt,
                PromptChars = variantPrompt.Length,
                ReferenceImagePaths = refNames,
                RefsAttached = true,
                ImageCount = batch.Count,
                Attempt = retryAttempt,
                Ok = true,
            }, ct);
            return batch.FirstOrDefault() ?? Array.Empty<byte>();
        }
        // ChatHttpStatusException IS an InvalidOperationException, but unlike other
        // InvalidOperationExceptions here (validation failures etc.) it comes from
        // PostImageEditAsync's HTTP path and was previously silently uncaught/unlogged —
        // net it into the same log-and-rethrow path as real transport exceptions.
        catch (Exception ex) when (ex is not InvalidOperationException || ex is ChatHttpStatusException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "image_edit",
                Endpoint = EndpointEdits,
                Model = modelName,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = variantPrompt,
                ReferenceImagePaths = refNames,
                Attempt = retryAttempt,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }
    }

    /// <summary>
    /// xAI multi-image: use <c>images</c> (array of data URI strings), mutually exclusive with <c>image</c>.
    /// Prompt cites &lt;IMAGE_0&gt;, &lt;IMAGE_1&gt;, … Single-image keeps <c>image</c> as a string / {url}.
    /// </summary>
    private async Task<string?> PostImageEditAsync(
        string modelName,
        string variantPrompt,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (imageUris.Count > 1)
            return await PostMultiImageEditAsync(modelName, variantPrompt, aspectRatio, imageUris, onProgress, ct)
                .ConfigureAwait(false);
        return await PostSingleImageEditAsync(modelName, variantPrompt, aspectRatio, imageUris, ct)
            .ConfigureAwait(false);
    }

    private static JsonObject BuildImageEditPayload(string modelName, string variantPrompt, AspectRatio aspectRatio) => new()
    {
        ["model"] = modelName,
        ["prompt"] = variantPrompt,
        ["response_format"] = "b64_json",
        ["aspect_ratio"] = aspectRatio.ToApiString(),
    };

    private async Task<(bool Ok, int Code, string Body, TimeSpan? RetryAfter)> SendImageEditAsync(
        JsonObject payload, CancellationToken ct)
    {
        using var content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        // Per-request Bearer — never mutate shared DefaultRequestHeaders (multi-user race).
        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointEdits) { Content = content };
        ProviderHttpHelpers.ApplyBearer(req, ResolveApiKey());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body, AiRetryPolicy.ParseRetryAfter(resp.Headers));
    }

    private async Task<string?> PostMultiImageEditAsync(
        string modelName,
        string variantPrompt,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Primary multi-ref shape per xAI: "images": [ dataUri, ... ]
        var arr = new JsonArray();
        foreach (var u in imageUris)
            arr.Add(u);
        var multi = BuildImageEditPayload(modelName, variantPrompt, aspectRatio);
        multi["images"] = arr;
        var (ok, code, body, retryAfter) = await SendImageEditAsync(multi, ct).ConfigureAwait(false);
        if (ok) return body;

        // Fallback: "image" as string[] (older / alternate parsers)
        var alt = BuildImageEditPayload(modelName, variantPrompt, aspectRatio);
        alt[KindImage] = arr.DeepClone();
        var (ok2, code2, body2, retryAfter2) = await SendImageEditAsync(alt, ct).ConfigureAwait(false);
        if (ok2) return body2;

        // Last resort: drop last ref(s) so 3→2 still produces a portrait
        if (imageUris.Count >= 3)
            return await RetryImageEditWithTwoRefsAsync(
                modelName, variantPrompt, aspectRatio, imageUris, onProgress, ct).ConfigureAwait(false);

        throw new ChatHttpStatusException(code2 != 0 ? code2 : code,
            $"Image edit failed: {Trim(body.Length > 0 ? body : body2, 400)}",
            retryAfter2 ?? retryAfter);
    }

    private async Task<string?> RetryImageEditWithTwoRefsAsync(
        string modelName,
        string variantPrompt,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        onProgress?.Invoke(
            $"3 reference images rejected by API — retrying with first 2…");
        var two = imageUris.Take(2).ToList();
        var cut = variantPrompt.IndexOf("CHARACTER CONTINUITY", StringComparison.OrdinalIgnoreCase);
        if (cut < 0)
            cut = variantPrompt.IndexOf("IDENTITY", StringComparison.OrdinalIgnoreCase);
        var core = cut >= 0 ? variantPrompt[cut..] : variantPrompt;
        var prompt2 = BuildMultiImageOrderHint(2) + core;
        return await PostImageEditAsync(
            modelName, prompt2, aspectRatio, two, onProgress, ct).ConfigureAwait(false);
    }

    private async Task<string?> PostSingleImageEditAsync(
        string modelName,
        string variantPrompt,
        AspectRatio aspectRatio,
        IReadOnlyList<string> imageUris,
        CancellationToken ct)
    {
        // Single image: "image" as data-URI string, then { "url": ... }
        var p = BuildImageEditPayload(modelName, variantPrompt, aspectRatio);
        p[KindImage] = imageUris[0];
        var (okSingle, codeSingle, bodySingle, retryAfterSingle) = await SendImageEditAsync(p, ct).ConfigureAwait(false);
        if (okSingle) return bodySingle;

        var p2 = BuildImageEditPayload(modelName, variantPrompt, aspectRatio);
        p2[KindImage] = new JsonObject { ["url"] = imageUris[0] };
        var (okSingle2, codeSingle2, bodySingle2, retryAfterSingle2) = await SendImageEditAsync(p2, ct).ConfigureAwait(false);
        if (okSingle2) return bodySingle2;

        throw new ChatHttpStatusException(codeSingle2 != 0 ? codeSingle2 : codeSingle,
            $"Image edit failed: {Trim(bodySingle2.Length > 0 ? bodySingle2 : bodySingle, 400)}",
            retryAfterSingle2 ?? retryAfterSingle);
    }

    private static string BuildMultiImageOrderHint(int count)
    {
        var sb = new StringBuilder();
        sb.Append("Multi-reference edit. ");
        for (var i = 0; i < count; i++)
            sb.Append($"<IMAGE_{i}> is reference {i + 1}. ");
        sb.Append("<IMAGE_0> is the identity / style lock (highest priority). ");
        if (count > 1)
            sb.Append("Later images are the SAME character for markings and style only. ");
        return sb.ToString();
    }

    private static List<byte[]> ParseImageResponse(string json, int n, string label)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Grok image API returned no image data ({label}): {Trim(json, 300)}");
        }

        var images = new List<byte[]>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("b64_json", out var b64) &&
                b64.GetString() is { Length: > 0 } s)
            {
                images.Add(Convert.FromBase64String(s));
            }
            // URL form is rare with response_format=b64_json; skip for now
        }

        if (images.Count < 1)
            throw new InvalidOperationException(
                $"Grok image API returned 0 usable images ({label})");
        return images.Take(n).ToList();
    }

    /// <summary>
    /// Encode a local image as a data URI. Large book pages are downscaled (Skia)
    /// so multi-ref edits (up to 3) stay under API body limits.
    /// </summary>
    private async Task<string> FileToDataUriAsync(
        string path,
        CancellationToken ct,
        int maxEdge = 1280,
        int jpegQuality = 88)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            using var original = SKBitmap.Decode(bytes);
            if (original is null)
                return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

            var w = original.Width;
            var h = original.Height;
            var edge = Math.Max(w, h);
            SKBitmap work = original;
            SKBitmap? scaled = null;
            if (edge > maxEdge && edge > 0)
            {
                var scale = maxEdge / (float)edge;
                var nw = Math.Max(1, (int)Math.Round(w * scale));
                var nh = Math.Max(1, (int)Math.Round(h * scale));
                // Bilinear + mipmap (matches the old SKFilterQuality.Medium) — SKSamplingOptions.Default
                // is nearest-neighbor with no mipmapping and visibly aliases downscaled reference photos.
                scaled = original.Resize(new SKImageInfo(nw, nh), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (scaled is not null)
                    work = scaled;
            }

            using (scaled)
            using (var image = SKImage.FromBitmap(work))
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality))
            {
                if (data is null)
                    return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                if (data.Size < bytes.Length || edge > maxEdge)
                {
                    _log.LogDebug(
                        "Ref {File}: {SrcKb:0} KB → {DstKb:0} KB (maxEdge={Edge})",
                        Path.GetFileName(path), bytes.Length / 1024.0, data.Size / 1024.0, maxEdge);
                }
                return $"data:image/jpeg;base64,{Convert.ToBase64String(data.Span)}";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not re-encode {Path}; sending original bytes", path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/jpeg",
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
    }

    private static string? ResolveApiKey() =>
        ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct) =>
        ProviderHttpHelpers.SendJsonAsync(
            _http, method, uri, payload, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, ResolveApiKey()));

    private static string Trim(string s, int n) => ProviderHttpHelpers.Trim(s, n);

    Task<IReadOnlyList<byte[]>> IImageClient.GenerateVariantsAsync(
        string prompt,
        int n,
        string aspectRatio,
        string? model,
        CancellationToken ct)
        => GenerateVariantsAsync(prompt, n, MediaEngineEnumExtensions.ParseAspectRatio(aspectRatio), model, ct);

    Task<IReadOnlyList<byte[]>> IImageClient.EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n,
        string aspectRatio,
        string? model,
        int maxRefs,
        string? costumeRefPath,
        bool illustratedMedium,
        Action<string>? onProgress,
        CancellationToken ct)
        => EditVariantsAsync(prompt, referenceImagePaths, n, MediaEngineEnumExtensions.ParseAspectRatio(aspectRatio), model, maxRefs, costumeRefPath, illustratedMedium, onProgress, ct);
}
