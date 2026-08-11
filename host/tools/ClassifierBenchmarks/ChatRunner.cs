using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassifierBenchmarks;

public sealed class ChatRunner : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _xaiApiKey;
    private readonly string? _claudeApiKey;
    private const string ContentKey = "content";
    private readonly string? _geminiApiKey;

    public ChatRunner(string? xaiApiKey, string? claudeApiKey, string? geminiApiKey = null)
    {
        _xaiApiKey = xaiApiKey;
        _claudeApiKey = claudeApiKey;
        _geminiApiKey = geminiApiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public static bool IsClaudeModel(string model) =>
        model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    public static bool IsGeminiModel(string model) =>
        model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("veo", StringComparison.OrdinalIgnoreCase);

    public async Task<string> CompleteAsync(string model, double temperature, string system, string user, CancellationToken ct = default) =>
        IsClaudeModel(model)
            ? await CompleteClaudeAsync(model, temperature, system, user, ct)
            : IsGeminiModel(model)
                ? await CompleteGeminiAsync(model, temperature, system, user, ct)
                : await CompleteXaiAsync(model, temperature, system, user, ct);

    private async Task<string> CompleteXaiAsync(string model, double temperature, string system, string user, CancellationToken ct)
    {
        var entry = PageToMovie.Core.Models.SupportedModelCatalog.Find(model);
        var envKey = entry is { RequiredEnvKeys: { Count: > 0 } keys } ? keys[0] : "XAI_API_KEY";
        var apiKey = Environment.GetEnvironmentVariable(envKey) ?? _xaiApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{envKey} required for model '{model}'");

        var targetUrl = entry is not null && !string.IsNullOrWhiteSpace(entry.ApiBase)
            ? $"{entry.ApiBase.TrimEnd('/')}/{(string.IsNullOrWhiteSpace(entry.EndpointPath) ? "chat/completions" : entry.EndpointPath).TrimStart('/')}"
            : "https://api.x.ai/v1/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", [ContentKey] = system },
                new Dictionary<string, object?> { ["role"] = "user", [ContentKey] = user },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"chat {(int)resp.StatusCode}: {Trim(text, 400)}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty(ContentKey).GetString() ?? "";
    }

    private async Task<string> CompleteClaudeAsync(string model, double temperature, string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_claudeApiKey))
            throw new InvalidOperationException($"CLAUDE_API_KEY required for model '{model}'");

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            // Anthropic requires max_tokens; classifier replies are short JSON payloads.
            ["max_tokens"] = 4096,
            ["system"] = system,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", [ContentKey] = user },
            },
        };
        // Newer Claude models (e.g. claude-sonnet-5) reject an explicit `temperature` field
        // ("temperature is deprecated for this model") — only send it when non-default.
        if (temperature > 0)
            body["temperature"] = Math.Clamp(temperature, 0, 1);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _claudeApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"claude chat {(int)resp.StatusCode}: {Trim(text, 400)}");
        using var doc = JsonDocument.Parse(text);
        var sb = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty(ContentKey).EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                block.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
        }
        return sb.ToString();
    }

    private async Task<string> CompleteGeminiAsync(string model, double temperature, string system, string user, CancellationToken ct)
    {
        var key = _geminiApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"GEMINI_API_KEY required for model '{model}'");

        var body = new Dictionary<string, object?>
        {
            ["system_instruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = system } },
            },
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = user } },
                },
            },
            ["generationConfig"] = new Dictionary<string, object?> { ["temperature"] = temperature },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key.Trim());

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"gemini chat {(int)resp.StatusCode}: {Trim(text, 400)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return "";
        var content = candidates[0].GetProperty(ContentKey);
        if (!content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0) return "";
        return parts[0].GetProperty("text").GetString() ?? "";
    }

    public static string Sha256Short(string s)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
