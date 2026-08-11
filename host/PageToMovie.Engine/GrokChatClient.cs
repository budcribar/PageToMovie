using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>Dynamic OpenAI-compatible chat/completions client (xAI Grok, OpenAI, Gemini OpenAI endpoint, etc.).</summary>
public sealed class GrokChatClient : IChatClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly IUserApiKeyProvider? _keyProvider;
    private readonly ILogger<GrokChatClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public GrokChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokChatClient> log,
        IUserApiKeyProvider? keyProvider = null,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _telemetry = telemetry;
        _keyProvider = keyProvider;
        _log = log;
        _errorLogger = errorLogger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY"));

    /// <summary>Maps the provider-neutral <c>reasoningEffort</c> scale to OpenAI/xAI's
    /// <c>reasoning_effort</c> values (confirmed live: OpenAI accepts none/low/medium/high/xhigh;
    /// xAI accepts the same set without erroring).</summary>
    private static string? MapReasoningEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "max" or "xhigh" => "xhigh",
        "high" => "high",
        "medium" => "medium",
        "low" => "low",
        var other => other,
    };

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null)
    {
        var key = await ResolveApiKeyAsync(model, ct).ConfigureAwait(false);
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();

        var entry = PageToMovie.Core.Models.SupportedModelCatalog.Find(model);
        var targetUrl = entry is not null && !string.IsNullOrWhiteSpace(entry.ApiBase)
            ? $"{entry.ApiBase.TrimEnd('/')}/{(string.IsNullOrWhiteSpace(entry.EndpointPath) ? "chat/completions" : entry.EndpointPath).TrimStart('/')}"
            : "https://api.x.ai/v1/chat/completions";

        var mappedEffort = MapReasoningEffort(reasoningEffort);

        Dictionary<string, object?> BuildPayload(bool includeTemperature, bool includeReasoningEffort)
        {
            var p = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt },
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
                },
            };
            if (includeTemperature)
                p["temperature"] = temperature;
            if (includeReasoningEffort && mappedEffort is not null)
                p["reasoning_effort"] = mappedEffort;
            return p;
        }

        // OpenAI's o-series reasoning models (o1, o3-mini, o4-mini, ...) reject `temperature`
        // outright — "Unsupported parameter: 'temperature' is not supported with this model" —
        // they only run at the implicit default. This client is shared OpenAI-compatible plumbing
        // for xAI/OpenAI/Gemini-OpenAI-compat models, so omit the key only for that id pattern.
        var includeTemperature = !IsOpenAiReasoningModel(model);
        var includeReasoningEffort = mappedEffort is not null;

        var sw = Stopwatch.StartNew();
        try
        {
            // Transient-retry wraps the outside of the existing param-shape self-heal loop below
            // (400 due to unsupported temperature/reasoning_effort) — that loop is unmodified;
            // this only retries the whole call again on 429/5xx or a network/timeout failure,
            // which previously propagated to the caller immediately with zero retries.
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                DoRequestAsync,
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => LogTransientRetryAsync(attemptNum, ex),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "chat",
                Mode = modeTag,
                Endpoint = "chat/completions",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }

        async Task<string> DoRequestAsync(int attemptNum)
        {
            // Up to 3 attempts: models vary on whether they accept temperature, reasoning_effort,
            // both, or neither — rather than hardcoding a capability matrix per model id, self-heal
            // by stripping whichever param the API just told us is unsupported and retrying, same
            // pattern as the single-param retries elsewhere in this client and AnthropicChatClient.
            for (var attempt = 0; ; attempt++)
            {
                var payload = BuildPayload(includeTemperature, includeReasoningEffort);
                using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Content = JsonContent.Create(payload),
                };
                if (!string.IsNullOrWhiteSpace(key))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode == 400 && attempt < 2)
                {
                    // Error text varies by provider: OpenAI echoes the request field verbatim
                    // ("reasoning_effort"), xAI reports it camelCase with no underscore
                    // ("reasoningEffort", e.g. grok-4.20-reasoning: "does not support parameter
                    // reasoningEffort") — match both spellings rather than the one we sent.
                    if (includeReasoningEffort
                        && (body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                            || body.Contains("reasoningeffort", StringComparison.OrdinalIgnoreCase)))
                    {
                        includeReasoningEffort = false;
                        continue;
                    }
                    if (includeTemperature && body.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        includeTemperature = false;
                        continue;
                    }
                }

                return await FinishAsync(resp, body, attemptNum);
            }
        }

        async Task LogTransientRetryAsync(int attemptNum, Exception ex)
        {
            if (_errorLogger is null) return;
            var httpStatus = ex is ChatHttpStatusException hse ? hse.StatusCode : (int?)null;
            await _errorLogger.LogAsync(new GenerationErrorRecord
            {
                Stage = "grok_chat_completion",
                Model = model,
                ErrorType = httpStatus is not null ? "http_error" : "exception",
                ErrorMessage = ex.Message,
                HttpStatus = httpStatus,
                Attempt = attemptNum,
                Resolved = false, // this row is the failed attempt; a later attempt may still succeed
                RequestSummary = $"mode={modeTag}; promptChars={(systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0)}",
            }, ct).ConfigureAwait(false);
        }

        async Task<string> FinishAsync(HttpResponseMessage resp, string body, int attemptNum)
        {
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = "chat",
                    Mode = modeTag,
                    Endpoint = "chat/completions",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    PromptChars = (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                    Attempt = attemptNum,
                    Error = Trim(body, 800),
                    Ok = false,
                }, ct);
                throw ChatHttpStatusException.FromResponse(resp,
                    $"Chat HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractMessageText(doc.RootElement);
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "chat",
                Mode = modeTag,
                Endpoint = "chat/completions",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                PromptChars = (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                Attempt = attemptNum,
                ResponsePreview = text.Length > 2000 ? text[..2000] : text,
                ResponseChars = text.Length,
                Ok = true,
            }, ct);
            return text;
        }
    }

    public static Dictionary<string, object?> ParseJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No JSON object in model output");
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```\s*$", "");
        }
        // Prefer first balanced/parseable object — avoid matching braces in preamble like "{high}".
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            for (var j = text.Length - 1; j > i; j--)
            {
                if (text[j] != '}') continue;
                var blob = text[i..(j + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(blob);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        return JsonElementToDict(doc.RootElement);
                }
                catch
                {
                    /* try next span */
                }
            }
        }
        throw new InvalidOperationException("No JSON object in model output");
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject())
            d[p.Name] = JsonElementToObject(p.Value);
        return d;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => JsonElementToDict(el),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    /// <summary>Test/helper for extracting assistant text from chat completion JSON.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    private static string ExtractMessageText(JsonElement result)
    {
        if (result.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.ValueKind == JsonValueKind.Object &&
                c0.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? "";
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String)
                            parts.Add(c.GetString() ?? "");
                        else if (c.TryGetProperty("text", out var t))
                            parts.Add(t.GetString() ?? "");
                    }
                    return string.Join("\n", parts);
                }
            }
        }
        if (result.TryGetProperty("output_text", out var ot) && ot.GetString() is { Length: > 0 } s)
            return s;
        var raw = result.GetRawText();
        return raw.Length <= 2000 ? raw : raw[..2000];
    }

    private static readonly Regex OpenAiReasoningModelRegex = new(@"^o\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for OpenAI o-series reasoning model ids (o1, o1-mini, o3-mini, o4-mini, ...).</summary>
    private static bool IsOpenAiReasoningModel(string model) =>
        !string.IsNullOrWhiteSpace(model) && OpenAiReasoningModelRegex.IsMatch(model.Trim());

    private async Task<string?> ResolveApiKeyAsync(string? model = null, CancellationToken ct = default)
    {
        var envKey = "XAI_API_KEY";
        if (!string.IsNullOrWhiteSpace(model))
        {
            var entry = PageToMovie.Core.Models.SupportedModelCatalog.Find(model);
            if (entry is { RequiredEnvKeys: { Count: > 0 } keys })
            {
                envKey = keys[0];
            }
        }

        return ApiKeyScope.Current ??
            (_keyProvider is not null ? await _keyProvider.GetKeyAsync(UserApiCallScope.UserId, "grok", ct).ConfigureAwait(false) : null) ??
            Environment.GetEnvironmentVariable(envKey);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
