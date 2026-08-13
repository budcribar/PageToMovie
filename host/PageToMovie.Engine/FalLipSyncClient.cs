using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Fal.ai / Sync Labs video lip-sync client. Queue endpoint, same convention as
/// <see cref="FalVideoClient"/>: submit → poll <c>/requests/{id}/status</c> → fetch
/// <c>/requests/{id}</c> for the result. Default model <c>fal-ai/sync-lipsync/v2/pro</c>
/// (see models_catalog.json). Explicit, human-triggered only — never call this from an
/// automatic job/pipeline; every call spends real provider money.
/// </summary>
public sealed class FalLipSyncClient : ILipSyncClient
{
    public const string ApiBase = SupportedModelCatalog.FalApiBase;

    /// <summary>
    /// A short clip's lip-sync job should resolve in well under this — generous ceiling in case
    /// of provider queue backpressure. Much shorter than FalVideoClient's 15-minute video-gen
    /// timeout since lip-sync only re-renders mouth region on an already-generated clip.
    /// </summary>
    private const int MaxPollAttempts = 100; // * 3s delay = 5 minutes
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly ILogger<FalLipSyncClient> _log;

    public FalLipSyncClient(HttpClient http, ILogger<FalLipSyncClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase.TrimEnd('/') + '/');
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveFal();

    public async Task<string?> GenerateLipSyncAsync(
        string videoPath,
        string audioPath,
        string? model = null,
        string syncMode = "cut_off",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("Fal.ai API key is missing — skipping lip-sync.");
            return null;
        }
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Lip-sync source video not found.", videoPath);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("Lip-sync source audio not found.", audioPath);

        var entry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.LipSync);
        var endpoint = entry.EndpointPath;

        onProgress?.Invoke("Preparing video and audio for lip-sync…");
        var videoDataUri = await ToDataUriAsync(videoPath, "video/mp4", ct).ConfigureAwait(false);
        var audioDataUri = await ToDataUriAsync(audioPath, "audio/mpeg", ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>
        {
            ["video_url"] = videoDataUri,
            ["audio_url"] = audioDataUri,
            ["sync_mode"] = string.IsNullOrWhiteSpace(syncMode) ? "cut_off" : syncMode,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        req.Content = JsonContent.Create(payload);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Fal.ai lip-sync submit failed HTTP {Status} ({Elapsed}ms): {Body}", resp.StatusCode, sw.ElapsedMilliseconds, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("request_id", out var reqIdEl) ||
            reqIdEl.GetString() is not { Length: > 0 } requestId)
        {
            _log.LogError("Fal.ai lip-sync response missing request_id: {Body}", body);
            return null;
        }

        _log.LogInformation("Fal.ai lip-sync job submitted to {Endpoint}: {RequestId}", endpoint, requestId);
        onProgress?.Invoke("Lip-sync submitted — waiting for result…");

        return await PollForResultUrlAsync(endpoint, requestId, apiKey, onProgress, ct).ConfigureAwait(false);
    }

    private async Task<string?> PollForResultUrlAsync(
        string endpoint,
        string requestId,
        string apiKey,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var statusUrl = $"{endpoint}/requests/{requestId}/status";
        var resultUrl = $"{endpoint}/requests/{requestId}";

        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (hardFail, statusBody) = await ReadStatusBodyAsync(statusUrl, apiKey, onProgress, ct).ConfigureAwait(false);
            if (hardFail) return null;
            if (statusBody is null) continue;

            using var doc = JsonDocument.Parse(statusBody);
            var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
            onProgress?.Invoke($"Fal.ai lip-sync status: {status}");

            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return await FetchCompletedVideoUrlAsync(resultUrl, apiKey, ct).ConfigureAwait(false);

            if (TryLogFailedStatus(status, doc.RootElement))
                return null;

            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
        }

        _log.LogError("Fal.ai lip-sync job {RequestId} timed out after {Seconds}s", requestId, MaxPollAttempts * PollDelay.TotalSeconds);
        return null;
    }

    /// <summary>
    /// HardFail means stop polling. A null body without HardFail means rate-limited — caller should continue.
    /// </summary>
    private async Task<(bool HardFail, string? Body)> ReadStatusBodyAsync(
        string statusUrl, string apiKey, Action<string>? onProgress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var statusBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
            return (false, statusBody);
        if (await FalPollingHelpers.HandleRateLimitAsync(resp, onProgress, ct).ConfigureAwait(false))
            return (false, null);
        _log.LogError("Fal.ai lip-sync status query failed HTTP {Status}: {Body}", resp.StatusCode, statusBody);
        return (true, null);
    }

    private async Task<string?> FetchCompletedVideoUrlAsync(string resultUrl, string apiKey, CancellationToken ct)
    {
        using var resultReq = new HttpRequestMessage(HttpMethod.Get, resultUrl);
        resultReq.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        using var resultResp = await _http.SendAsync(resultReq, ct).ConfigureAwait(false);
        var resultBody = await resultResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resultResp.IsSuccessStatusCode)
        {
            _log.LogError("Fal.ai lip-sync result fetch failed HTTP {Status}: {Body}", resultResp.StatusCode, resultBody);
            return null;
        }

        using var resultDoc = JsonDocument.Parse(resultBody);
        if (resultDoc.RootElement.TryGetProperty("video", out var vEl) &&
            vEl.TryGetProperty("url", out var urlEl) &&
            urlEl.GetString() is { Length: > 0 } videoUrl)
        {
            return videoUrl;
        }
        _log.LogError("Fal.ai lip-sync result payload missing video.url: {Body}", resultBody);
        return null;
    }

    private bool TryLogFailedStatus(string status, JsonElement root)
    {
        if (!string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            return false;
        var err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "Job failed";
        _log.LogError("Fal.ai lip-sync generation failed: {Error}", err);
        return true;
    }

    /// <summary>
    /// Base64 data URI for a local file — same convention as FalVideoClient's reference-image
    /// upload. Fal.ai accepts data URIs for file inputs, but recommends CDN upload for anything
    /// beyond a few MB (see fal.ai/docs/documentation/development/working-with-files); this app's
    /// clips are typically a few seconds long so stay well within that, but a genuinely large
    /// source file will be slow to submit rather than uploaded to Fal's CDN first — see the
    /// project report for this as a known follow-up if longer clips need lip-sync.
    /// </summary>
    private static async Task<string> ToDataUriAsync(string path, string fallbackContentType, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var contentType = ResolveContentType(path) ?? fallbackContentType;
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>Exposed for tests — pure MIME-type mapping, no I/O.</summary>
    public static string? ResolveContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        _ => null,
    };
}
