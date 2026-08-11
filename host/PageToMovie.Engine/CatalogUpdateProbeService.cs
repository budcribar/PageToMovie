using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Admin-only: probes public vendor docs/APIs and compares against models_catalog.json.
/// Status: unchanged (green), changed (red), not_found (yellow), error (yellow).
/// Does not write the catalog — caller accepts selected patches then SaveCatalogJson.
/// </summary>
public sealed class CatalogUpdateProbeService
{
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
        var envKeyName = providerId.ToLowerInvariant() switch
        {
            "openai" => "OPENAI_API_KEY",
            "grok" or "xai" => "XAI_API_KEY",
            "anthropic" or "claude" => "ANTHROPIC_API_KEY",
            "gemini" or "google" => "GEMINI_API_KEY",
            "fal" => "FAL_KEY",
            _ => null
        };
        if (envKeyName is not null)
        {
            var env = Environment.GetEnvironmentVariable(envKeyName);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        }
        if (providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase) || providerId.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            var g = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (!string.IsNullOrWhiteSpace(g)) return g.Trim();
        }
        if (providerId.Equals("fal", StringComparison.OrdinalIgnoreCase))
        {
            var f = Environment.GetEnvironmentVariable("FAL_API_KEY");
            if (!string.IsNullOrWhiteSpace(f)) return f.Trim();
        }
        return null;
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
                    Status = "error",
                    Message = ex.Message,
                });
            }

            // Summarize
            if (row.Fields.Count == 0)
            {
                row.Fields.Add(new CatalogFieldProbeResult
                {
                    Field = "(no probes)",
                    Status = "not_found",
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
            UnchangedFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status == "unchanged"),
            ChangedFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status == "changed"),
            NotFoundFields = result.Models.SelectMany(m => m.Fields).Count(f => f.Status is "not_found" or "error"),
            NewModels = result.NewModels.Count,
        };
        return result;
    }

    private async Task ProbeEntryAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var provider = entry.ProviderId ?? "";

        // Capability-agnostic: review dates age (informational, not live)
        if (!string.IsNullOrWhiteSpace(entry.PricingLastReviewedAt) &&
            DateTime.TryParse(entry.PricingLastReviewedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reviewed) &&
            (DateTime.UtcNow.Date - reviewed.Date).TotalDays > 90)
        {
            row.Fields.Add(new CatalogFieldProbeResult
            {
                Field = "pricingLastReviewedAt",
                CatalogValue = entry.PricingLastReviewedAt,
                LiveValue = null,
                Status = "not_found",
                Message = "Last cost review > 90 days ago — re-check vendor pricing.",
            });
        }

        var isFal = string.Equals(provider, "fal", StringComparison.OrdinalIgnoreCase)
                    || entry.Id.StartsWith("fal-", StringComparison.OrdinalIgnoreCase)
                    || entry.Id.StartsWith("fal-ai/", StringComparison.OrdinalIgnoreCase)
                    || (entry.EndpointPath?.Contains("fal", StringComparison.OrdinalIgnoreCase) == true);

        if (isFal)
        {
            await ProbeFalPricingAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(provider, "xai", StringComparison.OrdinalIgnoreCase))
        {
            // P1: pricing from docs pages + existing duration probes for video
            await ProbeXaiPricingAsync(entry, row, ct).ConfigureAwait(false);
            if (entry.Capability == ModelCapability.Video)
                await ProbeXaiVideoAsync(entry, row, ct).ConfigureAwait(false);
            else if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision)
                await ProbeXaiChatExistsAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision &&
            string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            await ProbeOpenAiModelExistsAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision &&
            (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
             || string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)))
        {
            await ProbeAnthropicModelAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision &&
            (string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase)
             || string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)))
        {
            await ProbeGeminiModelAsync(entry, row, userId, ct).ConfigureAwait(false);
            return;
        }

        // Generic: mark key required fields as not_found when no probe
        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = "live_probe",
            CatalogValue = entry.Id,
            Status = "not_found",
            Message = $"No live probe for provider '{provider}' / {entry.Capability}. Review manually.",
            SourceUrl = entry.PricingNotes,
        });
    }

    /// <summary>
    /// P0: fal Platform API list prices — GET /v1/models/pricing?endpoint_id=…
    /// Maps unit_price into imageCostPerImage or videoBaseCostByResolution / per-sec hints.
    /// </summary>
    private async Task ProbeFalPricingAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "fal", ct).ConfigureAwait(false);
        var endpointId = ResolveFalEndpointId(entry);
        const string sourceBase = "https://api.fal.ai/v1/models/pricing";

        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("pricing", null, null, "not_found",
                "FAL_KEY / FAL_API_KEY not set — cannot fetch live fal pricing.",
                sourceBase + "?endpoint_id=" + Uri.EscapeDataString(endpointId)));
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        var url = sourceBase + "?endpoint_id=" + Uri.EscapeDataString(endpointId);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Key " + key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field("pricing", null, null, "error",
                $"fal pricing HTTP {(int)resp.StatusCode}: {Truncate(body, 180)}", url));
            return;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array
            || prices.GetArrayLength() == 0)
        {
            row.Fields.Add(Field("pricing", null, null, "not_found",
                "fal pricing returned no prices[] for this endpoint_id.", url));
            return;
        }

        var price = prices[0];
        var unitPrice = price.TryGetProperty("unit_price", out var up) && up.TryGetDouble(out var upv) ? upv : (double?)null;
        var unit = price.TryGetProperty("unit", out var u) ? u.GetString() : null;
        var currency = price.TryGetProperty("currency", out var c) ? c.GetString() : "USD";

        if (unitPrice is null)
        {
            row.Fields.Add(Field("unit_price", null, null, "not_found", "unit_price missing in fal response.", url));
            return;
        }

        var liveStr = unitPrice.Value.ToString("0.####", CultureInfo.InvariantCulture);
        var unitNote = $"unit={unit ?? "?"} currency={currency}";

        if (entry.Capability == ModelCapability.Image
            || string.Equals(unit, "image", StringComparison.OrdinalIgnoreCase))
        {
            row.Fields.Add(CompareDouble("imageCostPerImage", entry.ImageCostPerImage, unitPrice.Value, url, unitNote));
            return;
        }

        if (entry.Capability == ModelCapability.Video
            || string.Equals(unit, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unit, "second", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unit, "sec", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer flat base fee when catalog has videoBaseCostByResolution; else per-sec table.
            if (entry.VideoBaseCostByResolution is { Count: > 0 } baseTable)
            {
                var catalogBase = baseTable.Values.FirstOrDefault();
                row.Fields.Add(CompareDouble("videoBaseCostByResolution.*", catalogBase, unitPrice.Value, url,
                    unitNote + " (fal unit applied as base/video fee)"));
            }
            else if (entry.VideoCostPerSecondByResolution is { Count: > 0 } perSec)
            {
                var catalogRate = perSec.Values.FirstOrDefault();
                row.Fields.Add(CompareDouble("videoCostPerSecondByResolution.*", catalogRate, unitPrice.Value, url,
                    unitNote + " (fal unit applied as $/sec or per-output)"));
            }
            else
            {
                row.Fields.Add(Field("video_price", null, liveStr, "changed",
                    $"Catalog has no video cost fields; fal reports {liveStr} per {unit}.", url));
            }
            return;
        }

        // Audio / lip-sync / other — report raw unit price for manual mapping
        var catalogHint = entry.ImageCostPerImage
                          ?? entry.CostPerMinuteUsd
                          ?? entry.VideoReferenceImageCost;
        row.Fields.Add(CompareDouble("unit_price", catalogHint, unitPrice.Value, url, unitNote));
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
        var client = _httpFactory.CreateClient("catalog-probe");
        var docUrl = ResolveXaiDocsUrl(entry);
        string html;
        try
        {
            html = await client.GetStringAsync(docUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("pricing_docs", null, null, "error", ex.Message, docUrl));
            return;
        }

        if (entry.Capability is ModelCapability.Chat or ModelCapability.Vision)
        {
            // Prefer "Input … $X" / "Output … $Y" style; fallback to first two $ amounts near "1M"
            var inMatch = Regex.Match(html,
                @"Input[^$]{0,80}\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*1M|/1M|per\s*1M|/\s*million)?",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var outMatch = Regex.Match(html,
                @"Output[^$]{0,80}\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*1M|/1M|per\s*1M|/\s*million)?",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (inMatch.Success && double.TryParse(inMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var inLive))
                row.Fields.Add(CompareDouble("inputCostPerMillionTokens", entry.InputCostPerMillionTokens, inLive, docUrl, "parsed Input $/1M"));
            else
                row.Fields.Add(Field("inputCostPerMillionTokens", entry.InputCostPerMillionTokens?.ToString(CultureInfo.InvariantCulture), null, "not_found",
                    "Could not parse Input $/1M from docs.", docUrl));

            if (outMatch.Success && double.TryParse(outMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var outLive))
                row.Fields.Add(CompareDouble("outputCostPerMillionTokens", entry.OutputCostPerMillionTokens, outLive, docUrl, "parsed Output $/1M"));
            else
                row.Fields.Add(Field("outputCostPerMillionTokens", entry.OutputCostPerMillionTokens?.ToString(CultureInfo.InvariantCulture), null, "not_found",
                    "Could not parse Output $/1M from docs.", docUrl));
            return;
        }

        if (entry.Capability == ModelCapability.Image)
        {
            var m = Regex.Match(html,
                @"\$([0-9]+(?:\.[0-9]+)?)\s*(?:/\s*image|per\s*image)",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(html, @"Pricing[^$]{0,40}\$([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var imgLive))
                row.Fields.Add(CompareDouble("imageCostPerImage", entry.ImageCostPerImage, imgLive, docUrl, "parsed $/image"));
            else
                row.Fields.Add(Field("imageCostPerImage", entry.ImageCostPerImage?.ToString(CultureInfo.InvariantCulture), null, "not_found",
                    "Could not parse $/image from docs.", docUrl));
            return;
        }

        if (entry.Capability == ModelCapability.Video)
        {
            // Resolution-tiered: 480p $0.05, 720p $0.07, or flat $0.05 per second
            var tierMatches = Regex.Matches(html,
                @"(480p|720p|1080p)[^$]{0,40}\$([0-9]+(?:\.[0-9]+)?)",
                RegexOptions.IgnoreCase);
            var foundTier = false;
            foreach (Match tm in tierMatches)
            {
                var res = tm.Groups[1].Value.ToLowerInvariant();
                if (!double.TryParse(tm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var liveRate))
                    continue;
                foundTier = true;
                double? catalog = null;
                if (entry.VideoCostPerSecondByResolution is { } table)
                {
                    foreach (var kv in table)
                    {
                        if (kv.Key.Contains(res.TrimEnd('p'), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Key, res, StringComparison.OrdinalIgnoreCase))
                        {
                            catalog = kv.Value;
                            break;
                        }
                    }
                    catalog ??= table.Values.FirstOrDefault();
                }
                row.Fields.Add(CompareDouble($"videoCostPerSecondByResolution.{res}", catalog, liveRate, docUrl,
                    "parsed resolution tier $/sec"));
            }

            if (!foundTier)
            {
                var m = Regex.Match(html,
                    @"\$([0-9]+(?:\.[0-9]+)?)\s*(?:per\s*second|/\s*sec|/\s*second)",
                    RegexOptions.IgnoreCase);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secLive))
                {
                    double? catalog = entry.VideoCostPerSecondByResolution?.Values.FirstOrDefault();
                    row.Fields.Add(CompareDouble("videoCostPerSecondByResolution.*", catalog, secLive, docUrl, "parsed $/sec"));
                }
                else
                {
                    row.Fields.Add(Field("videoCostPerSecondByResolution", null, null, "not_found",
                        "Could not parse video $/sec from docs.", docUrl));
                }
            }

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
    }

    private static string ResolveXaiDocsUrl(SupportedModelEntry entry)
    {
        var id = entry.Id.ToLowerInvariant();
        // Prefer model-specific docs pages when id matches known products
        if (id.Contains("imagine-video", StringComparison.Ordinal))
            return "https://docs.x.ai/developers/models/grok-imagine-video";
        if (id.Contains("imagine-image-quality", StringComparison.Ordinal) || id.Contains("image-quality", StringComparison.Ordinal))
            return "https://docs.x.ai/developers/models/grok-imagine-image-quality";
        if (id.Contains("imagine-image", StringComparison.Ordinal) || entry.Capability == ModelCapability.Image)
            return "https://docs.x.ai/developers/models/grok-imagine-image";
        if (id.Contains("grok-4.5", StringComparison.Ordinal) || id.Contains("grok-4-5", StringComparison.Ordinal))
            return "https://docs.x.ai/developers/models/grok-4.5";
        if (id.Contains("grok-4", StringComparison.Ordinal))
            return "https://docs.x.ai/developers/models/grok-4";
        // Generic models index — still has pricing tables in HTML
        return "https://docs.x.ai/developers/models/" + Uri.EscapeDataString(entry.Id);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private async Task ProbeXaiVideoAsync(SupportedModelEntry entry, CatalogModelProbeResult row, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("catalog-probe");
        // Public docs — extension duration 2–10, generation 1–15 (as of docs scan)
        string? extHtml = null;
        string? genHtml = null;
        try
        {
            extHtml = await client.GetStringAsync(
                "https://docs.x.ai/developers/model-capabilities/video/extension", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("docs.extension", null, null, "error", ex.Message,
                "https://docs.x.ai/developers/model-capabilities/video/extension"));
        }

        try
        {
            genHtml = await client.GetStringAsync(
                "https://docs.x.ai/developers/model-capabilities/video/generation", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("docs.generation", null, null, "error", ex.Message,
                "https://docs.x.ai/developers/model-capabilities/video/generation"));
        }

        // Extension max: docs say 2–10 seconds
        if (!string.IsNullOrEmpty(extHtml))
        {
            var m = Regex.Match(extHtml, @"extension duration range is\s+\*?\*?(\d+)\s*[–-]\s*(\d+)\s*seconds",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(extHtml, @"(\d+)\s*[–-]\s*(\d+)\s*seconds", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var liveExtMax))
            {
                var catalog = entry.MaxExtensionSeconds;
                row.Fields.Add(CompareInt("maxExtensionSeconds", catalog, liveExtMax,
                    "https://docs.x.ai/developers/model-capabilities/video/extension"));
            }
            else
            {
                row.Fields.Add(Field("maxExtensionSeconds", entry.MaxExtensionSeconds?.ToString(), null, "not_found",
                    "Could not parse extension duration from docs.",
                    "https://docs.x.ai/developers/model-capabilities/video/extension"));
            }
        }

        if (!string.IsNullOrEmpty(genHtml))
        {
            var m = Regex.Match(genHtml, @"allowed range is\s+(\d+)\s*[–-]\s*(\d+)\s*seconds",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(genHtml, @"(\d+)\s*[–-]\s*(\d+)\s*seconds", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var liveMax))
            {
                row.Fields.Add(CompareInt("maxClipDurationSeconds", entry.MaxClipDurationSeconds, liveMax,
                    "https://docs.x.ai/developers/model-capabilities/video/generation"));
                row.Fields.Add(CompareInt("absMaxClipDurationSeconds", entry.AbsMaxClipDurationSeconds, liveMax,
                    "https://docs.x.ai/developers/model-capabilities/video/generation"));
            }
            else
            {
                row.Fields.Add(Field("maxClipDurationSeconds", entry.MaxClipDurationSeconds?.ToString(), null, "not_found",
                    "Could not parse generation duration from docs.",
                    "https://docs.x.ai/developers/model-capabilities/video/generation"));
            }
        }

        // Reference images: multi-image docs historically say 7
        try
        {
            var refHtml = await client.GetStringAsync(
                "https://docs.x.ai/developers/model-capabilities/video/reference-to-video", ct).ConfigureAwait(false);
            var rm = Regex.Match(refHtml, @"maximum of\s+\*?\*?(\d+)\s+reference images", RegexOptions.IgnoreCase);
            if (rm.Success && int.TryParse(rm.Groups[1].Value, out var liveRefs))
            {
                row.Fields.Add(CompareInt("maxReferenceImages", entry.MaxReferenceImages, liveRefs,
                    "https://docs.x.ai/developers/model-capabilities/video/reference-to-video"));
            }
            else
            {
                row.Fields.Add(Field("maxReferenceImages", entry.MaxReferenceImages?.ToString(), null, "not_found",
                    "Could not parse max reference images.",
                    "https://docs.x.ai/developers/model-capabilities/video/reference-to-video"));
            }
        }
        catch (Exception ex)
        {
            row.Fields.Add(Field("maxReferenceImages", entry.MaxReferenceImages?.ToString(), null, "error", ex.Message,
                "https://docs.x.ai/developers/model-capabilities/video/reference-to-video"));
        }
    }

    private async Task ProbeOpenAiModelExistsAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "openai", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
                "OPENAI_API_KEY not configured — cannot list OpenAI models.", "https://platform.openai.com/docs/models"));
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "error", $"OpenAI models list HTTP {(int)resp.StatusCode}",
                "https://api.openai.com/v1/models"));
            return;
        }

        var found = DataArrayContainsId(body, entry.Id);

        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = "model_id",
            CatalogValue = entry.Id,
            LiveValue = found ? entry.Id : null,
            Status = found ? "unchanged" : "not_found",
            Message = found ? "Present in OpenAI /v1/models." : "Not present in OpenAI /v1/models for this API key.",
            SourceUrl = "https://api.openai.com/v1/models",
        });
    }

    private async Task ProbeXaiChatExistsAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "xai", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
                "XAI_API_KEY not configured — cannot list xAI models.", "https://api.x.ai/v1/models"));
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "error", $"xAI models list HTTP {(int)resp.StatusCode}",
                "https://api.x.ai/v1/models"));
            return;
        }

        var found = DataArrayContainsId(body, entry.Id);

        row.Fields.Add(new CatalogFieldProbeResult
        {
            Field = "model_id",
            CatalogValue = entry.Id,
            LiveValue = found ? entry.Id : null,
            Status = found ? "unchanged" : "not_found",
            Message = found ? "Present in xAI /v1/models." : "Not present in xAI /v1/models for this API key.",
            SourceUrl = "https://api.x.ai/v1/models",
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
        var key = await ResolveKeyAsync(userId, "openai", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("OpenAI: skipped (no OPENAI_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
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
                ProviderId = "openai",
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
        var key = await ResolveKeyAsync(userId, "xai", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("xAI: skipped (no XAI_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
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
            ProviderId = "xai",
            SuggestedCapability = id.Contains("video", StringComparison.OrdinalIgnoreCase) ? "Video"
                : id.Contains("image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Chat",
            Source = "xAI GET /v1/models",
            LabMode = true,
            LabNotes = "Discovered via xAI models list — add as lab and fill limits/costs before production.",
        });
        result.DiscoveryNotes.Add($"xAI: {added} candidate model(s) not in catalog.");
    }


    /// <summary>P1-A: Anthropic GET /v1/models — existence + optional max token fields.</summary>
    private async Task ProbeAnthropicModelAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "anthropic", ct).ConfigureAwait(false);
        const string url = "https://api.anthropic.com/v1/models";
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
                "ANTHROPIC_API_KEY not configured — cannot list Anthropic models.", url));
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "error",
                $"Anthropic models HTTP {(int)resp.StatusCode}", url));
            return;
        }

        using var doc = JsonDocument.Parse(body);
        JsonElement? match = null;
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)
                    || (id is not null && id.StartsWith(entry.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    match = m;
                    break;
                }
            }
        }

        if (match is null)
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
                "Not present in Anthropic /v1/models for this API key.", url));
            return;
        }

        var liveId = match.Value.TryGetProperty("id", out var lid) ? lid.GetString() : entry.Id;
        row.Fields.Add(Field("model_id", entry.Id, liveId, "unchanged",
            "Present in Anthropic /v1/models.", url));

        if (match.Value.TryGetProperty("max_input_tokens", out var mit) && mit.TryGetInt32(out var inTok) && inTok > 0)
            row.Fields.Add(CompareInt("maxInputTokens", entry.MaxInputTokens, inTok, url));
        if (match.Value.TryGetProperty("max_tokens", out var mot) && mot.TryGetInt32(out var outTok) && outTok > 0)
            row.Fields.Add(CompareInt("maxOutputTokens", entry.MaxOutputTokens, outTok, url));
    }

    /// <summary>P1-B: Gemini GET /v1beta/models — existence + input/output token limits.</summary>
    private async Task ProbeGeminiModelAsync(SupportedModelEntry entry, CatalogModelProbeResult row, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "gemini", ct).ConfigureAwait(false);
        const string baseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
        if (string.IsNullOrWhiteSpace(key))
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
                "GEMINI_API_KEY / GOOGLE_API_KEY not configured — cannot list Gemini models.", baseUrl));
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        var modelLeaf = entry.Id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? entry.Id["models/".Length..]
            : entry.Id;
        var getUrl = $"{baseUrl}/{modelLeaf}?key={Uri.EscapeDataString(key.Trim())}";

        using var resp = await client.GetAsync(getUrl, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : entry.Id;
            row.Fields.Add(Field("model_id", entry.Id, name, "unchanged", "Present in Gemini models.get.", baseUrl));
            if (doc.RootElement.TryGetProperty("inputTokenLimit", out var it) && it.TryGetInt32(out var inLim) && inLim > 0)
                row.Fields.Add(CompareInt("maxInputTokens", entry.MaxInputTokens, inLim, baseUrl));
            if (doc.RootElement.TryGetProperty("outputTokenLimit", out var ot) && ot.TryGetInt32(out var outLim) && outLim > 0)
                row.Fields.Add(CompareInt("maxOutputTokens", entry.MaxOutputTokens, outLim, baseUrl));
            return;
        }

        var listUrl = $"{baseUrl}?key={Uri.EscapeDataString(key.Trim())}&pageSize=100";
        using var listResp = await client.GetAsync(listUrl, ct).ConfigureAwait(false);
        var listBody = await listResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!listResp.IsSuccessStatusCode)
        {
            row.Fields.Add(Field("model_id", entry.Id, null, "error",
                $"Gemini models HTTP {(int)resp.StatusCode}/{(int)listResp.StatusCode}", baseUrl));
            return;
        }

        using var listDoc = JsonDocument.Parse(listBody);
        if (listDoc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (name is null) continue;
                var leaf = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name["models/".Length..] : name;
                if (!string.Equals(leaf, entry.Id, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, entry.Id, StringComparison.OrdinalIgnoreCase)
                    && !leaf.StartsWith(entry.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                row.Fields.Add(Field("model_id", entry.Id, name, "unchanged", "Present in Gemini models.list.", baseUrl));
                if (m.TryGetProperty("inputTokenLimit", out var it2) && it2.TryGetInt32(out var inLim2) && inLim2 > 0)
                    row.Fields.Add(CompareInt("maxInputTokens", entry.MaxInputTokens, inLim2, baseUrl));
                if (m.TryGetProperty("outputTokenLimit", out var ot2) && ot2.TryGetInt32(out var outLim2) && outLim2 > 0)
                    row.Fields.Add(CompareInt("maxOutputTokens", entry.MaxOutputTokens, outLim2, baseUrl));
                return;
            }
        }

        row.Fields.Add(Field("model_id", entry.Id, null, "not_found",
            "Not present in Gemini models list for this API key.", baseUrl));
    }

    private async Task DiscoverFromAnthropicAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "anthropic", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("Anthropic: skipped (no ANTHROPIC_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
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
            if (!id.Contains("claude", StringComparison.OrdinalIgnoreCase)) return null;
            var display = m.TryGetProperty("display_name", out var dn) ? dn.GetString() : id;
            return new CatalogNewModelHint
            {
                Id = id,
                Provider = "Anthropic",
                ProviderId = "anthropic",
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
        var key = await ResolveKeyAsync(userId, "gemini", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("Gemini: skipped (no GEMINI_API_KEY / GOOGLE_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        var url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100&key="
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
        var added = 0;
        foreach (var m in models.EnumerateArray())
        {
            var name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? name["models/".Length..]
                : name;
            if (known.Contains(id) || known.Contains(name)) continue;
            var methods = m.TryGetProperty("supportedGenerationMethods", out var sgm) ? sgm.ToString() : "";
            if (methods.Contains("embedContent", StringComparison.OrdinalIgnoreCase)
                && !methods.Contains("generateContent", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!id.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                && !id.Contains("imagen", StringComparison.OrdinalIgnoreCase))
                continue;

            var cap = id.Contains("imagen", StringComparison.OrdinalIgnoreCase)
                      || id.Contains("image", StringComparison.OrdinalIgnoreCase)
                ? "Image"
                : "Chat";
            result.NewModels.Add(new CatalogNewModelHint
            {
                Id = id,
                Provider = "Google",
                ProviderId = "google",
                SuggestedCapability = cap,
                Source = "Gemini GET /v1beta/models",
                LabMode = true,
                LabNotes = "Discovered via Gemini models list — add as lab and fill limits/costs before production.",
            });
            known.Add(id);
            if (++added >= 25) break;
        }
        result.DiscoveryNotes.Add($"Gemini: {added} candidate model(s) not in catalog.");
    }

    /// <summary>P1-C: fal GET /v1/models — discover endpoint_ids not in catalog.</summary>
    private async Task DiscoverFromFalAsync(CatalogUpdateScanResult result, HashSet<string> known, string? userId, CancellationToken ct)
    {
        var key = await ResolveKeyAsync(userId, "fal", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            result.DiscoveryNotes.Add("fal: skipped (no FAL_KEY / FAL_API_KEY configured).");
            return;
        }

        var client = _httpFactory.CreateClient("catalog-probe");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.fal.ai/v1/models?limit=50");
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
        var added = 0;
        foreach (var m in models.EnumerateArray())
        {
            var id = m.TryGetProperty("endpoint_id", out var eid) ? eid.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || known.Contains(id)) continue;

            var category = "";
            var display = id;
            if (m.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("category", out var cat))
                    category = cat.GetString() ?? "";
                if (meta.TryGetProperty("display_name", out var dn) && dn.GetString() is { } d)
                    display = d;
            }

            var cap = category.Contains("video", StringComparison.OrdinalIgnoreCase) ? "Video"
                : category.Contains("image", StringComparison.OrdinalIgnoreCase) ? "Image"
                : category.Contains("audio", StringComparison.OrdinalIgnoreCase)
                  || category.Contains("music", StringComparison.OrdinalIgnoreCase) ? "Audio"
                : category.Contains("speech", StringComparison.OrdinalIgnoreCase)
                  || category.Contains("tts", StringComparison.OrdinalIgnoreCase) ? "Voice"
                : "Image";

            result.NewModels.Add(new CatalogNewModelHint
            {
                Id = id,
                Provider = "Fal",
                ProviderId = "fal",
                SuggestedCapability = cap,
                Source = "fal GET /v1/models",
                LabMode = true,
                LabNotes = $"Discovered via fal model search ({display}, category={category}) — add as lab; use pricing scan for unit_price.",
            });
            known.Add(id);
            if (++added >= 30) break;
        }
        result.DiscoveryNotes.Add($"fal: {added} candidate endpoint(s) not in catalog.");
    }

    private static CatalogFieldProbeResult CompareInt(string field, int? catalog, int live, string? url)
    {
        if (catalog is null)
        {
            return Field(field, null, live.ToString(CultureInfo.InvariantCulture), "changed",
                "Catalog missing; live value available.", url);
        }
        if (catalog.Value == live)
        {
            return Field(field, catalog.Value.ToString(CultureInfo.InvariantCulture),
                live.ToString(CultureInfo.InvariantCulture), "unchanged", "Matches live docs/API.", url);
        }
        return Field(field, catalog.Value.ToString(CultureInfo.InvariantCulture),
            live.ToString(CultureInfo.InvariantCulture), "changed", "Catalog differs from live probe.", url);
    }

    private static CatalogFieldProbeResult CompareDouble(
        string field, double? catalog, double live, string? url, string? note = null)
    {
        var liveStr = live.ToString("0.####", CultureInfo.InvariantCulture);
        var msgSuffix = string.IsNullOrWhiteSpace(note) ? "" : " " + note;
        if (catalog is null)
        {
            return Field(field, null, liveStr, "changed",
                "Catalog missing; live value available." + msgSuffix, url);
        }
        if (Math.Abs(catalog.Value - live) < 0.00005)
        {
            return Field(field, catalog.Value.ToString("0.####", CultureInfo.InvariantCulture),
                liveStr, "unchanged", "Matches live docs/API." + msgSuffix, url);
        }
        return Field(field, catalog.Value.ToString("0.####", CultureInfo.InvariantCulture),
            liveStr, "changed", "Catalog differs from live probe." + msgSuffix, url);
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
