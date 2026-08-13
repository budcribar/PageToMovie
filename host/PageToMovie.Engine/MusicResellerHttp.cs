using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Shared submit / poll HTTP+JSON for the unofficial Suno reseller clients
/// (<see cref="SunoClient"/>, <see cref="AiMusicApiClient"/>). Vendor payload
/// shapes and poll-body interpretation stay at each call site.
/// </summary>
internal static class MusicResellerHttp
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(6);

    /// <summary>
    /// POST JSON with Bearer auth. Returns the response body on HTTP success;
    /// logs + progress-invokes and returns null on a non-success status.
    /// </summary>
    public static async Task<string?> PostJsonOrNullAsync(
        HttpClient http,
        string relativeUri,
        object payload,
        string apiKey,
        ILogger log,
        Action<string>? onProgress,
        string submittingMessage,
        string failedLogTemplate,
        string failedProgressPrefix,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(payload);

        onProgress?.Invoke(submittingMessage);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return body;

        log.LogError(failedLogTemplate, resp.StatusCode, body);
        onProgress?.Invoke($"{failedProgressPrefix}{(int)resp.StatusCode}");
        return null;
    }

    /// <summary>
    /// Parses a submit-response task id. Logs and returns null on unparseable JSON
    /// or a missing/blank id — same control flow both resellers already used.
    /// </summary>
    public static string? ReadTaskIdOrNull(
        string body,
        ILogger log,
        Func<JsonElement, string?> readId,
        string unparseableLogTemplate,
        string missingIdLogTemplate)
    {
        string? taskId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            taskId = readId(doc.RootElement);
        }
        catch (JsonException ex)
        {
            log.LogError(ex, unparseableLogTemplate, body);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(taskId))
            return taskId;

        log.LogError(missingIdLogTemplate, body);
        return null;
    }

    /// <summary>
    /// GET with Bearer auth. Returns the body on HTTP success; logs a warning and
    /// returns null on failure so the poll loop can continue.
    /// </summary>
    public static async Task<string?> GetBearerBodyOrNullAsync(
        HttpClient http,
        string relativeUri,
        string apiKey,
        ILogger log,
        string failedLogTemplate,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return body;

        log.LogWarning(failedLogTemplate, resp.StatusCode, body);
        return null;
    }

    /// <summary>
    /// POST submit → parse task id → delay-then-tick poll. Vendor payload, id
    /// path, poll URI, and body interpretation stay at the call site.
    /// </summary>
    public static async Task<string?> SubmitAndPollAsync(
        HttpClient http,
        string submitUri,
        object payload,
        string apiKey,
        ILogger log,
        Action<string>? onProgress,
        string submittingMessage,
        string submitFailedLogTemplate,
        string submitFailedProgressPrefix,
        Func<JsonElement, string?> readTaskId,
        string unparseableLogTemplate,
        string missingIdLogTemplate,
        Func<string, CancellationToken, Task<(bool Done, string? AudioUrl)>> pollTick,
        string timeoutLogTemplate,
        string timeoutProgress,
        CancellationToken ct)
    {
        var body = await PostJsonOrNullAsync(
            http, submitUri, payload, apiKey, log, onProgress,
            submittingMessage, submitFailedLogTemplate, submitFailedProgressPrefix, ct)
            .ConfigureAwait(false);
        if (body is null)
            return null;

        var taskId = ReadTaskIdOrNull(
            body, log, readTaskId, unparseableLogTemplate, missingIdLogTemplate);
        if (taskId is null)
            return null;

        return await PollUntilTimeoutAsync(
            DefaultPollInterval, DefaultPollTimeout,
            pollCt => pollTick(taskId, pollCt),
            log, onProgress, timeoutLogTemplate, timeoutProgress, taskId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One poll tick: GET the status URI, then run the vendor body handler.
    /// HTTP failure (null body) keeps polling.
    /// </summary>
    public static async Task<(bool Done, string? AudioUrl)> TickGetAndHandleAsync(
        HttpClient http,
        string relativeUri,
        string apiKey,
        ILogger log,
        string pollFailedLogTemplate,
        Func<string, (bool Done, string? AudioUrl)> handleBody,
        CancellationToken ct)
    {
        var body = await GetBearerBodyOrNullAsync(
            http, relativeUri, apiKey, log, pollFailedLogTemplate, ct).ConfigureAwait(false);
        if (body is null)
            return (false, null);
        return handleBody(body);
    }

    /// <summary>
    /// Delay-then-tick poll until <paramref name="onTick"/> reports done or the
    /// timeout elapses. Tick return <c>Done=true</c> ends the loop (success URL
    /// or vendor failure).
    /// </summary>
    public static async Task<string?> PollUntilTimeoutAsync(
        TimeSpan interval,
        TimeSpan timeout,
        Func<CancellationToken, Task<(bool Done, string? AudioUrl)>> onTick,
        ILogger log,
        Action<string>? onProgress,
        string timeoutLogTemplate,
        string timeoutProgress,
        string taskId,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var (done, audioUrl) = await onTick(ct).ConfigureAwait(false);
            if (done)
                return audioUrl;
        }

        log.LogError(timeoutLogTemplate, timeout, taskId);
        onProgress?.Invoke(timeoutProgress);
        return null;
    }
}
