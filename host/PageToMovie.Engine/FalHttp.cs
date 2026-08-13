using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Shared Fal.ai HTTP POST + Key-auth + JSON parse used by the Fal audio/image/video/lip-sync
/// clients. Failure policy (return null vs throw) stays at the call site via the two entry points.
/// </summary>
internal static class FalHttp
{
    internal const string FailedSubmitLogTemplate =
        "Fal.ai {Operation} failed HTTP {Status} ({Elapsed}ms): {Body}";

    private static void ApplyKey(HttpRequestMessage req, string apiKey) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);

    /// <summary>GET with Fal Key auth; returns status + body (status poll / result fetch).</summary>
    public static async Task<FalRawResponse> GetAsync(
        HttpClient http, string path, string apiKey, CancellationToken ct)
    {
        using var resp = await ProviderHttpHelpers.SendAsync(
            http, HttpMethod.Get, path, content: null, ct, req => ApplyKey(req, apiKey)).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new FalRawResponse(resp.StatusCode, resp.IsSuccessStatusCode, body);
    }

    /// <summary>Fal file objects are <c>{ "url": "https://..." }</c> nested under a named property.</summary>
    public static string? TryGetObjectUrl(JsonElement root, string objectProperty) =>
        root.TryGetProperty(objectProperty, out var obj) &&
        obj.TryGetProperty("url", out var urlEl) &&
        urlEl.GetString() is { Length: > 0 } url
            ? url
            : null;

    /// <summary>
    /// POST JSON with Fal Key auth. Logs HTTP failures and returns null (audio / lip-sync).
    /// </summary>
    public static Task<FalJsonResponse?> TryPostJsonAsync(
        HttpClient http,
        ILogger log,
        string path,
        string apiKey,
        Dictionary<string, object?> payload,
        string operation,
        CancellationToken ct) =>
        PostJsonCoreAsync(http, log, path, apiKey, payload, operation, onHttpError: null, ct);

    /// <summary>
    /// POST JSON with Fal Key auth. Logs HTTP failures then throws
    /// <c>{throwPrefix} {status}: {body}</c> (image / video).
    /// </summary>
    public static async Task<FalJsonResponse> PostJsonOrThrowAsync(
        HttpClient http,
        ILogger log,
        string path,
        string apiKey,
        Dictionary<string, object?> payload,
        string operation,
        string throwPrefix,
        CancellationToken ct)
    {
        var posted = await PostJsonCoreAsync(
            http, log, path, apiKey, payload, operation,
            (status, body) => new InvalidOperationException($"{throwPrefix} {status}: {body}"),
            ct).ConfigureAwait(false);
        return posted!;
    }

    private static async Task<FalJsonResponse?> PostJsonCoreAsync(
        HttpClient http,
        ILogger log,
        string path,
        string apiKey,
        Dictionary<string, object?> payload,
        string operation,
        Func<HttpStatusCode, string, Exception>? onHttpError,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var resp = await ProviderHttpHelpers.SendJsonAsync(
            http, HttpMethod.Post, path, payload, ct, req => ApplyKey(req, apiKey)).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var elapsedMs = sw.ElapsedMilliseconds;

        if (resp.IsSuccessStatusCode)
            return new FalJsonResponse(JsonDocument.Parse(body), body, elapsedMs);

        log.LogError(FailedSubmitLogTemplate, operation, resp.StatusCode, elapsedMs, body);
        if (onHttpError is not null)
            throw onHttpError(resp.StatusCode, body);
        return null;
    }
}

/// <summary>Successful Fal JSON POST: parsed document plus the raw body (callers log it on shape errors).</summary>
internal sealed class FalJsonResponse : IDisposable
{
    public FalJsonResponse(JsonDocument document, string body, long elapsedMs)
    {
        Document = document;
        Body = body;
        ElapsedMs = elapsedMs;
    }

    public JsonDocument Document { get; }
    public string Body { get; }
    public long ElapsedMs { get; }
    public JsonElement Root => Document.RootElement;

    public void Dispose() => Document.Dispose();
}

internal readonly record struct FalRawResponse(HttpStatusCode StatusCode, bool IsSuccess, string Body);
