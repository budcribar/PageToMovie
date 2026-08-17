using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Google Gemini <c>generateContent</c> client — chat completion (planning / cast scrub / QA
/// reasoning) and multimodal completion (clip / frame review). Response-shape notes below are
/// built from Gemini's public documented format; verify against a live call before relying on
/// this in production, same as any provider added without an account to test against.
/// </summary>
public sealed class GeminiChatClient : ChatProviderWithoutBookVision, IChatClient, IGeminiVideoAnalysisClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private const string PartsKey = "parts";
    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly GenerationErrorLogger? _errorLogger;

    public GeminiChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiChatClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _ = opts; // reserved — no Gemini-specific options today
        _http = http;
        _telemetry = telemetry;
        _errorLogger = errorLogger;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
        if (_http.Timeout < TimeSpan.FromSeconds(180))
            _http.Timeout = TimeSpan.FromSeconds(180);
    }

    protected override string UnsupportedVisionProvider => "Gemini";

    public override bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static readonly AsyncLocal<string?> _lastResolvedModel = new();

    /// <summary>
    /// The model id that actually served the most recently completed call on this async flow.
    /// Callers that need to report or attribute results by model (benchmarks, telemetry) should
    /// check this rather than assume the requested id is what generated the response.
    /// </summary>
    public static string? LastResolvedModel => _lastResolvedModel.Value;

    /// <summary>Maps the provider-neutral <c>reasoningEffort</c> scale to Gemini's
    /// <c>thinkingConfig.thinkingLevel</c>. Confirmed live: "high" is Gemini's ceiling — "max"/
    /// "xhigh"/"maximum" all 400. Falls through to "high" for anything at or above it.</summary>
    private static string? MapThinkingLevel(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "low" => "low",
        "medium" => "medium",
        _ => "high",
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
        var generationConfig = new Dictionary<string, object?> { ["temperature"] = temperature };
        var thinkingLevel = MapThinkingLevel(reasoningEffort);
        if (thinkingLevel is not null)
            generationConfig["thinkingConfig"] = new Dictionary<string, object?> { ["thinkingLevel"] = thinkingLevel };

        var payload = new Dictionary<string, object?>
        {
            ["system_instruction"] = new Dictionary<string, object?>
            {
                [PartsKey] = new object[] { new Dictionary<string, object?> { ["text"] = systemPrompt } },
            },
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    [PartsKey] = new object[] { new Dictionary<string, object?> { ["text"] = userPrompt } },
                },
            },
            ["generationConfig"] = generationConfig,
        };
        return await AiRetryPolicy.ChatSendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "chat", mode,
                systemPrompt, userPrompt,
                (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                attemptNum, ct),
            _errorLogger, "gemini_chat_completion", model, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Multi-image completion for clip auto-review (prev tail + current frames).</summary>
    public override async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        double temperature = 0.0,
        CancellationToken ct = default)
    {
        var parts = new List<object?>();
        foreach (var path in imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(path, ct, allowVideo: true).ConfigureAwait(false);
            parts.Add(new Dictionary<string, object?>
            {
                ["inline_data"] = new Dictionary<string, object?> { ["mime_type"] = mime, ["data"] = b64 },
            });
        }
        parts.Add(new Dictionary<string, object?> { ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", [PartsKey] = parts },
            },
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["temperature"] = temperature,
            },
        };
        return await AiRetryPolicy.ChatSendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "vision", "clip_auto_review",
                prompt, ChatClientHelpers.ImageNamesForLog(imagePaths),
                prompt.Length, attemptNum, ct),
            _errorLogger, "gemini_chat_completion", model, "clip_auto_review", ct).ConfigureAwait(false);
    }

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Gemini: model is required. Open Settings and choose a model (no silent default).");
        var trimmed = model.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["models/".Length..];
        // Strip provider prefix only — never rewrite to a different model id.
        return trimmed;
    }

    private async Task<string> SendAsync(
        Dictionary<string, object?> payload,
        string model,
        string kind,
        string? mode,
        string? promptForLog,
        string? userPromptForLog,
        int promptChars,
        int attemptNum,
        CancellationToken ct)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        var targetModel = NormalizeModelName(model);
        var endpoint = $"models/{Uri.EscapeDataString(targetModel)}:generateContent";
        var sw = Stopwatch.StartNew();
        try
        {
            // Auth on a per-request message, not _http.DefaultRequestHeaders: this client is a
            // singleton shared by every concurrent classifier call, and mutating shared headers
            // per-call is a race (one call's key can leak into or clobber another's in flight).
            using var resp = await ProviderHttpHelpers.SendJsonAsync(
                _http, HttpMethod.Post, endpoint, payload, ct,
                req => ProviderHttpHelpers.ApplyGoogleApiKey(req, key)).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Not every Gemini model supports thinkingConfig (confirmed live: gemini-2.5-flash
            // 400s with "Thinking level is not supported for this model"). Self-heal by
            // stripping it and retrying rather than maintaining a model-capability list.
            if (!resp.IsSuccessStatusCode
                && resp.StatusCode == System.Net.HttpStatusCode.BadRequest
                && payload.TryGetValue("generationConfig", out var gc)
                && gc is Dictionary<string, object?> genConfig
                && genConfig.ContainsKey("thinkingConfig")
                && body.Contains("hinking", StringComparison.OrdinalIgnoreCase))
            {
                var retryPayload = new Dictionary<string, object?>(payload);
                var retryGenConfig = new Dictionary<string, object?>(genConfig);
                retryGenConfig.Remove("thinkingConfig");
                retryPayload["generationConfig"] = retryGenConfig;
                return await SendAsync(retryPayload, model, kind, mode, promptForLog, userPromptForLog, promptChars, attemptNum, ct).ConfigureAwait(false);
            }

            var text = await ChatClientHelpers.FinishChatResponseAsync(
                _telemetry, resp, body,
                ChatRec(resp.IsSuccessStatusCode ? model : targetModel),
                ExtractMessageText, $"Gemini {endpoint}", ct).ConfigureAwait(false);
            _lastResolvedModel.Value = targetModel;
            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await ChatClientHelpers.LogChatExceptionAsync(
                _telemetry, ex, ChatRec(model), ct)
                .ConfigureAwait(false);
            throw;
        }

        ApiCallTelemetry ChatRec(string modelId) => new()
        {
            Kind = kind,
            Mode = modeTag,
            Endpoint = endpoint,
            Model = modelId,
            DurationMs = sw.ElapsedMilliseconds,
            SystemPrompt = promptForLog,
            UserPrompt = userPromptForLog,
            PromptChars = promptChars,
            Attempt = attemptNum,
        };
    }

    private static string? ResolveApiKey() =>
        Abstractions.ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);

    /// <summary>Test helper for extracting model text from a generateContent response.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    /// <summary>
    /// Gemini response shape: <c>{ candidates: [{ content: { parts: [{ text: "..." }] } }] }</c>.
    /// Concatenates all text parts of the first candidate.
    /// </summary>
    private static string ExtractMessageText(JsonElement result)
    {
        var text = TryExtractCandidatePartsText(result);
        if (text is not null)
            return text;
        var raw = result.GetRawText();
        return ProviderHttpHelpers.Trim(raw, ChatClientHelpers.ResponsePreviewMax);
    }

    private static string? TryExtractCandidatePartsText(JsonElement result)
    {
        if (!result.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return null;

        var c0 = candidates[0];
        if (c0.ValueKind != JsonValueKind.Object ||
            !c0.TryGetProperty("content", out var content) ||
            !content.TryGetProperty(PartsKey, out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return null;

        return JoinPartTexts(parts);
    }

    private static string? JoinPartTexts(JsonElement parts)
    {
        var texts = new List<string>();
        foreach (var p in parts.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t))
                texts.Add(t.GetString() ?? "");
        }
        if (texts.Count > 0)
            return string.Join("\n", texts);
        return null;
    }
}
