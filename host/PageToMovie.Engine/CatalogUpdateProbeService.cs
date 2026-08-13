using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Admin-only: probes public vendor docs/APIs and compares against models_catalog.json.
/// Status: unchanged (green), changed (red), not_found (yellow), error (yellow).
/// Does not write the catalog — caller accepts selected patches then SaveCatalogJson.
/// </summary>
public sealed class CatalogUpdateProbeService
{
    private const string HttpClientName = "catalog-probe";
    private const string StatusError = "error";
    private const string StatusNotFound = "not_found";
    private const string StatusUnchanged = "unchanged";
    private const string StatusChanged = "changed";
    private const string FieldModelId = "model_id";
    private const string UnitImage = "image";
    private const string CapabilityImage = "Image";
    private const string ModelsPrefix = "models/";
    private const string AuthBearer = "Bearer";
    private const string FormatFourDecimals = "0.####";

    // Provider ids match AiProviderId.ToApiString() where that enum has a member.
    private const string ProviderOpenAi = "openai";
    private const string ProviderAnthropic = "anthropic";
    private const string ProviderGemini = "gemini";
    private const string ProviderGoogle = "google";
    private const string ProviderGrok = "grok";
    private const string ProviderXai = "xai";
    private const string ProviderFal = "fal";
    private const string ProviderClaude = "claude";

    private const string XaiModelsUrl = SupportedModelCatalog.XaiApiBase + "/models";
    private const string AnthropicModelsUrl = SupportedModelCatalog.AnthropicApiBase + "/models";
    private const string AnthropicModelsListUrl = AnthropicModelsUrl + "?limit=100";
    private const string GeminiModelsUrl = SupportedModelCatalog.GoogleApiBase + "/models";
    private const string OpenAiModelsUrl = "https://api.openai.com/v1/models";
    private const string OpenAiDocsModelsUrl = "https://platform.openai.com/docs/models";
    private const string FalModelsPricingUrl = "https://api.fal.ai/v1/models/pricing";
    private const string FalModelsUrl = "https://api.fal.ai/v1/models";
    private const string FalModelsListUrl = FalModelsUrl + "?limit=50";

    private const string XaiDocsDevelopersBase = "https://docs.x.ai/developers";
    private const string XaiDocsModelsBase = XaiDocsDevelopersBase + "/models/";
    private const string XaiDocsGrokImagineVideo = XaiDocsModelsBase + "grok-imagine-video";
    private const string XaiDocsGrokImagineImageQuality = XaiDocsModelsBase + "grok-imagine-image-quality";
    private const string XaiDocsGrokImagineImage = XaiDocsModelsBase + "grok-imagine-image";
    private const string XaiDocsGrok45 = XaiDocsModelsBase + "grok-4.5";
    private const string XaiDocsGrok4 = XaiDocsModelsBase + "grok-4";
    private const string XaiDocsVideoExtension = XaiDocsDevelopersBase + "/model-capabilities/video/extension";
    private const string XaiDocsVideoGeneration = XaiDocsDevelopersBase + "/model-capabilities/video/generation";
    private const string XaiDocsVideoReferenceToVideo = XaiDocsDevelopersBase + "/model-capabilities/video/reference-to-video";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IUserApiKeyProvider? _keyProvider;

    public CatalogUpdateProbeService(IHttpClientFactory httpFactory, IUserApiKeyProvider? keyProvider = null)
    {
        _httpFactory = httpFactory;
        _keyProvider = keyProvider;
    }

    private async Task<string?> ResolveKeyAsync(string? userId, string providerId, CancellationToken ct = default)
    {
        if (_keyProvider is not null)
        {
            var k = await _keyProvider.GetKeyAsync(userId, providerId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
        }
        if (TryTrimmedEnv(EnvKeyNameForProvider(providerId), out var env)) return env;
        if (IsGeminiFamily(providerId) && TryTrimmedEnv("GOOGLE_API_KEY", out var g)) return g;
        if (providerId.Equals(ProviderFal, StringComparison.OrdinalIgnoreCase) && TryTrimmedEnv("FAL_API_KEY", out var f))
            return f;
        return null;
    }

    private static string? EnvKeyNameForProvider(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            ProviderOpenAi => "OPENAI_API_KEY",
            ProviderGrok or ProviderXai => "XAI_API_KEY",
            ProviderAnthropic or ProviderClaude => "ANTHROPIC_API_KEY",
            ProviderGemini or ProviderGoogle => "GEMINI_API_KEY",
            ProviderFal => "FAL_KEY",
            _ => null
        };

    private static bool IsGeminiFamily(string providerId) =>
        providerId.Equals(ProviderGemini, StringComparison.OrdinalIgnoreCase)
        || providerId.Equals(ProviderGoogle, StringComparison.OrdinalIgnoreCase);

    private static bool TryTrimmedEnv(string? name, out string value)
    {
        value = "";
        if (name is null) return false;
        var env = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(env)) return false;
        value = env.Trim();
        return true;
    }

    public async Task<CatalogUpdateScanResult> ScanAsync(string? userId = null, CancellationToken ct = default)
    {
        SupportedModelCatalog.ReloadCatalog();
        var result = new CatalogUpdateScanResult
        {
            CheckedAtUtc = DateTime.UtcNow.ToString("o"),
        };

        foreach (var entry in SupportedModelCatalog.Entries.Where(e => e.Enabled && !e.Deprecated))
        {
            var row = new CatalogModelProbeResult
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Capability = entry.Capability.ToString(),
                ProviderId = entry.ProviderId,
                LabMode = entry.LabMode,
            };

            try
            {
                await ProbeEntryAsync(entry, row, userId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                row.Fields.Add(new CatalogFieldProbeResult
                {
                    Field = "(probe)",
                    CatalogValue = null,
                    LiveValue = null,
                    Status = StatusError,
                    Message = ex.Message,
                });
            }

            // Summarize
            if (row.Fields.Count == 0)
            {
                row.Fields.Add(new CatalogFieldProbeResult
                {
                    Field = "(no probes)",
                    Status = StatusNotFound,
                    Message = "No automated probe registered for this model yet.",
                });
            }

            result.Models.Add(row);
        }

        try
        {
            await DiscoverNewModelsAsync(result, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.DiscoveryNotes.Add("New-model discovery error: " + ex.Message);
        }

        result.Summary = new CatalogUpdateSummary
        {
            ModelsScanned = result.Models.Count,
            UnchangedFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status == StatusUnchanged),
            ChangedFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status == StatusChanged),
            NotFoundFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status is StatusNotFound or StatusError),
            NewModels = result.NewModels.Count,
        };
        return result;
    }

    private async Task ProbeEntryAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var provider = entry.ProviderId ?? "";
        AddStalePricingReviewIfNeeded(entry, row);

        if (IsFalEntry(entry, provider))
        {
            await ProbeFalPricingAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(provider, ProviderXai, StringComparison.OrdinalIgnoreCase))
        {
            await ProbeXaiEntryAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (await TryProbeChatVisionByProviderAsync(entry, row, provider, userId, ct).ConfigureAwait(false))
            return;

        // Generic: mark key required fields as not_found when no probe
        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = "live_probe",
            CatalogValue = entry.Id,
            Status = StatusNotFound,
            Message = $"No live probe for provider '{provider}' / {entry.Capability}. Review manually.",
            SourceUrl = entry.PricingNotes,
        });
    }

    private static void AddStalePricingReviewIfNeeded(SupportedModelEntry entry, CatalogModelProbeResult row)
    {
        // Capability-agnostic: review dates age (informational, not live)
        if (string.IsNullOrWhiteSpace(entry.PricingLastReviewedAt) ||
            !DateTime.TryParse(entry.PricingLastReviewedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reviewed) ||
            (DateTime.UtcNow.Date - reviewed.Date).TotalDays <= 90)
            return;

        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = "pricingLastReviewedAt",
            CatalogValue = entry.PricingLastReviewedAt,
            LiveValue = null,
            Status = StatusNotFound,
            Message = "Last cost review > 90 days ago — re-check vendor pricing.",
        });
    }

    private static bool IsFalEntry(SupportedModelEntry entry, string provider) =>
        string.Equals(provider, ProviderFal, StringComparison.OrdinalIgnoreCase)
        || entry.Id.StartsWith("fal-", StringComparison.OrdinalIgnoreCase)
        || entry.Id.StartsWith("fal-ai/", StringComparison.OrdinalIgnoreCase)
        || (entry.EndpointPath?.Contains(ProviderFal, StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsChatOrVision(SupportedModelEntry entry) =>
        entry.Capability is ModelCapability.Chat or ModelCapability.Vision;

    private async Task ProbeXaiEntryAsync(
        SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        // P1: pricing from docs pages + existing duration probes for video
        await ProbeXaiPricingAsync(entry, row, ct).ConfigureAwait(false);
        if (entry.Capability == ModelCapability.Video)
            await ProbeXaiVideoAsync(entry, row, ct).ConfigureAwait(false);
        else if (IsChatOrVision(entry))
            await ProbeXaiChatExistsAsync(entry, row, userId, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryProbeChatVisionByProviderAsync(
        SupportedModelEntry entry, CatalogModelProbeResult row, string provider, string? userId, CancellationToken ct)
    {
        if (!IsChatOrVision(entry)) return false;
        if (string.Equals(provider, ProviderOpenAi, StringComparison.OrdinalIgnoreCase))
        {
            await ProbeOpenAiModelExistsAsync(entry, row, userId, ct).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(provider, ProviderAnthropic, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, ProviderClaude, StringComparison.OrdinalIgnoreCase))
        {
            await ProbeAnthropicModelAsync(entry, row, userId, ct).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(provider, ProviderGoogle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, ProviderGemini, StringComparison.OrdinalIgnoreCase))
        {
            await ProbeGeminiModelAsync(entry, row, userId, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// P0: fal Platform API list prices — GET /v1/models/pricing?endpoint_id=…
    /// Maps unit_price into imageCostPerImage or videoBaseCostByResolution / per-sec hints.
    /// </summary>
    private async Task ProbeFalPricingAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderFal, ct).ConfigureAwait(false);
        var endpointId = ResolveFalEndpointId(entry);
        var url = FalModelsPricingUrl + "?endpoint_id=" + Uri.EscapeDataString(endpointId);

        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("pricing", null, null, StatusNotFound,
                "FAL_KEY / FAL_API_KEY not set — cannot fetch live fal pricing.", url));
            return;
        }

        var body = await FetchFalPricingBodyAsync(key, url, row, ct).ConfigureAwait(false);
        if (body is null) return;
        if (!TryReadFalUnitPrice(body, url, row, out var unitPrice, out var unit, out var currency))
            return;
        AddFalUnitPriceFields(entry, row, unitPrice, unit, currency, url);
    }

    private async Task<string?> FetchFalPricingBodyAsync(
        string key, string url, CatalogModelProbeResult row, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Key " + key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return body;
        row.Fields.Add(Field("pricing", null, null, StatusError,
            $"fal pricing HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}", url));
        return null;
    }

    private static bool TryReadFalUnitPrice(
        string body, string url, CatalogModelProbeResult row,
        out double unitPrice, out string? unit, out string? currency)
    {
        unitPrice = 0;
        unit = null;
        currency = "USD";
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array
            || prices.GetArrayLength() == 0)
        {
            row.Fields.Add(Field("pricing", null, null, StatusNotFound,
                "fal pricing returned no prices[] for this endpoint_id.", url));
            return false;
        }

        var price = prices[0];
        unit = price.TryGetProperty("unit", out var u) ? u.GetString() : null;
        currency = price.TryGetProperty("currency", out var c) ? c.GetString() : "USD";
        if (price.TryGetProperty("unit_price", out var up) && up.TryGetDouble(out var upv))
        {
            unitPrice = upv;
            return true;
        }
        row.Fields.Add(Field("unit_price", null, null, StatusNotFound, "unit_price missing in fal response.", url));
        return false;
    }

    private static void AddFalUnitPriceFields(
        SupportedModelEntry entry, CatalogModelProbeResult row,
        double unitPrice, string? unit, string? currency, string url)
    {
        var liveStr = unitPrice.ToString(FormatFourDecimals, CultureInfo.InvariantCulture);
        var unitNote = $"unit={unit ?? "?"} currency={currency}";

        if (entry.Capability == ModelCapability.Image
            || string.Equals(unit, UnitImage, StringComparison.OrdinalIgnoreCase))
        {
            row.Fields.Add(CompareDouble("imageCostPerImage", entry.ImageCostPerImage, unitPrice, url, unitNote));
            return;
        }

        if (IsFalVideoUnit(entry, unit))
        {
            AddFalVideoPrice(entry, row, unitPrice, unit, liveStr, unitNote, url);
            return;
        }

        // Audio / lip-sync / other — report raw unit price for manual mapping
        var catalogHint = entry.ImageCostPerImage
                          ?? entry.CostPerMinuteUsd
                          ?? entry.VideoReferenceImageCost;
        row.Fields.Add(CompareDouble("unit_price", catalogHint, unitPrice, url, unitNote));
    }

    private static bool IsFalVideoUnit(SupportedModelEntry entry, string? unit) =>
        entry.Capability == ModelCapability.Video
        || string.Equals(unit, "video", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "second", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "sec", StringComparison.OrdinalIgnoreCase);

    private static void AddFalVideoPrice(
        SupportedModelEntry entry, CatalogModelProbeResult row,
        double unitPrice, string? unit, string liveStr, string unitNote, string url)
    {
        // Prefer flat base fee when catalog has videoBaseCostByResolution; else per-sec table.
        if (entry.VideoBaseCostByResolution is { Count: > 0 } baseTable)
        {
            var catalogBase = baseTable.Values.FirstOrDefault();
            row.Fields.Add(CompareDouble("videoBaseCostByResolution.*", catalogBase, unitPrice, url,
                unitNote + " (fal unit applied as base/video fee)"));
            return;
        }
        if (entry.VideoCostPerSecondByResolution is { Count: > 0 } perSec)
        {
            var catalogRate = perSec.Values.FirstOrDefault();
            row.Fields.Add(CompareDouble("videoCostPerSecondByResolution.*", catalogRate, unitPrice, url,
                unitNote + " (fal unit applied as $/sec or per-output)"));
            return;
        }
        row.Fields.Add(Field("video_price", null, liveStr, StatusChanged,
            $"Catalog has no video cost fields; fal reports {liveStr} per {unit}.", url));
    }

    private static string ResolveFalEndpointId(SupportedModelEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.EndpointPath)
            && entry.EndpointPath.Contains('/', StringComparison.Ordinal))
            return entry.EndpointPath.Trim().TrimStart('/');
        return entry.Id.Trim();
    }

    /// <summary>
    /// P1: Parse public xAI docs HTML for list prices (chat $/MTok, image $/image, video $/sec).
    /// </summary>
    private async Task ProbeXaiPricingAsync(SupportedModelEntry entry, CatalogModelProbeResult row, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        var docUrl = ResolveXaiDocsUrl(entry);
        string html;
        try
        {
            html = await client.GetStringAsync(docUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("pricing_docs", null, null, StatusError, ex.Message, docUrl));
            return;
        }

        if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision)
        {
            AddXaiChatTokenPricing(entry, row, html, docUrl);
            return;
        }

        if (entry.Capability == ModelCapability.Image)
        {
            AddXaiImagePricing(entry, row, html, docUrl);
            return;
        }

        if (entry.Capability == ModelCapability.Video)
            AddXaiVideoPricing(entry, row, html, docUrl);
    }

    private static void AddXaiChatTokenPricing(SupportedModelEntry entry, CatalogModelProbeResult row, string html, string docUrl)
    {
        // Prefer "Input … $X" / "Output … $Y" style; fallback to first two $ amounts near "1M"
        var inMatch = CommonRegex.Match(html,
            @"Input[^$]{0,80}\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*1M|/1M|per\s*1M|/\s*million)?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var outMatch = CommonRegex.Match(html,
            @"Output[^$]{0,80}\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*1M|/1M|per\s*1M|/\s*million)?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (inMatch.Success && double.TryParse(inMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var inLive))
            row.Fields.Add(CompareDouble("inputCostPerMillionTokens", entry.InputCostPerMillionTokens, inLive, docUrl, "parsed Input $/1M"));
        else
            row.Fields.Add(Field("inputCostPerMillionTokens", entry.InputCostPerMillionTokens?.ToString(CultureInfo.InvariantCulture), null, StatusNotFound,
                "Could not parse Input $/1M from docs.", docUrl));

        if (outMatch.Success && double.TryParse(outMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var outLive))
            row.Fields.Add(CompareDouble("outputCostPerMillionTokens", entry.OutputCostPerMillionTokens, outLive, docUrl, "parsed Output $/1M"));
        else
            row.Fields.Add(Field("outputCostPerMillionTokens", entry.OutputCostPerMillionTokens?.ToString(CultureInfo.InvariantCulture), null, StatusNotFound,
                "Could not parse Output $/1M from docs.", docUrl));
    }

    private static void AddXaiImagePricing(SupportedModelEntry entry, CatalogModelProbeResult row, string html, string docUrl)
    {
        var m = CommonRegex.Match(html,
            @"\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*image|per\s*image)",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = CommonRegex.Match(html, @"Pricing[^$]{0,40}\$([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var imgLive))
            row.Fields.Add(CompareDouble("imageCostPerImage", entry.ImageCostPerImage, imgLive, docUrl, "parsed $/image"));
        else
            row.Fields.Add(Field("imageCostPerImage", entry.ImageCostPerImage?.ToString(CultureInfo.InvariantCulture), null, StatusNotFound,
                "Could not parse $/image from docs.", docUrl));
    }

    private static void AddXaiVideoPricing(SupportedModelEntry entry, CatalogModelProbeResult row, string html, string docUrl)
    {
        // Resolution-tiered: 480p $0.05, 720p $0.07, or flat $0.05 per second
        var tierMatches = CommonRegex.Matches(html,
            @"(480p|720p|1080p)[^$]{0,40}\$([0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase);
        var foundTier = false;
        foreach (var (tm, liveRate) in tierMatches.Cast<Match>()
            .Select(static m => (m, parsed: double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate), rate))
            .Where(static x => x.parsed)
            .Select(static x => (x.m, x.rate)))
        {
            var res = tm.Groups[1].Value.ToLowerInvariant();
            foundTier = true;
            var catalog = LookupXaiVideoCostForResolution(entry, res);
            row.Fields.Add(CompareDouble($"videoCostPerSecondByResolution.{res}", catalog, liveRate, docUrl,
                "parsed resolution tier $/sec"));
        }

        if (!foundTier)
            AddXaiVideoFlatSecOrNotFound(entry, row, html, docUrl);

        AddXaiVideoExtendCompare(entry, row, docUrl);
    }

    private static double? LookupXaiVideoCostForResolution(SupportedModelEntry entry, string res)
    {
        if (entry.VideoCostPerSecondByResolution is not { } table)
            return null;
        foreach (var kv in table)
        {
            if (kv.Key.Contains(res.TrimEnd('p'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key, res, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return table.Values.FirstOrDefault();
    }

    private static void AddXaiVideoFlatSecOrNotFound(SupportedModelEntry entry, CatalogModelProbeResult row, string html, string docUrl)
    {
        var m = CommonRegex.Match(html,
            @"\$([0-9]+(?:\.[0-9]+)?)\s*(?:per\s*second|/\s*sec|/\s*second)",
            RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secLive))
        {
            double? catalog = entry.VideoCostPerSecondByResolution?.Values.FirstOrDefault();
            row.Fields.Add(CompareDouble("videoCostPerSecondByResolution.*", catalog, secLive, docUrl, "parsed $/sec"));
        }
        else
        {
            row.Fields.Add(Field("videoCostPerSecondByResolution", null, null, StatusNotFound,
                "Could not parse video $/sec from docs.", docUrl));
        }
    }

    private static void AddXaiVideoExtendCompare(SupportedModelEntry entry, CatalogModelProbeResult row, string docUrl)
    {
        // Extend often billed at generation rate — note only
        if (entry.SupportsVideoContinue && entry.VideoExtendCostPerSecond is { } ext)
        {
            var gen = entry.VideoCostPerSecondByResolution?.Values.FirstOrDefault();
            if (gen is not null)
            {
                row.Fields.Add(CompareDouble("videoExtendCostPerSecond", ext, gen.Value, docUrl,
                    "extend vs generation rate (docs often say extend uses generation $/sec)"));
            }
        }
    }

    private static string ResolveXaiDocsUrl(SupportedModelEntry entry)
    {
        var id = entry.Id.ToLowerInvariant();
        // Prefer model-specific docs pages when id matches known products
        if (id.Contains("imagine-video", StringComparison.Ordinal))
            return XaiDocsGrokImagineVideo;
        if (id.Contains("imagine-image-quality", StringComparison.Ordinal) || id.Contains("image-quality", StringComparison.Ordinal))
            return XaiDocsGrokImagineImageQuality;
        if (id.Contains("imagine-image", StringComparison.Ordinal) || entry.Capability == ModelCapability.Image)
            return XaiDocsGrokImagineImage;
        if (id.Contains("grok-4.5", StringComparison.Ordinal) || id.Contains("grok-4-5", StringComparison.Ordinal))
            return XaiDocsGrok45;
        if (id.Contains("grok-4", StringComparison.Ordinal))
            return XaiDocsGrok4;
        // Generic models index — still has pricing tables in HTML
        return XaiDocsModelsBase + Uri.EscapeDataString(entry.Id);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private async Task ProbeXaiVideoAsync(SupportedModelEntry entry, CatalogModelProbeResult row, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        // Public docs — extension duration 2–10, generation 1–15 (as of docs scan)
        var extHtml = await TryGetDocsHtmlAsync(client, XaiDocsVideoExtension, "docs.extension", row, ct)
            .ConfigureAwait(false);
        var genHtml = await TryGetDocsHtmlAsync(client, XaiDocsVideoGeneration, "docs.generation", row, ct)
            .ConfigureAwait(false);

        AddXaiDurationRangeField(
            extHtml, entry.MaxExtensionSeconds, "maxExtensionSeconds",
            @"extension duration range is\s+\*?\*?(\d+)\s*[–-]\s*(\d+)\s*seconds",
            "Could not parse extension duration from docs.",
            XaiDocsVideoExtension, row, compareAbsMax: false);
        AddXaiDurationRangeField(
            genHtml, entry.MaxClipDurationSeconds, "maxClipDurationSeconds",
            @"allowed range is\s+(\d+)\s*[–-]\s*(\d+)\s*seconds",
            "Could not parse generation duration from docs.",
            XaiDocsVideoGeneration, row, compareAbsMax: true, absCatalog: entry.AbsMaxClipDurationSeconds);

        await AddXaiMaxReferenceImagesAsync(client, entry, row, ct).ConfigureAwait(false);
    }

    private static async Task<string?> TryGetDocsHtmlAsync(
        HttpClient client, string url, string field, CatalogModelProbeResult row, CancellationToken ct)
    {
        try
        {
            return await client.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field(field, null, null, StatusError, ex.Message, url));
            return null;
        }
    }

    private static void AddXaiDurationRangeField(
        string? html,
        int? catalog,
        string field,
        string primaryPattern,
        string notFoundMessage,
        string url,
        CatalogModelProbeResult row,
        bool compareAbsMax,
        int? absCatalog = null)
    {
        if (string.IsNullOrEmpty(html)) return;
        var m = CommonRegex.Match(html, primaryPattern, RegexOptions.IgnoreCase);
        if (!m.Success)
            m = CommonRegex.Match(html, @"(\d+)\s*[–-]\s*(\d+)\s*seconds", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[2].Value, out var liveMax))
        {
            row.Fields.Add(CompareInt(field, catalog, liveMax, url));
            if (compareAbsMax)
                row.Fields.Add(CompareInt("absMaxClipDurationSeconds", absCatalog, liveMax, url));
            return;
        }
        row.Fields.Add(Field(field, catalog?.ToString(), null, StatusNotFound, notFoundMessage, url));
    }

    private async Task AddXaiMaxReferenceImagesAsync(
        HttpClient client, SupportedModelEntry entry, CatalogModelProbeResult row, CancellationToken ct)
    {
        // Reference images: multi-image docs historically say 7
        try
        {
            var refHtml = await client.GetStringAsync(XaiDocsVideoReferenceToVideo, ct).ConfigureAwait(false);
            var rm = CommonRegex.Match(refHtml, @"maximum of\s+\*?\*?(\d+)\s+reference images", RegexOptions.IgnoreCase);
            if (rm.Success && int.TryParse(rm.Groups[1].Value, out var liveRefs))
            {
                row.Fields.Add(CompareInt("maxReferenceImages", entry.MaxReferenceImages, liveRefs,
                    XaiDocsVideoReferenceToVideo));
            }
            else
            {
                row.Fields.Add(Field("maxReferenceImages", entry.MaxReferenceImages?.ToString(), null, StatusNotFound,
                    "Could not parse max reference images.",
                    XaiDocsVideoReferenceToVideo));
            }
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("maxReferenceImages", entry.MaxReferenceImages?.ToString(), null, StatusError, ex.Message,
                XaiDocsVideoReferenceToVideo));
        }
    }

    private async Task ProbeOpenAiModelExistsAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderOpenAi, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
                "OPENAI_API_KEY not configured — cannot list OpenAI models.", OpenAiDocsModelsUrl));
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, OpenAiModelsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue(AuthBearer, key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusError, $"OpenAI models list HTTP {(int)resp.StatusCode}",
                OpenAiModelsUrl));
            return;
        }

        var found = DataArrayContainsId(body, entry.Id);

        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = FieldModelId,
            CatalogValue = entry.Id,
            LiveValue = found ? entry.Id : null,
            Status = found ? StatusUnchanged : StatusNotFound,
            Message = found ? "Present in OpenAI /v1/models." : "Not present in OpenAI /v1/models for this API key.",
            SourceUrl = OpenAiModelsUrl,
        });
    }

    private async Task ProbeXaiChatExistsAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderXai, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
                "XAI_API_KEY not configured — cannot list xAI models.", XaiModelsUrl));
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, XaiModelsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue(AuthBearer, key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusError, $"xAI models list HTTP {(int)resp.StatusCode}",
                XaiModelsUrl));
            return;
        }

        var found = DataArrayContainsId(body, entry.Id);

        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = FieldModelId,
            CatalogValue = entry.Id,
            LiveValue = found ? entry.Id : null,
            Status = found ? StatusUnchanged : StatusNotFound,
            Message = found ? "Present in xAI /v1/models." : "Not present in xAI /v1/models for this API key.",
            SourceUrl = XaiModelsUrl,
        });
    }

    private async Task DiscoverNewModelsAsync(CatalogUpdateScanResult result, string? userId, CancellationToken ct)
    {
        var known = new HashSet<string>(
            SupportedModelCatalog.Entries.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);

        await DiscoverFromOpenAiAsync(result, known, userId, ct).ConfigureAwait(false);
        await DiscoverFromXaiAsync(result, known, userId, ct).ConfigureAwait(false);
        await DiscoverFromAnthropicAsync(result, known, userId, ct).ConfigureAwait(false);
        await DiscoverFromGeminiAsync(result, known, userId, ct).ConfigureAwait(false);
        await DiscoverFromFalAsync(result, known, userId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when a provider's <c>data[]</c> list response contains a model whose <c>id</c> exactly
    /// (case-insensitively) matches <paramref name="id"/>. Shared by the per-provider "exists" probes.
    /// </summary>
    private static bool DataArrayContainsId(string body, string id)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var idEl) &&
                    string.Equals(idEl.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Enumerate a provider's <c>data[]</c> models list, skip ids already known, let the caller
    /// turn each new id into a <see cref="CatalogNewModelHint"/> (returning null to skip it), add
    /// accepted hints (recording their ids as known), and stop after 25. Returns the count added.
    /// Shared by the OpenAI/xAI/Anthropic discovery passes.
    /// </summary>
    private static int AddDiscoveredModels(
        string body,
        HashSet<string> known,
        List<CatalogNewModelHint> newModels,
        Func<string, JsonElement, CatalogNewModelHint?> makeHint)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return 0;
        var added = 0;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || known.Contains(id)) continue;
            var hint = makeHint(id, m);
            if (hint is null) continue;
            newModels.Add(hint);
            known.Add(id);
            if (++added >= 25) break;
        }
        return added;
    }

    private async Task DiscoverFromOpenAiAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderOpenAi, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("OpenAI: skipped (no OPENAI_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, OpenAiModelsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue(AuthBearer, key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            result.DiscoveryNotes.Add($"OpenAI: list failed HTTP {(int)resp.StatusCode}");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var added = AddDiscoveredModels(body, known, result.NewModels, (id, _) =>
        {
            // Only surface chat-like gpt / o-series to avoid noise
            if (!(id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)))
                return null;
            return new CatalogNewModelHint
            {
                Id = id,
                Provider = "OpenAI",
                ProviderId = ProviderOpenAi,
                SuggestedCapability = "Chat",
                Source = "OpenAI GET /v1/models",
                LabMode = true,
                LabNotes = "Discovered via OpenAI models list — add as lab and fill limits/costs before production.",
            };
        });
        result.DiscoveryNotes.Add($"OpenAI: {added} candidate model(s) not in catalog.");
    }

    private async Task DiscoverFromXaiAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderXai, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("xAI: skipped (no XAI_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, XaiModelsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue(AuthBearer, key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            result.DiscoveryNotes.Add($"xAI: list failed HTTP {(int)resp.StatusCode}");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var added = AddDiscoveredModels(body, known, result.NewModels, (id, _) => new CatalogNewModelHint
        {
            Id = id,
            Provider = "Xai",
            ProviderId = ProviderXai,
            SuggestedCapability = id.Contains("video", StringComparison.OrdinalIgnoreCase) ? "Video"
                : id.Contains(UnitImage, StringComparison.OrdinalIgnoreCase) ? CapabilityImage : "Chat",
            Source = "xAI GET /v1/models",
            LabMode = true,
            LabNotes = "Discovered via xAI models list — add as lab and fill limits/costs before production.",
        });
        result.DiscoveryNotes.Add($"xAI: {added} candidate model(s) not in catalog.");
    }


    /// <summary>P1-A: Anthropic GET /v1/models — existence + optional max token fields.</summary>
    private async Task ProbeAnthropicModelAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderAnthropic, ct).ConfigureAwait(false);
        const string url = AnthropicModelsUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
                "ANTHROPIC_API_KEY not configured — cannot list Anthropic models.", url));
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusError,
                $"Anthropic models HTTP {(int)resp.StatusCode}", url));
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var match = FindAnthropicModelInList(doc.RootElement, entry.Id);
        if (match is null)
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
                "Not present in Anthropic /v1/models for this API key.", url));
            return;
        }

        var liveId = match.Value.TryGetProperty("id", out var lid) ? lid.GetString() : entry.Id;
        row.Fields.Add(Field(FieldModelId, entry.Id, liveId, StatusUnchanged,
            "Present in Anthropic /v1/models.", url));
        AddAnthropicTokenLimitFields(entry, row, match.Value, url);
    }

    private static JsonElement? FindAnthropicModelInList(JsonElement root, string entryId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.Equals(id, entryId, StringComparison.OrdinalIgnoreCase)
                || (id is not null && id.StartsWith(entryId, StringComparison.OrdinalIgnoreCase)))
                return m;
        }
        return null;
    }

    private static void AddAnthropicTokenLimitFields(
        SupportedModelEntry entry, CatalogModelProbeResult row, JsonElement match, string url)
    {
        if (match.TryGetProperty("max_input_tokens", out var mit) && mit.TryGetInt32(out var inTok) && inTok > 0)
            row.Fields.Add(CompareInt("maxInputTokens", entry.MaxInputTokens, inTok, url));
        if (match.TryGetProperty("max_tokens", out var mot) && mot.TryGetInt32(out var outTok) && outTok > 0)
            row.Fields.Add(CompareInt("maxOutputTokens", entry.MaxOutputTokens, outTok, url));
    }

    /// <summary>P1-B: Gemini GET /v1beta/models — existence + input/output token limits.</summary>
    private async Task ProbeGeminiModelAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderGemini, ct).ConfigureAwait(false);
        const string baseUrl = GeminiModelsUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
                "GEMINI_API_KEY / GOOGLE_API_KEY not configured — cannot list Gemini models.", baseUrl));
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        var modelLeaf = entry.Id.StartsWith(ModelsPrefix, StringComparison.OrdinalIgnoreCase)
            ? entry.Id[ModelsPrefix.Length..]
            : entry.Id;
        var getUrl = $"{baseUrl}/{modelLeaf}?key={Uri.EscapeDataString(key.Trim())}";

        using var resp = await client.GetAsync(getUrl, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            AddGeminiFieldsFromGetBody(entry, row, body, baseUrl);
            return;
        }

        var listUrl = $"{baseUrl}?key={Uri.EscapeDataString(key.Trim())}&pageSize=100";
        using var listResp = await client.GetAsync(listUrl, ct).ConfigureAwait(false);
        var listBody = await listResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!listResp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusError,
                $"Gemini models HTTP {(int)resp.StatusCode}/{(int)listResp.StatusCode}", baseUrl));
            return;
        }

        if (TryAddGeminiFieldsFromListBody(entry, row, listBody, baseUrl))
            return;

        row.Fields.Add(Field(FieldModelId, entry.Id, null, StatusNotFound,
            "Not present in Gemini models list for this API key.", baseUrl));
    }

    private static void AddGeminiFieldsFromGetBody(SupportedModelEntry entry, CatalogModelProbeResult row, string body, string baseUrl)
    {
        using var doc = JsonDocument.Parse(body);
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : entry.Id;
        row.Fields.Add(Field(FieldModelId, entry.Id, name, StatusUnchanged, "Present in Gemini models.get.", baseUrl));
        AddGeminiTokenLimits(doc.RootElement, entry, row, baseUrl);
    }

    private static void AddGeminiTokenLimits(JsonElement el, SupportedModelEntry entry, CatalogModelProbeResult row, string baseUrl)
    {
        if (el.TryGetProperty("inputTokenLimit", out var it) && it.TryGetInt32(out var inLim) && inLim > 0)
            row.Fields.Add(CompareInt("maxInputTokens", entry.MaxInputTokens, inLim, baseUrl));
        if (el.TryGetProperty("outputTokenLimit", out var ot) && ot.TryGetInt32(out var outLim) && outLim > 0)
            row.Fields.Add(CompareInt("maxOutputTokens", entry.MaxOutputTokens, outLim, baseUrl));
    }

    private static bool TryAddGeminiFieldsFromListBody(SupportedModelEntry entry, CatalogModelProbeResult row, string listBody, string baseUrl)
    {
        using var listDoc = JsonDocument.Parse(listBody);
        if (!listDoc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var m in models.EnumerateArray())
        {
            var name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (name is null || !GeminiListNameMatches(name, entry.Id))
                continue;

            row.Fields.Add(Field(FieldModelId, entry.Id, name, StatusUnchanged, "Present in Gemini models.list.", baseUrl));
            AddGeminiTokenLimits(m, entry, row, baseUrl);
            return true;
        }
        return false;
    }

    private static bool GeminiListNameMatches(string name, string entryId)
    {
        var leaf = name.StartsWith(ModelsPrefix, StringComparison.OrdinalIgnoreCase) ? name[ModelsPrefix.Length..] : name;
        return string.Equals(leaf, entryId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, entryId, StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith(entryId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DiscoverFromAnthropicAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderAnthropic, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("Anthropic: skipped (no ANTHROPIC_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, AnthropicModelsListUrl);
        req.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            result.DiscoveryNotes.Add($"Anthropic: list failed HTTP {(int)resp.StatusCode}");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var added = AddDiscoveredModels(body, known, result.NewModels, (id, m) =>
        {
            if (!id.Contains(ProviderClaude, StringComparison.OrdinalIgnoreCase)) return null;
            var display = m.TryGetProperty("display_name", out var dn) ? dn.GetString() : id;
            return new CatalogNewModelHint
            {
                Id = id,
                Provider = "Anthropic",
                ProviderId = ProviderAnthropic,
                SuggestedCapability = "Chat",
                Source = "Anthropic GET /v1/models",
                LabMode = true,
                LabNotes = $"Discovered via Anthropic models list ({display}) — add as lab and fill limits/costs before production.",
            };
        });
        result.DiscoveryNotes.Add($"Anthropic: {added} candidate model(s) not in catalog.");
    }

    private async Task DiscoverFromGeminiAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderGemini, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("Gemini: skipped (no GEMINI_API_KEY / GOOGLE_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        var url = GeminiModelsUrl + "?pageSize=100&key="
                  + Uri.EscapeDataString(key.Trim());
        using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            result.DiscoveryNotes.Add($"Gemini: list failed HTTP {(int)resp.StatusCode}");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("models", out var models)) return;
        var added = AddGeminiDiscoveredModels(models, known, result.NewModels);
        result.DiscoveryNotes.Add($"Gemini: {added} candidate model(s) not in catalog.");
    }

    private static int AddGeminiDiscoveredModels(
        JsonElement models, HashSet<string> known, List<CatalogNewModelHint> newModels)
    {
        var added = 0;
        foreach (var m in models.EnumerateArray())
        {
            var id = GeminiDiscoveryId(m, known);
            if (id is null || ShouldSkipGeminiDiscovery(m, id)) continue;
            newModels.Add(MakeGeminiDiscoveryHint(id));
            known.Add(id);
            if (++added >= 25) break;
        }
        return added;
    }

    private static string? GeminiDiscoveryId(JsonElement m, HashSet<string> known)
    {
        var name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var id = name.StartsWith(ModelsPrefix, StringComparison.OrdinalIgnoreCase)
            ? name[ModelsPrefix.Length..]
            : name;
        if (known.Contains(id) || known.Contains(name)) return null;
        return id;
    }

    private static bool ShouldSkipGeminiDiscovery(JsonElement m, string id)
    {
        var methods = m.TryGetProperty("supportedGenerationMethods", out var sgm) ? sgm.ToString() : "";
        if (methods.Contains("embedContent", StringComparison.OrdinalIgnoreCase)
            && !methods.Contains("generateContent", StringComparison.OrdinalIgnoreCase))
            return true;
        return !id.Contains(ProviderGemini, StringComparison.OrdinalIgnoreCase)
               && !id.Contains("imagen", StringComparison.OrdinalIgnoreCase);
    }

    private static CatalogNewModelHint MakeGeminiDiscoveryHint(string id)
    {
        var cap = id.Contains("imagen", StringComparison.OrdinalIgnoreCase)
                  || id.Contains(UnitImage, StringComparison.OrdinalIgnoreCase)
            ? CapabilityImage
            : "Chat";
        return new CatalogNewModelHint
        {
            Id = id,
            Provider = "Google",
            ProviderId = ProviderGoogle,
            SuggestedCapability = cap,
            Source = "Gemini GET /v1beta/models",
            LabMode = true,
            LabNotes = "Discovered via Gemini models list — add as lab and fill limits/costs before production.",
        };
    }

    /// <summary>P1-C: fal GET /v1/models — discover endpoint_ids not in catalog.</summary>
    private async Task DiscoverFromFalAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, ProviderFal, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("fal: skipped (no FAL_KEY / FAL_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, FalModelsListUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Key " + key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            result.DiscoveryNotes.Add($"fal: list failed HTTP {(int)resp.StatusCode}");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("models", out var models)) return;
        var added = AddFalDiscoveredModels(models, known, result.NewModels);
        result.DiscoveryNotes.Add($"fal: {added} candidate endpoint(s) not in catalog.");
    }

    private static int AddFalDiscoveredModels(JsonElement models, HashSet<string> known, List<CatalogNewModelHint> newModels)
    {
        var added = 0;
        foreach (var m in models.EnumerateArray())
        {
            var id = m.TryGetProperty("endpoint_id", out var eid) ? eid.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || known.Contains(id)) continue;

            var (category, display) = ReadFalDiscoveryMeta(m, id);
            newModels.Add(new CatalogNewModelHint
            {
                Id = id,
                Provider = "Fal",
                ProviderId = ProviderFal,
                SuggestedCapability = SuggestFalCapability(category),
                Source = "fal GET /v1/models",
                LabMode = true,
                LabNotes = $"Discovered via fal model search ({display}, category={category}) — add as lab; use pricing scan for unit_price.",
            });
            known.Add(id);
            if (++added >= 30) break;
        }
        return added;
    }

    private static (string Category, string Display) ReadFalDiscoveryMeta(JsonElement m, string id)
    {
        var category = "";
        var display = id;
        if (m.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("category", out var cat))
                category = cat.GetString() ?? "";
            if (meta.TryGetProperty("display_name", out var dn) && dn.GetString() is { } d)
                display = d;
        }
        return (category, display);
    }

    private static string SuggestFalCapability(string category)
    {
        if (category.Contains("video", StringComparison.OrdinalIgnoreCase)) return "Video";
        if (category.Contains(UnitImage, StringComparison.OrdinalIgnoreCase)) return CapabilityImage;
        if (category.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || category.Contains("music", StringComparison.OrdinalIgnoreCase))
            return "Audio";
        if (category.Contains("speech", StringComparison.OrdinalIgnoreCase)
            || category.Contains("tts", StringComparison.OrdinalIgnoreCase))
            return "Voice";
        return CapabilityImage;
    }

    private static CatalogFieldProbeResult CompareInt(string field, int? catalog, int live, string? url)
    {
        if (catalog is null)
        {
            return Field(field, null, live.ToString(CultureInfo.InvariantCulture), StatusChanged,
                "Catalog missing; live value available.", url);
        }
        if (catalog.Value == live)
        {
            return Field(field, catalog.Value.ToString(CultureInfo.InvariantCulture),
                live.ToString(CultureInfo.InvariantCulture), StatusUnchanged, "Matches live docs/API.", url);
        }
        return Field(field, catalog.Value.ToString(CultureInfo.InvariantCulture),
            live.ToString(CultureInfo.InvariantCulture), StatusChanged, "Catalog differs from live probe.", url);
    }

    private static CatalogFieldProbeResult CompareDouble(
        string field, double? catalog, double live, string? url, string? note = null)
    {
        var liveStr = live.ToString(FormatFourDecimals, CultureInfo.InvariantCulture);
        var msgSuffix = string.IsNullOrWhiteSpace(note) ? "" : " " + note;
        if (catalog is null)
        {
            return Field(field, null, liveStr, StatusChanged,
                "Catalog missing; live value available." + msgSuffix, url);
        }
        if (Math.Abs(catalog.Value - live) < 0.00005)
        {
            return Field(field, catalog.Value.ToString(FormatFourDecimals, CultureInfo.InvariantCulture),
                liveStr, StatusUnchanged, "Matches live docs/API." + msgSuffix, url);
        }
        return Field(field, catalog.Value.ToString(FormatFourDecimals, CultureInfo.InvariantCulture),
            liveStr, StatusChanged, "Catalog differs from live probe." + msgSuffix, url);
    }

    private static CatalogFieldProbeResult Field(
        string field, string? catalog, string? live, string status, string? message, string? url = null) =>
        new()
        {
            Field = field,
            CatalogValue = catalog,
            LiveValue = live,
            Status = status,
            Message = message,
            SourceUrl = url,
        };
}

public sealed class CatalogUpdateScanResult
{
    public string CheckedAtUtc { get; set; } = "";
    public CatalogUpdateSummary Summary { get; set; } = new();
    public List<CatalogModelProbeResult> Models { get; set; } = new();
    public List<CatalogNewModelHint> NewModels { get; set; } = new();
    public List<string> DiscoveryNotes { get; set; } = new();
}

public sealed class CatalogUpdateSummary
{
    public int ModelsScanned { get; set; }
    public int UnchangedFields { get; set; }
    public int ChangedFields { get; set; }
    public int NotFoundFields { get; set; }
    public int NewModels { get; set; }
}

public sealed class CatalogModelProbeResult
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Capability { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public bool LabMode { get; set; }
    public List<CatalogFieldProbeResult> Fields { get; set; } = new();
}

public sealed class CatalogFieldProbeResult
{
    /// <summary>unchanged | changed | not_found | error</summary>
    public string Status { get; set; } = "not_found";
    public string Field { get; set; } = "";
    public string? CatalogValue { get; set; }
    public string? LiveValue { get; set; }
    public string? Message { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class CatalogNewModelHint
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string SuggestedCapability { get; set; } = "Chat";
    public string Source { get; set; } = "";
    public bool LabMode { get; set; } = true;
    public string? LabNotes { get; set; }
}
