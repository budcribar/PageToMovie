using System.Diagnostics;
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
    private const string KindVision = "vision";
    private const string EndpointResponses = "responses";
    private const string ContentKey = "content";

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
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
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
            // Invalid path — skip cache and continue without FileInfo.
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
                Kind = KindVision,
                Mode = "transcribe_page",
                Endpoint = EndpointResponses,
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

        Task<string> DoRequestAsync(int attemptNum) =>
            SendVisionRequestAsync(
                payload, "transcribe_page", model, attemptNum, sw,
                "Grok vision HTTP", @"^```(?:\w+)?\s*", RegexOptions.None, ct);
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
            // Invalid path — skip cache and continue without FileInfo.
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
                Kind = KindVision,
                Mode = "classify_characters",
                Endpoint = EndpointResponses,
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

        Task<string> DoRequestAsync(int attemptNum) =>
            SendVisionRequestAsync(
                payload, "classify_characters", model, attemptNum, sw,
                "Grok vision classify HTTP", @"^```(?:json)?\s*", RegexOptions.IgnoreCase, ct);
    }

    private async Task<string> SendVisionRequestAsync(
        object payload,
        string mode,
        string model,
        int attemptNum,
        Stopwatch sw,
        string httpErrorPrefix,
        string fenceOpenPattern,
        RegexOptions fenceOptions,
        CancellationToken ct)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, EndpointResponses, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            await _telemetry.LogApiCallAsync(
                VisionHttpTelemetry(mode, model, (int)resp.StatusCode, sw.ElapsedMilliseconds, attemptNum,
                    error: Trim(body, 500), ok: false), ct);
            throw ChatHttpStatusException.FromResponse(resp,
                $"{httpErrorPrefix} {(int)resp.StatusCode}: {Trim(body, 500)}");
        }

        using var doc = JsonDocument.Parse(body);
        var text = StripMarkdownFence(ExtractResponseText(doc.RootElement), fenceOpenPattern, fenceOptions);
        await _telemetry.LogApiCallAsync(
            VisionHttpTelemetry(mode, model, (int)resp.StatusCode, sw.ElapsedMilliseconds, attemptNum,
                responseChars: text.Length, ok: true), ct);
        return text;
    }

    private static ApiCallTelemetry VisionHttpTelemetry(
        string mode, string model, int httpStatus, long durationMs, int attempt,
        string? error = null, int? responseChars = null, bool ok = false) =>
        new()
        {
            Kind = KindVision,
            Mode = mode,
            Endpoint = EndpointResponses,
            Model = model,
            HttpStatus = httpStatus,
            DurationMs = durationMs,
            Attempt = attempt,
            Error = error,
            ResponseChars = responseChars,
            Ok = ok,
        };

    private static string StripMarkdownFence(string text, string openPattern, RegexOptions options)
    {
        text = CommonRegex.Replace(text.Trim(), openPattern, "", options);
        return CommonRegex.Replace(text, @"\s*```$", "").Trim();
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
                    [ContentKey] = new object[]
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
            ApplyClassificationJson(result, text, allowed);
        }
        catch (Exception)
        {
            result.PageKind = "parse_error";
        }

        if (result.PageKind is "text_heavy" or "text")
        {
            result.Matches.Clear();
            result.PrimaryCharacterKey = null;
        }

        return result;
    }

    private static void ApplyClassificationJson(
        CharacterPageClassification result,
        string text,
        HashSet<string> allowed)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return;

        using var doc = JsonDocument.Parse(text[start..(end + 1)]);
        var root = doc.RootElement;
        if (root.TryGetProperty("page_kind", out var pk))
            result.PageKind = (pk.GetString() ?? "unknown").Trim().ToLowerInvariant();
        TryReadPrimaryKey(result, root, allowed);

        if (root.TryGetProperty("characters", out var arr) && arr.ValueKind == JsonValueKind.Array)
            AddCharacterMatches(result, arr, allowed);

        PromotePrimaryIfNoMatches(result);
    }

    private static void TryReadPrimaryKey(
        CharacterPageClassification result,
        JsonElement root,
        HashSet<string> allowed)
    {
        if (root.TryGetProperty("primary_character_key", out var prim) &&
            prim.ValueKind == JsonValueKind.String &&
            prim.GetString() is { Length: > 0 } pkey &&
            allowed.Contains(pkey))
            result.PrimaryCharacterKey = pkey;
    }

    private static void AddCharacterMatches(
        CharacterPageClassification result,
        JsonElement arr,
        HashSet<string> allowed)
    {
        foreach (var item in arr.EnumerateArray())
            TryAddCharacterMatch(result, item, allowed);
    }

    private static void TryAddCharacterMatch(
        CharacterPageClassification result,
        JsonElement item,
        HashSet<string> allowed)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        var key = item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key) || !allowed.Contains(key))
            return;
        var visible = true;
        if (item.TryGetProperty("visible", out var v))
            visible = ReadVisible(v);
        if (!visible)
            return;
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

    private static bool ReadVisible(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => !string.Equals(v.GetString(), "false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static void PromotePrimaryIfNoMatches(CharacterPageClassification result)
    {
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

    private static string ExtractResponseText(JsonElement result)
    {
        if (TryGetOutputText(result, out var direct))
            return direct;
        if (TryCollectOutputArrayTexts(result, out var texts))
            return string.Join("\n", texts);
        if (TryFromChatChoices(result, out var fromChoices))
            return fromChoices;
        var raw = result.GetRawText();
        return raw[..Math.Min(500, raw.Length)];
    }

    private static bool TryGetOutputText(JsonElement result, out string text)
    {
        if (result.TryGetProperty("output_text", out var ot) &&
            ot.GetString() is { Length: > 0 } direct)
        {
            text = direct;
            return true;
        }

        text = "";
        return false;
    }

    private static bool TryCollectOutputArrayTexts(JsonElement result, out List<string> texts)
    {
        texts = new List<string>();
        if (!result.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in output.EnumerateArray())
            AddOutputItemTexts(item, texts);

        return texts.Count > 0;
    }

    private static void AddOutputItemTexts(JsonElement item, List<string> texts)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty(ContentKey, out var content))
            return;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
                TryAddContentPartText(part, texts);
        }
        else if (content.ValueKind == JsonValueKind.String &&
                 content.GetString() is { Length: > 0 } cs)
        {
            texts.Add(cs);
        }
    }

    private static void TryAddContentPartText(JsonElement part, List<string> texts)
    {
        if (part.ValueKind != JsonValueKind.Object)
            return;
        var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is "output_text" or "text" &&
            part.TryGetProperty("text", out var tx) &&
            tx.GetString() is { Length: > 0 } s)
            texts.Add(s);
    }

    private static bool TryFromChatChoices(JsonElement result, out string text)
    {
        if (result.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty(ContentKey, out var mc) &&
                mc.GetString() is { Length: > 0 } mcs)
            {
                text = mcs;
                return true;
            }
        }

        text = "";
        return false;
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
        double temperature = 0.0,
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
            ["temperature"] = temperature,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    [ContentKey] = content,
                },
            },
        };

        var sw = Stopwatch.StartNew();
        var imageNames = paths.Select(Path.GetFileName).OfType<string>().ToList();
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
                Kind = KindVision,
                Endpoint = EndpointResponses,
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
            using var resp = await SendJsonAsync(HttpMethod.Post, EndpointResponses, payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = KindVision,
                    Endpoint = EndpointResponses,
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
                Kind = KindVision,
                Endpoint = EndpointResponses,
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

    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct = default)
    {
        var providerId = PageToMovie.Core.Models.SupportedModelCatalog.ProviderIdForApiBase(
            PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase);
        if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(ApiKeyScope.Get(providerId)))
            return ApiKeyScope.Get(providerId);
        if (!string.IsNullOrWhiteSpace(ApiKeyScope.Current))
            return ApiKeyScope.Current;
        if (_keyProvider is not null && !string.IsNullOrWhiteSpace(UserApiCallScope.UserId)
            && !string.IsNullOrWhiteSpace(providerId))
        {
            var fromUser = await _keyProvider.GetKeyAsync(UserApiCallScope.UserId, providerId, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromUser))
                return fromUser;
        }
        return Environment.GetEnvironmentVariable(PageToMovie.Core.Models.SupportedModelCatalog.XaiApiKeyEnv);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct)
    {
        var key = await ResolveApiKeyAsync(ct).ConfigureAwait(false);
        return await ProviderHttpHelpers.SendJsonAsync(
            _http, method, uri, payload, ct,
            req => ProviderHttpHelpers.ApplyBearer(req, key)).ConfigureAwait(false);
    }

    private static string Trim(string s, int n) => ProviderHttpHelpers.Trim(s, n);
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
