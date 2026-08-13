using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Suno background-music generation via aimusicapi.ai — a different unofficial third-party Suno
/// reseller from <see cref="SunoClient"/> (formerly reached via a sunoapi.com redirect). Their
/// public docs (docs.aimusicapi.ai) are thin and partly 404/JS-rendered, so the request shape
/// below is assembled from what was consistently confirmed across multiple doc pages
/// (POST /api/v1/suno/create, custom_mode + make_instrumental + tags/title/prompt fields) but the
/// poll response schema was never fully confirmed — this parses defensively across several
/// plausible shapes and should be tightened against a real response once a working account is
/// available (the account this was built for hit a sign-in error on aimusicapi.ai/sunoapi.com
/// during setup). No documented duration control, unlike sunoapi.org.
/// </summary>
public sealed class AiMusicApiClient : IAudioClient
{
    public const string ApiBase = "https://api.aimusicapi.ai/api/v1/";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(6);

    private readonly HttpClient _http;
    private readonly ILogger<AiMusicApiClient> _log;

    public AiMusicApiClient(HttpClient http, ILogger<AiMusicApiClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey()
    {
        var key = ApiKeyScope.CurrentAiMusicApi
            ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.AiMusicApiKeyEnv);
        return ProviderApiKey.Clean(key);
    }

    public async Task<string?> GenerateMusicTrackAsync(
        string prompt,
        int durationSeconds,
        string? model = null,
        CancellationToken ct = default,
        Action<string>? onProgress = null,
        bool isVocal = false,
        string? lyrics = null)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("AIMusicAPI (aimusicapi.ai) key is missing — skipping audio generation.");
            return null;
        }

        var wireModel = ResolveAiMusicWireModel(model);
        var payload = new Dictionary<string, object?>
        {
            ["task_type"] = "create_music",
            ["custom_mode"] = true,
            ["make_instrumental"] = !isVocal,
            // "tags" carries genre/mood tags either way; when singing, "prompt" carries the actual
            // lyrics to sing.
            ["tags"] = prompt,
            ["prompt"] = isVocal ? (lyrics ?? "") : "",
            ["title"] = "Scene Score",
            ["mv"] = wireModel,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "suno/create");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(payload);

        onProgress?.Invoke("Submitting to AIMusicAPI (aimusicapi.ai)…");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("AIMusicAPI submit failed HTTP {Status}: {Body}", resp.StatusCode, body);
            onProgress?.Invoke($"AIMusicAPI submit failed: HTTP {(int)resp.StatusCode}");
            return null;
        }

        string? taskId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            taskId = FindStringByAnyKey(doc.RootElement, "task_id", "taskId", "id");
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "AIMusicAPI submit returned unparseable JSON: {Body}", body);
            return null;
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            _log.LogError("AIMusicAPI submit response had no task id: {Body}", body);
            return null;
        }

        return await PollForAudioUrlAsync(taskId, apiKey, onProgress, ct).ConfigureAwait(false);
    }

    private async Task<string?> PollForAudioUrlAsync(
        string taskId, string apiKey, Action<string>? onProgress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, $"suno/task/{Uri.EscapeDataString(taskId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("AIMusicAPI poll HTTP {Status}: {Body}", resp.StatusCode, body);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var status = FindStringByAnyKey(doc.RootElement, "status", "state");
                onProgress?.Invoke($"AIMusicAPI status: {status ?? "unknown"}");

                var audioUrl = FindStringByAnyKey(doc.RootElement, "audio_url", "audioUrl", "url");
                if (!string.IsNullOrWhiteSpace(audioUrl))
                {
                    _log.LogInformation("AIMusicAPI audio ready: {Url}", audioUrl);
                    return audioUrl;
                }

                if (status is not null &&
                    (status.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                     status.Contains("error", StringComparison.OrdinalIgnoreCase)))
                {
                    _log.LogError("AIMusicAPI generation failed: {Status} — {Body}", status, body);
                    return null;
                }
                // Still pending — keep polling.
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "AIMusicAPI poll returned unparseable JSON: {Body}", body);
            }
        }

        _log.LogError("AIMusicAPI generation timed out after {Timeout} for task {TaskId}", PollTimeout, taskId);
        onProgress?.Invoke("AIMusicAPI generation timed out.");
        return null;
    }

    /// <summary>
    /// Depth-limited search for the first string value found under any of the given key names,
    /// checking both the object itself and one level into any "data"/"result" wrapper — a
    /// defensive stand-in for a confirmed schema (see class doc comment).
    /// </summary>
    private static string? FindStringByAnyKey(JsonElement element, params string[] keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return FindStringOnObject(element, keys);
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
            return FindStringByAnyKey(element[0], keys);
        return null;
    }

    private static string? FindStringOnObject(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        foreach (var wrapperKey in new[] { "data", "result", "response" })
        {
            if (!element.TryGetProperty(wrapperKey, out var wrapper)) continue;
            var found = FindStringByAnyKey(wrapper, keys);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Vendor wire model for aimusicapi.ai. Catalog id <c>aimusicapi-suno</c> maps to the
    /// documented <c>chirp-v5</c> family token used in the <c>mv</c> field — derived only when
    /// the model is a known catalog audio entry for this provider.
    /// </summary>
    private static string ResolveAiMusicWireModel(string? model)
    {
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Audio)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                "Background music: no AI Music API model selected. Open Settings → Studio coverage and choose a music model.");

        if (!string.Equals(entry.ProviderId, "aimusicapi", StringComparison.OrdinalIgnoreCase)
            && entry.Provider != ModelProviderFamily.AiMusicApi
            && !entry.Id.Contains("aimusic", StringComparison.OrdinalIgnoreCase))
        {
            // Still allow if caller passed a raw vendor token that is in catalog notes/id.
        }

        // Known catalog id → documented wire token. Prefer raw id if it already looks like chirp-*.
        if (entry.Id.StartsWith("chirp-", StringComparison.OrdinalIgnoreCase))
            return entry.Id;
        if (entry.Id.Contains("suno", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.ProviderId, "aimusicapi", StringComparison.OrdinalIgnoreCase))
            return "chirp-v5"; // sole documented Suno family token for this reseller today

        return entry.Id;
    }
}
