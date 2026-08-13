using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Shared HTTP/JSON/error bits used by the concrete provider clients. Auth schemes and
/// provider-specific payload shapes stay at the call site — this is not a common client base.
/// </summary>
internal static class ProviderHttpHelpers
{
    public static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];

    /// <summary>
    /// Sets <see cref="HttpClient.BaseAddress"/> to <paramref name="apiBase"/> with a trailing
    /// slash when the client has no address yet (typical singleton registration).
    /// </summary>
    public static void EnsureTrailingSlashBaseAddress(HttpClient http, string apiBase)
    {
        if (http.BaseAddress is not null) return;
        http.BaseAddress = new Uri(apiBase.TrimEnd('/') + '/');
    }

    public static void ApplyBearer(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static void ApplyGoogleApiKey(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
    }

    /// <summary>
    /// Per-request send — never mutate shared <see cref="HttpClient.DefaultRequestHeaders"/>
    /// (concurrent jobs would race). <paramref name="applyAuth"/> runs before the send.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        HttpContent? content,
        CancellationToken ct,
        Action<HttpRequestMessage> applyAuth)
    {
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        applyAuth(req);
        return await http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient http,
        HttpMethod method,
        string uri,
        object payload,
        CancellationToken ct,
        Action<HttpRequestMessage> applyAuth) =>
        SendAsync(http, method, uri, JsonContent.Create(payload), ct, applyAuth);

    /// <summary>
    /// Reads the response body and throws <see cref="ChatHttpStatusException"/> on a non-success
    /// status, using the same <c>{prefix} HTTP {code}: {trimmed body}</c> message shape every
    /// provider client already used.
    /// </summary>
    public static async Task<string> ReadSuccessBodyAsync(
        HttpResponseMessage resp,
        CancellationToken ct,
        string errorPrefix,
        int trimLen = 400)
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ChatHttpStatusException.FromResponse(resp,
                $"{errorPrefix} HTTP {(int)resp.StatusCode}: {Trim(body, trimLen)}");
        return body;
    }

    public static string RequireJsonString(string body, string propertyName, string missingPrefix)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(propertyName, out var el) ||
            el.GetString() is not { Length: > 0 } value)
        {
            throw new InvalidOperationException($"{missingPrefix}: {Trim(body, 300)}");
        }
        return value;
    }

    public static async Task<string> ReadRequiredJsonStringAsync(
        HttpResponseMessage resp,
        CancellationToken ct,
        string propertyName,
        string errorPrefix,
        string missingPrefix,
        int errorTrim = 400)
    {
        var body = await ReadSuccessBodyAsync(resp, ct, errorPrefix, errorTrim).ConfigureAwait(false);
        return RequireJsonString(body, propertyName, missingPrefix);
    }

    /// <summary>
    /// Creates the destination directory, GETs <paramref name="url"/> (optionally configuring the
    /// request for provider auth), and copies the response stream to disk.
    /// </summary>
    public static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string destPath,
        CancellationToken ct,
        ILogger log,
        Action<HttpRequestMessage>? configureRequest = null,
        string logMessage = "Downloaded {Bytes} bytes → {Path}")
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        using var resp = configureRequest is null
            ? await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)
            : await SendConfiguredGetAsync(http, url, configureRequest, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        log.LogInformation(logMessage, new FileInfo(destPath).Length, destPath);
    }

    private static async Task<HttpResponseMessage> SendConfiguredGetAsync(
        HttpClient http, string url, Action<HttpRequestMessage> configureRequest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configureRequest(req);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
}
