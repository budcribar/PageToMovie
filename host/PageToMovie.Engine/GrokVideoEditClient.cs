using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// xAI Grok prompt-based clip edit client (<c>POST /v1/videos/edits</c>). Tries a stored Files API
/// file_id first (see <see cref="GrokVideoClient"/>'s <c>storage_options</c> on generation/extend
/// submits), falling back to a base64 data URI of the local clip file on any failure — see
/// <see cref="IVideoEditClient"/>'s doc comment for why the fallback is unconditional.
/// </summary>
public sealed class GrokVideoEditClient : IVideoEditClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;

    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokVideoEditClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public GrokVideoEditClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokVideoEditClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<string> EditClipAsync(
        string videoPath,
        string prompt,
        string? sourceFileId = null,
        string? model = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var entry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.VideoEdit);
        var resolvedModel = entry.Id;

        string requestId;
        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            try
            {
                requestId = await SubmitOnceAsync(
                    resolvedModel, prompt,
                    video: new Dictionary<string, object?> { ["file_id"] = sourceFileId },
                    mode: "file_id", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // xAI's exact "file not found/expired" error shape isn't confirmed by docs, so this
                // doesn't try to distinguish that from any other submit failure — any exception on
                // the file_id path falls back to uploading the local file rather than propagating.
                // The local file always exists (every clip lives on disk), so this is always safe.
                _log.LogWarning(ex,
                    "Grok video edit: file_id path failed, falling back to base64 upload ({FileId})",
                    sourceFileId);
                onProgress?.Invoke("stored file unavailable — uploading clip instead");
                var dataUri = await MediaDataUri.FileToDataUriAsync(videoPath, ct).ConfigureAwait(false);
                requestId = await SubmitOnceAsync(
                    resolvedModel, prompt,
                    video: new Dictionary<string, object?> { ["url"] = dataUri },
                    mode: "upload", ct).ConfigureAwait(false);
            }
        }
        else
        {
            var dataUri = await MediaDataUri.FileToDataUriAsync(videoPath, ct).ConfigureAwait(false);
            requestId = await SubmitOnceAsync(
                resolvedModel, prompt,
                video: new Dictionary<string, object?> { ["url"] = dataUri },
                mode: "upload", ct).ConfigureAwait(false);
        }

        return await PollForEditedUrlAsync(requestId, onProgress, ct).ConfigureAwait(false);
    }

    private async Task<string> SubmitOnceAsync(
        string model, string prompt, Dictionary<string, object?> video, string mode, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["video"] = video,
        };

        var sw = Stopwatch.StartNew();
        try
        {
            // Same reasoning as GrokVideoClient's own submit retry: a lost response here is
            // unrecoverable either way (no request_id to find the job), so automatic retry is no
            // riskier than the manual retry a human would do on seeing the same failure.
            var requestId = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                async _ =>
                {
                    using var resp = await SendJsonAsync(HttpMethod.Post, "videos/edits", payload, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                        throw ChatHttpStatusException.FromResponse(resp,
                            $"Grok video edit submit HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");

                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("request_id", out var rid) ||
                        rid.GetString() is not { Length: > 0 } id)
                    {
                        throw new InvalidOperationException(
                            $"Grok video edit response missing request_id: {Trim(body, 300)}");
                    }
                    return id;
                },
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync(
                    "grok_video_edit_submit", model, $"mode={mode}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);

            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_edit",
                Endpoint = "videos/edits",
                Model = model,
                HttpStatus = 200,
                RequestId = requestId,
                DurationMs = sw.ElapsedMilliseconds,
                Mode = mode,
                Prompt = prompt,
                PromptChars = prompt.Length,
                Ok = true,
            }, ct);
            return requestId;
        }
        catch (Exception ex)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_edit",
                Endpoint = "videos/edits",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Mode = mode,
                Prompt = prompt,
                PromptChars = prompt.Length,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }
    }

    private async Task<string> PollForEditedUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        // Same budget as full video generation (GrokVideoClient.PollForVideoUrlAsync) — edit
        // processing time is not guaranteed short just because input is capped at 8.7s.
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, _opts.GrokTimeoutSeconds));
        var poll = Math.Max(2, _opts.GrokPollSeconds);
        var sw = Stopwatch.StartNew();
        var polls = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            polls++;

            string body;
            try
            {
                body = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                    async _ =>
                    {
                        using var resp = await SendAsync(HttpMethod.Get, $"videos/{requestId}", content: null, ct);
                        var b = await resp.Content.ReadAsStringAsync(ct);
                        if (!resp.IsSuccessStatusCode)
                            throw ChatHttpStatusException.FromResponse(resp,
                                $"Grok video edit poll HTTP {(int)resp.StatusCode}: {Trim(b, 400)}");
                        return b;
                    },
                    isTransient: AiRetryPolicy.IsTransientChatFailure,
                    maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                    backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                    onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync(
                        "grok_video_edit_poll", null, $"requestId={requestId}; poll={polls}", attemptNum, ex, ct),
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.PollFailed, sw.ElapsedMilliseconds, polls, ok: false, ex.Message, ct);
                throw;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("video", out var video) &&
                    video.TryGetProperty("url", out var urlEl) &&
                    urlEl.GetString() is { Length: > 0 } url)
                {
                    await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.Ok, sw.ElapsedMilliseconds, polls, ok: true, null, ct);
                    return url;
                }
                await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, "done with no video.url", ct);
                throw new InvalidOperationException("Grok video edit done with no video.url");
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                var detail = root.TryGetProperty("error", out var err) ? err.ToString() : body;
                await _telemetry.LogOutcomeAsync(null, requestId, string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase) ? VideoJobOutcome.Expired : VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, Trim(detail, 500), ct);
                throw new InvalidOperationException($"Grok video edit job {status}: {Trim(detail, 400)}");
            }

            var progress = root.TryGetProperty("progress", out var pr) ? pr.ToString() : null;
            onProgress?.Invoke(progress is null ? $"status={status}" : $"status={status} ({progress}%)");
            await Task.Delay(TimeSpan.FromSeconds(poll), ct);
        }

        await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.TimedOut, sw.ElapsedMilliseconds, polls, ok: false, $"timed out after {_opts.GrokTimeoutSeconds}s", ct);
        throw new TimeoutException($"Grok video edit timed out after {_opts.GrokTimeoutSeconds}s");
    }

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
        _log.LogInformation("Downloaded edited clip {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }

    /// <summary>Same key source as <see cref="GrokVideoClient"/> — no separate "video-edit key"
    /// concept; edits reuse the existing xAI/Grok key already used for generation.</summary>
    private static string? ResolveApiKey() =>
        ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct)
    {
        var content = JsonContent.Create(payload);
        return await SendAsync(method, uri, content, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        var key = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
