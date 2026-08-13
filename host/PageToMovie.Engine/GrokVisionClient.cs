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

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>xAI /responses vision transcription for book page images.</summary>
public sealed class GrokVisionClient : IVisionClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;

    private const string TranscribePrompt =
        "You are transcribing a children's / illustrated book page.\n\n" +
        "Task: extract ALL readable printed text on this page (title, body, dialogue).\n" +
        "Rules:\n" +
        "- Preserve verse line breaks when it looks like rhyme/poetry.\n" +
        "- Fix obvious OCR-style noise only if the letters on the page are clear; otherwise write what you see.\n" +
        "- Do NOT invent story, paraphrase, or add scene descriptions.\n" +
        "- If the page is illustration-only with no readable words, output exactly: (illustration only)\n" +
        "- Output plain text only — no markdown, no JSON, no preamble.\n";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, CharacterPageClassification Result)> ClassifyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, string Result)> TranscribeCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly IUserApiKeyProvider? _keyProvider;
    private readonly GenerationErrorLogger? _errorLogger;

    public GrokVisionClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokVisionClient> log,
        IUserApiKeyProvider? keyProvider = null,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _telemetry = telemetry;
        _keyProvider = keyProvider;
        _errorLogger = errorLogger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY"));

    public async Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "",
        CancellationToken ct = default)
    {
        FileInfo? fi = null;
        try { fi = new FileInfo(imagePath); }
        catch (Exception)
        {
            fi = null;
        }
        var cacheKey = $"{imagePath}|p{page}|m:{model}";
        if (fi is not null && fi.Exists &&
            TranscribeCache.TryGetValue(cacheKey, out var hit) &&
            hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
            hit.Length == fi.Length)
        {
            return hit.Result;
        }

        var dataUri = await FileToDataUriAsync(imagePath, ct);
        var payload = BuildVisionPayload(
            model,
            dataUri,
            detail: "high",
            text: $"Page {page} of the book.\n\n{TranscribePrompt}");

        var sw = Stopwatch.StartNew();
        string text;
        try
        {
            text = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                DoRequestAsync,
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_vision_transcribe_page", model, $"page={page}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ChatHttpStatusException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "transcribe_page",
                Endpoint = "responses",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }

        if (fi is not null && fi.Exists)
        {
            TranscribeCache[cacheKey] = (fi.LastWriteTimeUtc.Ticks, fi.Length, text);
        }
        return text;

        async Task<string> DoRequestAsync(int attemptNum)
        {
            using var resp = await SendJsonAsync(HttpMethod.Post, "responses", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = "vision",
                    Mode = "transcribe_page",
                    Endpoint = "responses",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Attempt = attemptNum,
                    Error = Trim(body, 500),
                    Ok = false,
                }, ct);
                throw ChatHttpStatusException.FromResponse(resp,
                    $"Grok vision HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");
            }

            using var doc = JsonDocument.Parse(body);
            var t = ExtractResponseText(doc.RootElement);
            t = CommonRegex.Replace(t.Trim(), @"^```(?:\w+)?\s*", "");
            t = CommonRegex.Replace(t, @"\s*```$", "").Trim();
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "transcribe_page",
                Endpoint = "responses",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Attempt = attemptNum,
                ResponseChars = t.Length,
                Ok = true,
            }, ct);
            return t;
        }
    }

    /// <summary>
    /// Ask Grok which cast members are visibly illustrated on a book page.
    /// Text-only / no-figure pages return PageKind = text_heavy and empty matches.
    /// </summary>
    public async Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "",
        CancellationToken ct = default)
    {
        if (cast.Count == 0)
            return new CharacterPageClassification { Page = page, PageKind = "unknown" };

        FileInfo? fi = null;
        try { fi = new FileInfo(imagePath); }
        catch (Exception)
        {
            fi = null;
        }
        var castKey = string.Join(";", cast.Select(c => $"{c.Key}:{c.DisplayName}:{c.Description}"));
        var cacheKey = $"{imagePath}|p{page}|m:{model}|c:{castKey}";
        if (fi is not null && fi.Exists &&
            ClassifyCache.TryGetValue(cacheKey, out var hit) &&
            hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
            hit.Length == fi.Length)
        {
            return hit.Result;
        }

        var castLines = cast.Select(c =>
        {
            var desc = string.IsNullOrWhiteSpace(c.Description)
                ? ""
                : $" — {Trim(c.Description.Replace('\n', ' '), 160)}";
            return $"- key={c.Key} | name={c.DisplayName}{desc}";
        });
        var prompt =
            "You are sorting illustrated children's book pages onto a film cast list.\n\n" +
            $"This is book page image #{page} (file may be a full page or crop).\n\n" +
            "Cast (use ONLY these keys):\n" +
            string.Join("\n", castLines) + "\n\n" +
            "Task: decide which cast members are VISIBLY ILLUSTRATED as figures in this image.\n" +
            "Rules:\n" +
            "- If the image is mostly printed story text, a word list, or blank with no character art: " +
            "page_kind=\"text_heavy\", characters=[].\n" +
            "- If it is a picture (cover, spot art, full-bleed scene) with figures: page_kind=\"illustration\".\n" +
            "- Mixed text+art with clear figures: page_kind=\"mixed\".\n" +
            "- Only match characters you can see drawn/painted in the art. Do not match from text names alone " +
            "if there is no figure for them.\n" +
            "- A hand, arm, foot, or silhouette alone is NOT enough to match an adult (Mom/Dad) — " +
            "require face and/or clear upper body of that person.\n" +
            "- If the only clear figure is the animal/hero dog, match only that cast key — do NOT invent " +
            "Mom/Dad just because a bed/home scene implies parents.\n" +
            "- Narrator / voice-only roles are never visual matches.\n" +
            "- confidence is 0..1 for how sure you are the figure is that cast member.\n" +
            "- primary_character_key = best single identity plate candidate (face-forward if possible), or null.\n\n" +
            "Respond with JSON ONLY (no markdown):\n" +
            "{\n" +
            "  \"page_kind\": \"illustration\"|\"text_heavy\"|\"mixed\"|\"unknown\",\n" +
            "  \"primary_character_key\": \"Character_...\"|null,\n" +
            "  \"characters\": [\n" +
            "    {\"key\":\"Character_...\",\"visible\":true,\"confidence\":0.0,\"notes\":\"short\"}\n" +
            "  ]\n" +
            "}\n";

        var dataUri = await FileToDataUriAsync(imagePath, ct);
        // low detail is enough for "who is on this page" and cheaper/faster for many pages
        var payload = BuildVisionPayload(model, dataUri, detail: "low", text: prompt);

        var sw = Stopwatch.StartNew();
        string text;
        try
        {
            text = await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                DoRequestAsync,
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_vision_classify_characters", model, $"page={page}; cast={cast.Count}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ChatHttpStatusException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "classify_characters",
                Endpoint = "responses",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }

        var res = ParseClassification(text, page, cast);
        if (fi is not null && fi.Exists)
        {
            ClassifyCache[cacheKey] = (fi.LastWriteTimeUtc.Ticks, fi.Length, res);
        }
        return res;

        async Task<string> DoRequestAsync(int attemptNum)
        {
            using var resp = await SendJsonAsync(HttpMethod.Post, "responses", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = "vision",
                    Mode = "classify_characters",
                    Endpoint = "responses",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Attempt = attemptNum,
                    Error = Trim(body, 500),
                    Ok = false,
                }, ct);
                throw ChatHttpStatusException.FromResponse(resp,
                    $"Grok vision classify HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");
            }

            using var doc = JsonDocument.Parse(body);
            var t = ExtractResponseText(doc.RootElement);
            t = CommonRegex.Replace(t.Trim(), @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            t = CommonRegex.Replace(t, @"\s*```$", "").Trim();
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Mode = "classify_characters",
                Endpoint = "responses",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Attempt = attemptNum,
                ResponseChars = t.Length,
                Ok = true,
            }, ct);
            return t;
        }
    }

    private static Dictionary<string, object?> BuildVisionPayload(
        string model,
        string dataUri,
        string detail,
        string text) =>
        new()
        {
            ["model"] = model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_image",
                            ["image_url"] = dataUri,
                            ["detail"] = detail,
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_text",
                            ["text"] = text,
                        },
                    },
                },
            },
        };

    private static CharacterPageClassification ParseClassification(
        string text,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast)
    {
        var result = new CharacterPageClassification { Page = page, PageKind = "unknown", Raw = text };
        var allowed = new HashSet<string>(cast.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

        try
        {
            // Extract first JSON object if model added preamble
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return result;
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.TryGetProperty("page_kind", out var pk))
                result.PageKind = (pk.GetString() ?? "unknown").Trim().ToLowerInvariant();
            if (root.TryGetProperty("primary_character_key", out var prim) &&
                prim.ValueKind == JsonValueKind.String &&
                prim.GetString() is { Length: > 0 } pkey &&
                allowed.Contains(pkey))
                result.PrimaryCharacterKey = pkey;

            if (root.TryGetProperty("characters", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var key = item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(key) || !allowed.Contains(key)) continue;
                    var visible = true;
                    if (item.TryGetProperty("visible", out var v))
                    {
                        visible = v.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => !string.Equals(v.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                            _ => true,
                        };
                    }
                    if (!visible) continue;
                    var conf = 0.5;
                    if (item.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cd))
                        conf = Math.Clamp(cd, 0, 1);
                    var notes = item.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
                    result.Matches.Add(new CharacterPageMatch
                    {
                        Key = key,
                        Confidence = conf,
                        Notes = notes,
                    });
                }
            }

            // Promote primary if listed with no matches
            if (result.Matches.Count == 0 &&
                result.PrimaryCharacterKey is { Length: > 0 } pk2 &&
                result.PageKind is not ("text_heavy" or "text"))
            {
                result.Matches.Add(new CharacterPageMatch
                {
                    Key = pk2,
                    Confidence = 0.55,
                    Notes = "primary_only",
                });
            }
        }
        catch (Exception)
        {
            result.PageKind = "parse_error";
        }

        // Never keep matches on hard text-only pages
        if (result.PageKind is "text_heavy" or "text")
        {
            result.Matches.Clear();
            result.PrimaryCharacterKey = null;
        }

        return result;
    }

    private static string ExtractResponseText(JsonElement result)
    {
        if (result.TryGetProperty("output_text", out var ot) &&
            ot.GetString() is { Length: > 0 } direct)
            return direct;

        if (result.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type is "output_text" or "text" &&
                            part.TryGetProperty("text", out var tx) &&
                            tx.GetString() is { Length: > 0 } s)
                            texts.Add(s);
                    }
                }
                else if (content.ValueKind == JsonValueKind.String &&
                         content.GetString() is { Length: > 0 } cs)
                {
                    texts.Add(cs);
                }
            }
            if (texts.Count > 0)
                return string.Join("\n", texts);
        }

        if (result.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var mc) &&
                mc.GetString() is { Length: > 0 } mcs)
                return mcs;
        }

        return result.GetRawText()[..Math.Min(500, result.GetRawText().Length)];
    }

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".ico"
    };

    /// <inheritdoc />
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("prompt required", nameof(prompt));
        var requested = (imagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var paths = requested
            .Where(p => File.Exists(p) && AllowedImageExtensions.Contains(Path.GetExtension(p)))
            .Take(8)
            .ToList();
        if (requested.Count > 0 && paths.Count == 0)
        {
            var sample = requested[0];
            var ext = Path.GetExtension(sample);
            var exists = File.Exists(sample);
            throw new InvalidOperationException(
                $"Vision call received {requested.Count} image path(s) but none were attachable " +
                $"(exists={exists}, ext='{ext}'). Use png/jpg/webp.");
        }

        var content = new List<object?>();
        foreach (var path in paths)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "input_image",
                ["image_url"] = await FileToDataUriAsync(path, ct),
                ["detail"] = string.IsNullOrWhiteSpace(detail) ? "low" : detail,
            });
        }
        content.Add(new Dictionary<string, object?>
        {
            ["type"] = "input_text",
            ["text"] = prompt,
        });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
        };

        var sw = Stopwatch.StartNew();
        var imageNames = paths.Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
        try
        {
            // Retries the whole request on 429/5xx or a network/timeout failure — previously a
            // transient blip failed the style gate / dialogue-verify / cast-on-image call outright,
            // unlike GrokChatClient/AnthropicChatClient/GeminiChatClient which all retry here.
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                DoRequestAsync,
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: LogTransientRetryAsync,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ChatHttpStatusException && ex is not ArgumentException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Endpoint = "responses",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                ReferenceImagePaths = imageNames,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }

        async Task<string> DoRequestAsync(int attemptNum)
        {
            using var resp = await SendJsonAsync(HttpMethod.Post, "responses", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = "vision",
                    Endpoint = "responses",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt.Length,
                    ReferenceImagePaths = imageNames,
                    ImageCount = paths.Count,
                    Attempt = attemptNum,
                    Error = Trim(body, 500),
                    Ok = false,
                }, ct);
                throw ChatHttpStatusException.FromResponse(resp,
                    $"Grok vision multi-image HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractResponseText(doc.RootElement);
            text = CommonRegex.Replace(text.Trim(), @"^```(?:\w+)?\s*", "", RegexOptions.IgnoreCase);
            text = CommonRegex.Replace(text, @"\s*```$", "").Trim();
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "vision",
                Endpoint = "responses",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt.Length,
                ReferenceImagePaths = imageNames,
                ImageCount = paths.Count,
                Attempt = attemptNum,
                ResponsePreview = text.Length > 2000 ? text[..2000] : text,
                ResponseChars = text.Length,
                Ok = true,
            }, ct);
            return text;
        }

        Task LogTransientRetryAsync(int attemptNum, Exception ex) =>
            _errorLogger.LogRetryAttemptAsync("grok_vision_completion", model,
                $"promptChars={prompt.Length}; images={paths.Count}", attemptNum, ex, ct);
    }

    private static async Task<string> FileToDataUriAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct = default) =>
        ApiKeyScope.Current
        ?? (_keyProvider is not null ? await _keyProvider.GetKeyAsync(null, "grok", ct).ConfigureAwait(false) : null)
        ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct)
    {
        // Per-request Bearer — never mutate shared DefaultRequestHeaders (multi-user race).
        using var req = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(payload),
        };

        var key = await ResolveApiKeyAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class CharacterClassifyHint
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class CharacterPageClassification
{
    public int Page { get; set; }
    /// <summary>illustration | text_heavy | mixed | unknown | parse_error</summary>
    public string PageKind { get; set; } = "unknown";
    public string? PrimaryCharacterKey { get; set; }
    public List<CharacterPageMatch> Matches { get; set; } = new();
    public string? Raw { get; set; }
}

public sealed class CharacterPageMatch
{
    public string Key { get; set; } = "";
    public double Confidence { get; set; }
    public string Notes { get; set; } = "";
}
