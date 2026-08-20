using System.Text.Json;

namespace PageToMovie.Core.Models;

/// <summary>
/// Resolve the model the operator chose in Settings for a project.
/// Catalog + project config only — never invent Grok/other defaults.
/// </summary>
public static class ProjectModelSelection
{
    public const string VideoConfigKey = "model_name";
    public const string ImageConfigKey = "image_model_name";
    public const string PlanningConfigKey = "planning_model_name";
    public const string ChatConfigKey = "chat_model_name";
    public const string VisionConfigKey = "vision_model_name";
    public const string QualityConfigKey = "quality_model_name";
    public const string AudioConfigKey = "audio_model_name";
    public const string VoiceConfigKey = "voice_model_name";

    /// <summary>
    /// Read a configured model id from project config. Empty if unset or "none"/"disabled".
    /// Does not invent defaults.
    /// </summary>
    public static string? TryGet(
        IReadOnlyDictionary<string, JsonElement>? cfg,
        params string[] configKeys)
    {
        if (cfg is null || configKeys.Length == 0) return null;
        foreach (var key in configKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var id = el.GetString()?.Trim();
            if (!IsUsableModelId(id)) continue;
            return id;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="id"/> is a concrete catalog model id (not empty / none / disabled / auto).
    /// </summary>
    public static bool IsUsableModelId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Require a project-configured model that exists (enabled) in the catalog.
    /// Throws <see cref="InvalidOperationException"/> with operator-facing guidance.
    /// </summary>
    public static string Require(
        IReadOnlyDictionary<string, JsonElement>? cfg,
        ModelCapability capability,
        string jobLabel,
        params string[] configKeys)
    {
        var id = TryGet(cfg, configKeys);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                $"{jobLabel}: no model selected. Open Settings → Studio coverage and choose a model for this job.");

        // Capability-scoped lookup only. Do not fall back to Find(id) without capability —
        // that allowed a Chat model in the Video slot (and similar mismatches).
        // Chat↔Vision overlap remains inside SupportedModelCatalog.Find for those two caps.
        var entry = SupportedModelCatalog.Find(id, capability);
        if (entry is null || !entry.Enabled)
        {
            var wrongCap = SupportedModelCatalog.Find(id) is { } other
                ? $" Model '{id}' is catalogued as {other.Capability}, not {capability}."
                : "";
            throw new InvalidOperationException(
                $"{jobLabel}: model '{id}' is not in the models catalog for {capability} (or is disabled).{wrongCap} " +
                "Open Settings → Studio coverage and pick a model that matches this job.");
        }

        return entry.Id;
    }

    public static string RequirePlanning(IReadOnlyDictionary<string, JsonElement>? cfg, string jobLabel = "Script & planning") =>
        Require(cfg, ModelCapability.Chat, jobLabel, PlanningConfigKey, ChatConfigKey);

    public static string RequireVision(IReadOnlyDictionary<string, JsonElement>? cfg, string jobLabel = "Image vision") =>
        Require(cfg, ModelCapability.Vision, jobLabel, VisionConfigKey, PlanningConfigKey, ChatConfigKey);

    /// <summary>
    /// Project-configured vision model when the catalog has an enabled Vision row; otherwise null.
    /// Does not throw — plate sort and other fallback paths use this instead of <see cref="RequireVision"/>.
    /// </summary>
    public static string? TryVision(IReadOnlyDictionary<string, JsonElement>? cfg)
    {
        var id = TryGet(cfg, VisionConfigKey, PlanningConfigKey, ChatConfigKey);
        if (string.IsNullOrWhiteSpace(id)) return null;
        var entry = SupportedModelCatalog.Find(id, ModelCapability.Vision);
        return entry is { Enabled: true } ? entry.Id : null;
    }

    public static string RequireVideoReview(IReadOnlyDictionary<string, JsonElement>? cfg, string jobLabel = "Video review") =>
        Require(cfg, ModelCapability.Chat, jobLabel, QualityConfigKey, VisionConfigKey, PlanningConfigKey);

    public static string RequireVideo(IReadOnlyDictionary<string, JsonElement>? cfg, string jobLabel = "Video generation") =>
        Require(cfg, ModelCapability.Video, jobLabel, VideoConfigKey);

    /// <summary>
    /// Project-configured video model when the catalog has an enabled Video row; otherwise null.
    /// Does not throw — recovery and other fallback paths use this instead of <see cref="RequireVideo"/>.
    /// </summary>
    public static string? TryVideo(IReadOnlyDictionary<string, JsonElement>? cfg)
    {
        var id = TryGet(cfg, VideoConfigKey);
        if (string.IsNullOrWhiteSpace(id)) return null;
        var entry = SupportedModelCatalog.Find(id, ModelCapability.Video);
        return entry is { Enabled: true } ? entry.Id : null;
    }

    public static string RequireImage(IReadOnlyDictionary<string, JsonElement>? cfg, string jobLabel = "Image generation") =>
        Require(cfg, ModelCapability.Image, jobLabel, ImageConfigKey);

    /// <summary>
    /// Require an explicit model id argument (caller already chose it) that exists in the catalog.
    /// Empty/missing → error (no silent default).
    /// </summary>
    public static string RequireExplicit(string? modelId, ModelCapability capability, string jobLabel)
    {
        if (string.IsNullOrWhiteSpace(modelId)
            || modelId.Equals("none", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{jobLabel}: model is required. Open Settings → Studio coverage and choose a model.");

        var entry = SupportedModelCatalog.Find(modelId.Trim(), capability)
                    ?? SupportedModelCatalog.Find(modelId.Trim());
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                $"{jobLabel}: model '{modelId}' is not in the models catalog (or is disabled). " +
                "Open Settings and pick a current model.");

        return entry.Id;
    }
}
