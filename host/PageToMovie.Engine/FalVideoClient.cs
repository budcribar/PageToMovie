using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Fal.ai serverless GPU client for HunyuanVideo (13B DiT open-source video generation).
/// Queue endpoint: https://queue.fal.run/fal-ai/hunyuan-video
/// </summary>
public sealed class FalVideoClient : IVideoClient
{
    public const string ApiBase = SupportedModelCatalog.FalApiBase;

    private readonly HttpClient _http;
    private readonly ILogger<FalVideoClient> _log;

    public FalVideoClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<FalVideoClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveFal();

    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null,
        string? aspectRatio = null)
    {
        var apiKey = ResolveApiKey()
            ?? throw new InvalidOperationException($"Fal.ai API key is missing. Set {SupportedModelCatalog.FalApiKeyEnv} in environment or Configuration.");

        var catalogEntry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video);
        var maxLen = catalogEntry.MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Fal video: model '{catalogEntry.Id}' has no maxPromptLength in models_catalog.json.");
        if (prompt.Length > maxLen)
        {
            prompt = ClipVideoPromptBuilder.FitPromptToVideoBudget(prompt, maxLen);
        }

        // Frame/step params only when the catalog declares them (Hunyuan). Duration-native
        // models (Wan) omit these — do not invent defaults.
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["aspect_ratio"] = ResolveAspectRatio(model, aspectRatio),
        };
        ApplyFrameCountParams(payload, catalogEntry, durationSeconds);
        if (catalogEntry.NumInferenceSteps is { } steps)
            payload["num_inference_steps"] = steps;

        var imagePath = ResolveInitImagePath(startFrameImagePath, referenceImagePaths);

        // Catalog endpointPath is SSoT (e.g. fal-ai/hunyuan-video). When an init image is present,
        // hunyuan's i2v path is the text endpoint + "-image-to-video"; other fal models already list
        // their full i2v path as endpointPath.
        var endpoint = !string.IsNullOrWhiteSpace(catalogEntry.EndpointPath)
            ? catalogEntry.EndpointPath.Trim().TrimStart('/')
            : throw new InvalidOperationException(
                $"Fal video: model '{catalogEntry.Id}' has no endpointPath in models_catalog.json.");
        if (!string.IsNullOrWhiteSpace(imagePath))
            endpoint = await AttachInitImageAsync(payload, catalogEntry, endpoint, imagePath, ct).ConfigureAwait(false);

        using var posted = await FalHttp.PostJsonOrThrowAsync(
            new HttpCall(_http, apiKey, _log, ct), endpoint, payload,
            "HunyuanVideo submit", "Fal.ai HunyuanVideo error").ConfigureAwait(false);

        if (!posted.Root.TryGetProperty("request_id", out var reqIdEl) ||
            reqIdEl.GetString() is not { Length: > 0 } reqId)
        {
            throw new InvalidOperationException($"Fal.ai response missing request_id: {posted.Body}");
        }

        _log.LogInformation("Fal.ai HunyuanVideo job submitted to {Endpoint}: {RequestId}", endpoint, reqId);
        return $"{endpoint}:{reqId}";
    }

    private static void ApplyFrameCountParams(
        Dictionary<string, object?> payload, SupportedModelEntry catalogEntry, int durationSeconds)
    {
        if (catalogEntry.ShortClipFrameCount is not null || catalogEntry.LongClipFrameCount is not null)
        {
            var numFrames = durationSeconds is > 0 and <= 4
                ? catalogEntry.ShortClipFrameCount
                    ?? throw new InvalidOperationException(
                        $"Fal video: model '{catalogEntry.Id}' has longClipFrameCount but no shortClipFrameCount.")
                : catalogEntry.LongClipFrameCount
                    ?? throw new InvalidOperationException(
                        $"Fal video: model '{catalogEntry.Id}' has shortClipFrameCount but no longClipFrameCount.");
            payload["num_frames"] = numFrames;
        }
    }

    private static string? ResolveInitImagePath(string? startFrameImagePath, IReadOnlyList<string>? referenceImagePaths)
    {
        if (!string.IsNullOrWhiteSpace(startFrameImagePath) && File.Exists(startFrameImagePath))
            return startFrameImagePath;
        if (referenceImagePaths is { Count: > 0 } && File.Exists(referenceImagePaths[0]))
            return referenceImagePaths[0];
        return null;
    }

    private static async Task<string> AttachInitImageAsync(
        Dictionary<string, object?> payload,
        SupportedModelEntry catalogEntry,
        string endpoint,
        string imagePath,
        CancellationToken ct)
    {
        if (!endpoint.Contains("image-to-video", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint.TrimEnd('/') + "-image-to-video";
        var maxDim = catalogEntry.MaxReferenceImageDimension
            ?? throw new InvalidOperationException(
                $"Fal video: model '{catalogEntry.Id}' has no maxReferenceImageDimension in models_catalog.json.");
        payload["image_url"] = await PrepareOptimizedImageDataUriAsync(imagePath, maxDim, ct).ConfigureAwait(false);
        return endpoint;
    }

    public async Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var apiKey = ResolveApiKey()
            ?? throw new InvalidOperationException($"Fal.ai API key is missing ({SupportedModelCatalog.FalApiKeyEnv}).");

        var (endpoint, actualReqId) = SplitPollRequestId(requestId);
        var statusUrl = $"{endpoint}/requests/{actualReqId}/status";
        var resultUrl = $"{endpoint}/requests/{actualReqId}";

        var delay = TimeSpan.FromSeconds(3);
        var maxAttempts = 300; // 15 minutes max timeout

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var statusBody = await GetStatusBodyAsync(statusUrl, apiKey, requestId, onProgress, ct).ConfigureAwait(false);
            if (statusBody is null)
                continue;

            using var doc = JsonDocument.Parse(statusBody);
            var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
            var queuePos = doc.RootElement.TryGetProperty("queue_position", out var qEl) ? qEl.GetInt32().ToString() : null;
            onProgress?.Invoke(queuePos is not null ? $"Fal.ai status: {status} (queue position: {queuePos})" : $"Fal.ai status: {status}");

            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return await FetchCompletedVideoUrlAsync(resultUrl, apiKey, ct).ConfigureAwait(false);
            ThrowIfFalFailed(status, doc.RootElement);

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Fal.ai job {requestId} timed out after {maxAttempts * 3}s");
    }

    private static (string Endpoint, string RequestId) SplitPollRequestId(string requestId)
    {
        var parts = requestId.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new InvalidOperationException(
                $"Fal video poll: request id must be 'endpoint:requestId' (got '{requestId}').");
        return (parts[0], parts[1]);
    }

    /// <summary>Null means the caller should <c>continue</c> (rate-limited 429).</summary>
    private async Task<string?> GetStatusBodyAsync(
        string statusUrl, string apiKey, string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var raw = await FalHttp.GetAsync(_http, statusUrl, apiKey, ct).ConfigureAwait(false);
        if (raw.IsSuccess) return raw.Body;
        if (await FalPollingHelpers.HandleRateLimitAsync(raw.StatusCode, onProgress, ct).ConfigureAwait(false))
            return null;
        _log.LogError("Fal.ai status query failed HTTP {Status} for request {RequestId}: {Body}", raw.StatusCode, requestId, raw.Body);
        throw new InvalidOperationException($"Fal.ai status query error HTTP {raw.StatusCode}: {raw.Body}");
    }

    private async Task<string> FetchCompletedVideoUrlAsync(string resultUrl, string apiKey, CancellationToken ct)
    {
        var raw = await FalHttp.GetAsync(_http, resultUrl, apiKey, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
            throw new InvalidOperationException($"Fal.ai result fetch failed HTTP {raw.StatusCode}: {raw.Body}");

        using var resultDoc = JsonDocument.Parse(raw.Body);
        if (FalHttp.TryGetObjectUrl(resultDoc.RootElement, "video") is { } videoUrl)
            return videoUrl;
        throw new InvalidOperationException($"Fal.ai result payload missing video.url: {raw.Body}");
    }

    private static void ThrowIfFalFailed(string status, JsonElement root)
    {
        if (!string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            return;
        var err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "Job failed";
        throw new InvalidOperationException($"Fal.ai generation failed on GPU: {err}");
    }

    /// <summary>Fal never requests xAI-style Files API storage — file_id reuse is a Grok-only
    /// optimization (see <see cref="IVideoEditClient"/>).</summary>
    public (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId) => (null, null);

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Requested aspect ratio if given, else catalog's <c>DefaultAspectRatio</c> for the requested Fal.ai model,
    /// falling back to the historical hardcoded "16:9" for models the catalog doesn't cover yet.
    /// </summary>
    private static string ResolveAspectRatio(string model, string? requestedAspectRatio = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedAspectRatio))
            return requestedAspectRatio;
        return SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).DefaultAspectRatio ?? "16:9";
    }

    private static async Task<string> PrepareOptimizedImageDataUriAsync(string imagePath, int maxDim, CancellationToken ct)
    {
        try
        {
            var bytes = await Task.Run<byte[]?>(() =>
            {
                using var original = SkiaSharp.SKBitmap.Decode(imagePath);
                if (original is null) return null;

                int width = original.Width;
                int height = original.Height;

                if (width > maxDim || height > maxDim)
                {
                    float scale = Math.Min((float)maxDim / width, (float)maxDim / height);
                    int newW = Math.Max(1, (int)(width * scale));
                    int newH = Math.Max(1, (int)(height * scale));

                    using var resized = original.Resize(new SkiaSharp.SKImageInfo(newW, newH), SkiaSharp.SKSamplingOptions.Default);
                    if (resized is not null)
                    {
                        using var image = SkiaSharp.SKImage.FromBitmap(resized);
                        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
                        return data.ToArray();
                    }
                }

                using var img = SkiaSharp.SKImage.FromBitmap(original);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
                return enc.ToArray();
            }, ct).ConfigureAwait(false);

            bytes ??= await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);

            return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            var rawBytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(rawBytes)}";
        }
    }
}
