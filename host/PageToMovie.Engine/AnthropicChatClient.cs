using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Anthropic Messages API client — chat completion (planning / cast scrub / QA reasoning)
/// and multimodal completion (clip / frame review). Mirrors <see cref="GrokChatClient"/>'s
/// telemetry and auth conventions so callers and the api_calls.jsonl log see the same shape
/// regardless of provider.
/// </summary>
public sealed class AnthropicChatClient : IChatClient, IVisionClient
{
    public const string ApiBase = SupportedModelCatalog.AnthropicApiBase;
    public const string ApiVersion = "2023-06-01";
    private const string MessagesKey = "messages";
    private const string ThinkingKey = "thinking";
    private const string OutputConfigKey = "output_config";
    private const string TemperatureKey = "temperature";
    /// <summary>
    /// Resolves <c>max_tokens</c> from the catalog only. Missing model or maxOutputTokens → error.
    /// </summary>
    private static int ResolveMaxTokens(string model)
    {
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Chat)
            ?? throw new InvalidOperationException(
                $"Chat model '{model}' is not in models_catalog.json. Unknown models have no maxOutputTokens.");
        if (entry.MaxOutputTokens is not { } max || max <= 0)
            throw new InvalidOperationException(
                $"Chat model '{entry.Id}' has no maxOutputTokens in models_catalog.json. " +
                "Add the real API limit — do not invent a default.");
        return max;
    }

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly GenerationErrorLogger? _errorLogger;

    public AnthropicChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<AnthropicChatClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _ = opts; // reserved — no Anthropic-specific options today
        _http = http;
        _telemetry = telemetry;
        _errorLogger = errorLogger;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>Maps the provider-neutral <c>reasoningEffort</c> scale to Anthropic's
    /// <c>output_config.effort</c> values (confirmed live: low/medium/high/xhigh/max).</summary>
    private static string? MapReasoningEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "max" or "xhigh" => "max",
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
        var mappedEffort = MapReasoningEffort(reasoningEffort);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = ResolveMaxTokens(model),
            ["system"] = systemPrompt,
            [MessagesKey] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };
        if (mappedEffort is not null)
        {
            // Extended/adaptive thinking requires temperature to stay at its implicit default
            // (confirmed live: "`temperature` may only be set to 1 when thinking is enabled or
            // in adaptive mode") — omit it up front rather than round-tripping a 400 first.
            payload[ThinkingKey] = new Dictionary<string, object?> { ["type"] = "adaptive" };
            payload[OutputConfigKey] = new Dictionary<string, object?> { ["effort"] = mappedEffort };
        }
        else
        {
            payload[TemperatureKey] = temperature;
        }
        return await SendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "chat", MessagesKey, mode,
                systemPrompt, userPrompt,
                (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                attemptNum, ct),
            model, mode, ct).ConfigureAwait(false);
    }

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    /// <summary>Multi-image completion for clip auto-review (prev tail + current frames).</summary>
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default)
    {
        var content = new List<object?>();
        foreach (var path in imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && AllowedImageExtensions.Contains(Path.GetExtension(p))))
        {
            var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(path, ct).ConfigureAwait(false);
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = "base64",
                    ["media_type"] = mime,
                    ["data"] = b64,
                },
            });
        }
        content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = ResolveMaxTokens(model),
            [MessagesKey] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = content },
            },
        };
        return await SendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "vision", MessagesKey, "clip_auto_review",
                prompt, string.Join(", ", imagePaths.Select(Path.GetFileName)),
                prompt.Length, attemptNum, ct),
            model, "clip_auto_review", ct).ConfigureAwait(false);
    }

    private Task<string> SendWithTransientRetryAsync(
        Func<int, Task<string>> call,
        string model,
        string? mode,
        CancellationToken ct) =>
        AiRetryPolicy.ChatSendWithTransientRetryAsync(
            call, _errorLogger, "anthropic_chat_completion", model, mode, ct);


    /// <summary>
    /// Not implemented for Anthropic — book-page OCR / cast classification stay on
    /// <see cref="GrokVisionClient"/> for now. Fails loudly rather than silently
    /// returning a wrong answer if ever routed here.
    /// </summary>
    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "", CancellationToken ct = default) =>
        ChatClientHelpers.TranscribePageNotSupported("Anthropic");

    /// <inheritdoc cref="TranscribePageAsync"/>
    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "", CancellationToken ct = default) =>
        ChatClientHelpers.ClassifyCharactersNotSupported("Anthropic");

    private async Task<string> SendAsync(
        Dictionary<string, object?> payload,
        string model,
        string kind,
        string endpoint,
        string? mode,
        string? promptForLog,
        string? userPromptForLog,
        int promptChars,
        int attemptNum,
        CancellationToken ct)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        var sw = Stopwatch.StartNew();
        try
        {
            // Auth on a per-request message, not _http.DefaultRequestHeaders: this client is a
            // singleton shared by every concurrent classifier call, and mutating shared headers
            // per-call is a race (one call's key can leak into or clobber another's in flight).
            using var resp = await ProviderHttpHelpers.SendJsonAsync(
                _http, HttpMethod.Post, endpoint, payload, ct,
                req =>
                {
                    if (string.IsNullOrWhiteSpace(key)) return;
                    req.Headers.Add("x-api-key", key.Trim());
                    req.Headers.Add("anthropic-version", ApiVersion);
                }).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Some Anthropic models (e.g. claude-sonnet-5) have deprecated `temperature`
                // entirely and 400 if it's present at all. Retry once without it rather than
                // hardcoding a model-id list that would drift as Anthropic adds/retires models —
                // the API itself is telling us definitively when this applies.
                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && payload.ContainsKey(TemperatureKey)
                    && body.Contains(TemperatureKey, StringComparison.OrdinalIgnoreCase)
                    && body.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                {
                    var retryPayload = new Dictionary<string, object?>(payload);
                    retryPayload.Remove(TemperatureKey);
                    return await SendAsync(
                        retryPayload, model, kind, endpoint, mode,
                        promptForLog, userPromptForLog, promptChars, attemptNum, ct).ConfigureAwait(false);
                }

                // Older/smaller Claude models don't support adaptive thinking or output_config.effort
                // at all — self-heal the same way rather than maintaining a model-capability list.
                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && (payload.ContainsKey(ThinkingKey) || payload.ContainsKey(OutputConfigKey))
                    && (body.Contains(ThinkingKey, StringComparison.OrdinalIgnoreCase)
                        || body.Contains(OutputConfigKey, StringComparison.OrdinalIgnoreCase)))
                {
                    var retryPayload = new Dictionary<string, object?>(payload);
                    retryPayload.Remove(ThinkingKey);
                    retryPayload.Remove(OutputConfigKey);
                    retryPayload[TemperatureKey] = 0.2;
                    return await SendAsync(
                        retryPayload, model, kind, endpoint, mode,
                        promptForLog, userPromptForLog, promptChars, attemptNum, ct).ConfigureAwait(false);
                }
            }

            return await ChatClientHelpers.FinishChatResponseAsync(
                _telemetry, resp, body, kind, modeTag, endpoint, model,
                errorModel: null, sw.ElapsedMilliseconds,
                promptForLog, userPromptForLog, promptChars, attemptNum,
                ExtractMessageText, $"Anthropic {endpoint}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await ChatClientHelpers.LogChatExceptionAsync(
                _telemetry, ex, kind, modeTag, endpoint, model,
                sw.ElapsedMilliseconds, promptForLog, userPromptForLog, attemptNum, ct)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Test helper for extracting assistant text from a Messages API response.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    /// <summary>
    /// Anthropic Messages API response shape: <c>{ content: [{ type: "text", text: "..." }, ...] }</c>.
    /// Concatenates all text blocks (tool_use / other block types are skipped).
    /// </summary>
    private static string? ResolveApiKey()
    {
        var raw = Abstractions.ApiKeyScope.CurrentAnthropic
            ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.AnthropicApiKeyEnv);
        return ProviderApiKey.Clean(raw);
    }

    private static string ExtractMessageText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("type", out var t) &&
                    string.Equals(t.GetString(), "text", StringComparison.Ordinal) &&
                    block.TryGetProperty("text", out var txt))
                {
                    parts.Add(txt.GetString() ?? "");
                }
            }
            if (parts.Count > 0)
                return string.Join("\n", parts);
        }
        var raw = result.GetRawText();
        return ProviderHttpHelpers.Trim(raw, ChatClientHelpers.ResponsePreviewMax);
    }
}
