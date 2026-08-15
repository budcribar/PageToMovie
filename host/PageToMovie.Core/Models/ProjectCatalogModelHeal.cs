using System.Text.Json;

namespace PageToMovie.Core.Models;

/// <summary>
/// Rewrite project config model slots whose stored id is disabled, deprecated, or missing
/// from the enabled catalog. Required slots use
/// <see cref="SupportedModelCatalog.DefaultModelIdForCapability"/>; optional audio/voice
/// fall back to <c>none</c>. Does not remap an enabled stored id. Does not invent a model
/// when the catalog has no default.
/// </summary>
public static class ProjectCatalogModelHeal
{
    public const string ModelSelectionsKey = "model_selections";
    public const string VideoReviewModelKey = "video_review_model_name";
    public const string QualityProviderKey = "quality_provider";
    public const string VideoProviderKey = "video_provider";
    public const string ImageProviderKey = "image_provider";
    public const string CharacterDesignProviderKey = "character_design_provider";
    public const string PlanningProviderKey = "planning_provider";
    public const string VisionProviderKey = "vision_provider";

    /// <summary>
    /// Heal <paramref name="cfg"/> in place. Returns true when any slot was rewritten.
    /// </summary>
    public static bool Apply(Dictionary<string, JsonElement> cfg)
    {
        if (cfg is null) return false;
        var changed = false;
        changed |= HealRequired(
            cfg,
            ModelCapability.Video,
            "video",
            [ProjectModelSelection.VideoConfigKey],
            "video",
            [VideoProviderKey]);
        changed |= HealRequired(
            cfg,
            ModelCapability.Image,
            "image",
            [ProjectModelSelection.ImageConfigKey],
            "image",
            [ImageProviderKey, CharacterDesignProviderKey]);
        changed |= HealRequired(
            cfg,
            ModelCapability.Chat,
            "chat",
            [ProjectModelSelection.PlanningConfigKey, ProjectModelSelection.ChatConfigKey],
            "chat",
            [PlanningProviderKey]);
        changed |= HealRequired(
            cfg,
            ModelCapability.Vision,
            "vision",
            [ProjectModelSelection.VisionConfigKey],
            "vision",
            [VisionProviderKey]);
        changed |= HealRequired(
            cfg,
            ModelCapability.Chat,
            SupportedModelCatalog.VideoReviewCapabilityId,
            [ProjectModelSelection.QualityConfigKey, VideoReviewModelKey],
            SupportedModelCatalog.VideoReviewCapabilityId,
            [QualityProviderKey]);
        changed |= HealOptional(
            cfg,
            ModelCapability.Audio,
            [ProjectModelSelection.AudioConfigKey],
            "audio");
        changed |= HealOptional(
            cfg,
            ModelCapability.Voice,
            [ProjectModelSelection.VoiceConfigKey],
            "voice");
        return changed;
    }

    private static bool HealRequired(
        Dictionary<string, JsonElement> cfg,
        ModelCapability capability,
        string defaultCapabilityId,
        string[] keys,
        string selectionsKey,
        string[] providerKeys)
    {
        var stored = ProjectModelSelection.TryGet(cfg, keys);
        if (string.IsNullOrWhiteSpace(stored)) return false;
        if (IsEnabledForCapability(stored, capability)) return false;

        var fallback = SupportedModelCatalog.DefaultModelIdForCapability(defaultCapabilityId);
        if (string.IsNullOrWhiteSpace(fallback)) return false;
        if (string.Equals(fallback, stored, StringComparison.OrdinalIgnoreCase)) return false;

        WriteRequiredSlot(cfg, keys, fallback, selectionsKey, providerKeys, capability);
        return true;
    }

    private static bool HealOptional(
        Dictionary<string, JsonElement> cfg,
        ModelCapability capability,
        string[] keys,
        string selectionsKey)
    {
        var stored = ProjectModelSelection.TryGet(cfg, keys);
        if (string.IsNullOrWhiteSpace(stored)) return false;
        if (IsEnabledForCapability(stored, capability)) return false;

        WriteOptionalNone(cfg, keys, selectionsKey);
        return true;
    }

    private static bool IsEnabledForCapability(string id, ModelCapability capability)
    {
        var entry = SupportedModelCatalog.Find(id, capability);
        return entry is { Enabled: true, Deprecated: false };
    }

    private static void WriteRequiredSlot(
        Dictionary<string, JsonElement> cfg,
        string[] keys,
        string modelId,
        string selectionsKey,
        string[] providerKeys,
        ModelCapability capability)
    {
        SetString(cfg, keys[0], modelId);
        for (var i = 1; i < keys.Length; i++)
        {
            if (cfg.ContainsKey(keys[i]))
                SetString(cfg, keys[i], modelId);
        }

        var providerId = SupportedModelCatalog.ProviderIdFor(modelId, capability);
        foreach (var providerKey in providerKeys)
        {
            if (cfg.ContainsKey(providerKey) || string.Equals(providerKey, QualityProviderKey, StringComparison.OrdinalIgnoreCase))
                SetString(cfg, providerKey, providerId);
        }

        SetSelection(cfg, selectionsKey, modelId);
    }

    private static void WriteOptionalNone(
        Dictionary<string, JsonElement> cfg,
        string[] keys,
        string selectionsKey)
    {
        foreach (var key in keys)
        {
            if (cfg.ContainsKey(key))
                SetString(cfg, key, "none");
        }

        if (cfg.ContainsKey(ModelSelectionsKey))
            SetSelection(cfg, selectionsKey, "none");
    }

    private static void SetString(Dictionary<string, JsonElement> cfg, string key, string value) =>
        cfg[key] = JsonSerializer.SerializeToElement(value);

    private static void SetSelection(Dictionary<string, JsonElement> cfg, string capKey, string modelId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.TryGetValue(ModelSelectionsKey, out var el) && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
            }
        }

        map[capKey] = modelId;
        cfg[ModelSelectionsKey] = JsonSerializer.SerializeToElement(map);
    }
}
