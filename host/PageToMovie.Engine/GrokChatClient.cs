using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>Dynamic OpenAI-compatible chat/completions client (xAI Grok, OpenAI, Gemini OpenAI endpoint, etc.).</summary>
public sealed class GrokChatClient : IChatClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;
    private const string ChatCompletionsPath = "chat/completions";

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly IUserApiKeyProvider? _keyProvider;
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
        _errorLogger = errorLogger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY"));

    /// <summary>Maps the provider-neutral <c>reasoningEffort</c> scale to OpenAI/xAI's
    /// <c>reasoning_effort</c> values (confirmed live: OpenAI accepts none/low/medium/high/xhigh;
    /// xAI accepts the same set without erroring).</summary>
    private static string CombineApiUrl(string apiBase, string endpointPath)
    {
        var slash = Path.AltDirectorySeparatorChar;
        return apiBase.TrimEnd(slash, Path.DirectorySeparatorChar)
               + slash
               + endpointPath.TrimStart(slash, Path.DirectorySeparatorChar);
    }

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
        var targetUrl = ResolveChatTargetUrl(entry);
        var mappedEffort = MapReasoningEffort(reasoningEffort);

        // OpenAI's o-series reasoning models (o1, o3-mini, o4-mini, ...) reject `temperature`
        // outright — "Unsupported parameter: 'temperature' is not supported with this model" —
        // they only run at the implicit default. This client is shared OpenAI-compatible plumbing
        // for xAI/OpenAI/Gemini-OpenAI-compat models, so omit the key only for that id pattern.
        var state = new ChatCompletionState
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Model = model,
            Temperature = temperature,
            Key = key,
            TargetUrl = targetUrl,
            MappedEffort = mappedEffort,
            IncludeTemperature = !IsOpenAiReasoningModel(model),
            IncludeReasoningEffort = mappedEffort is not null,
            Stopwatch = Stopwatch.StartNew(),
            ModeTag = modeTag,
            Ct = ct,
        };

        try
        {
            // Transient-retry wraps the existing param-shape self-heal loop. That inner loop
            // still strips unsupported temperature or reasoning_effort on HTTP 400. This wrapper
            // only retries the whole call on 429, 5xx, or a network/timeout failure, which
            // previously propagated to the caller immediately with zero retries.
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                attemptNum => DoChatRequestAsync(state, attemptNum),
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => LogChatTransientRetryAsync(state, attemptNum, ex),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await ChatClientHelpers.LogChatExceptionAsync(
                _telemetry, ex, "chat", state.ModeTag, ChatCompletionsPath, state.Model,
                state.Stopwatch.ElapsedMilliseconds, state.SystemPrompt, state.UserPrompt,
                attempt: null, ct);
            throw;
        }
    }

    private static string ResolveChatTargetUrl(SupportedModelEntry? entry) =>
        entry is not null && !string.IsNullOrWhiteSpace(entry.ApiBase)
            ? CombineApiUrl(entry.ApiBase, string.IsNullOrWhiteSpace(entry.EndpointPath) ? ChatCompletionsPath : entry.EndpointPath)
            : CombineApiUrl(ApiBase, ChatCompletionsPath);

    private static Dictionary<string, object?> BuildChatPayload(ChatCompletionState state)
    {
        var p = new Dictionary<string, object?>
        {
            ["model"] = state.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = state.SystemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = state.UserPrompt },
            },
        };
        if (state.IncludeTemperature)
            p["temperature"] = state.Temperature;
        if (state.IncludeReasoningEffort && state.MappedEffort is not null)
            p["reasoning_effort"] = state.MappedEffort;
        return p;
    }

    private async Task<string> DoChatRequestAsync(ChatCompletionState state, int attemptNum)
    {
        // Up to 3 attempts: models vary on whether they accept temperature, reasoning_effort,
        // both, or neither — rather than hardcoding a capability matrix per model id, self-heal
        // by stripping whichever param the API just told us is unsupported and retrying, same
        // pattern as the single-param retries elsewhere in this client and AnthropicChatClient.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var payload = BuildChatPayload(state);
            using var req = new HttpRequestMessage(HttpMethod.Post, state.TargetUrl)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(state.Key))
                ProviderHttpHelpers.ApplyBearer(req, state.Key);
            using var resp = await _http.SendAsync(req, state.Ct);
            var body = await resp.Content.ReadAsStringAsync(state.Ct);

            if (TryHealUnsupportedParam(state, resp, body, attempt))
                continue;

            return await ChatClientHelpers.FinishChatResponseAsync(
                _telemetry, resp, body, "chat", state.ModeTag, ChatCompletionsPath, state.Model,
                errorModel: null, state.Stopwatch.ElapsedMilliseconds,
                state.SystemPrompt, state.UserPrompt,
                (state.SystemPrompt?.Length ?? 0) + (state.UserPrompt?.Length ?? 0),
                attemptNum, ExtractMessageText, "Chat", state.Ct);
        }

        throw new InvalidOperationException("Chat parameter retry loop exhausted.");
    }

    private static bool TryHealUnsupportedParam(
        ChatCompletionState state, HttpResponseMessage resp, string body, int attempt)
    {
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode == 400 && attempt < 2)
        {
            // Error text varies by provider: OpenAI echoes the request field verbatim
            // ("reasoning_effort"), xAI reports it camelCase with no underscore
            // ("reasoningEffort", e.g. grok-4.20-reasoning: "does not support parameter
            // reasoningEffort") — match both spellings rather than the one we sent.
            if (state.IncludeReasoningEffort
                && (body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("reasoningeffort", StringComparison.OrdinalIgnoreCase)))
            {
                state.IncludeReasoningEffort = false;
                return true;
            }
            if (state.IncludeTemperature && body.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                state.IncludeTemperature = false;
                return true;
            }
        }
        return false;
    }

    private async Task LogChatTransientRetryAsync(ChatCompletionState state, int attemptNum, Exception ex)
    {
        if (_errorLogger is null) return;
        var httpStatus = ex is ChatHttpStatusException hse ? hse.StatusCode : (int?)null;
        await _errorLogger.LogAsync(new GenerationErrorRecord
        {
            Stage = "grok_chat_completion",
            Model = state.Model,
            ErrorType = httpStatus is not null ? "http_error" : "exception",
            ErrorMessage = ex.Message,
            HttpStatus = httpStatus,
            Attempt = attemptNum,
            Resolved = false, // this row is the failed attempt; a later attempt may still succeed
            RequestSummary = $"mode={state.ModeTag}; promptChars={(state.SystemPrompt?.Length ?? 0) + (state.UserPrompt?.Length ?? 0)}",
        }, state.Ct).ConfigureAwait(false);
    }

    private sealed class ChatCompletionState
    {
        public required string SystemPrompt;
        public required string UserPrompt;
        public required string Model;
        public required double Temperature;
        public string? Key;
        public required string TargetUrl;
        public string? MappedEffort;
        public bool IncludeTemperature;
        public bool IncludeReasoningEffort;
        public required Stopwatch Stopwatch;
        public string? ModeTag;
        public CancellationToken Ct;
    }

    public static Dictionary<string, object?> ParseJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No JSON object in model output");
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = CommonRegex.Replace(text, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            text = CommonRegex.Replace(text, @"\s*```\s*$", "");
        }
        // Prefer first balanced/parseable object — avoid matching braces in preamble like "{high}".
        var parsed = TryParseBalancedJsonObject(text);
        if (parsed is not null)
            return parsed;
        throw new InvalidOperationException("No JSON object in model output");
    }

    private static Dictionary<string, object?>? TryParseBalancedJsonObject(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                for (var j = text.Length - 1; j > i; j--)
                {
                    if (text[j] == '}' && TryParseObjectBlob(text[i..(j + 1)], out var dict))
                        return dict;
                }
            }
        }
        return null;
    }

    private static bool TryParseObjectBlob(string blob, out Dictionary<string, object?> dict)
    {
        dict = null!;
        try
        {
            using var doc = JsonDocument.Parse(blob);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                dict = JsonElementToDict(doc.RootElement);
                return true;
            }
        }
        catch
        {
            /* try next span */
        }
        return false;
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
        var fromChoices = TryExtractChoicesContent(result);
        if (fromChoices is not null)
            return fromChoices;
        if (result.TryGetProperty("output_text", out var ot) && ot.GetString() is { Length: > 0 } s)
            return s;
        var raw = result.GetRawText();
        return ProviderHttpHelpers.Trim(raw, ChatClientHelpers.ResponsePreviewMax);
    }

    private static string? TryExtractChoicesContent(JsonElement result)
    {
        if (!result.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            return null;

        var c0 = choices[0];
        if (c0.ValueKind != JsonValueKind.Object ||
            !c0.TryGetProperty("message", out var msg) ||
            msg.ValueKind != JsonValueKind.Object ||
            !msg.TryGetProperty("content", out var content))
            return null;

        return ExtractContentText(content);
    }

    private static string? ExtractContentText(JsonElement content)
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
        return null;
    }

    private static readonly Regex OpenAiReasoningModelRegex = new(@"^o\d", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

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
}
