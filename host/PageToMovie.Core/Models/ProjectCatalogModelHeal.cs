using System.Text.Json;

namespace PageToMovie.Core.Models;

/// <summary>
/// Validate project config model slots against the enabled catalog. A stored video
/// id that is in the catalog but disabled or deprecated is replaced with the video
/// capability default (<see cref="SupportedModelCatalog.DefaultModelIdForCapability(string)"/>).
/// Unknown ids, empty required slots, and every other capability still fail fast —
/// missing or unknown ids are not rewritten, and a disabled image id is not rewritten.
/// Optional slots (audio, voice, video-review) may stay unset or <c>none</c>; an unknown
/// non-empty id still fails.
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
    /// Validate <paramref name="cfg"/> in place. Returns true when a disabled or
    /// deprecated video id was replaced with the video capability default.
    /// Throws <see cref="InvalidOperationException"/> when a present required slot
    /// is empty or unknown, or a present optional slot has an unknown non-empty id.
    /// A known-but-disabled image (or other non-video) id still throws and is not rewritten.
    /// </summary>
    public static bool Apply(Dictionary<string, JsonElement> cfg)
    {
        if (cfg is null) return false;
        var changed = TryReplaceDisabledVideo(cfg);
        ValidateRequired(
            cfg,
            ModelCapability.Video,
            "video",
            [ProjectModelSelection.VideoConfigKey]);
        ValidateRequired(
            cfg,
            ModelCapability.Image,
            "image",
            [ProjectModelSelection.ImageConfigKey]);
        ValidateRequired(
            cfg,
            ModelCapability.Chat,
            "chat",
            [ProjectModelSelection.PlanningConfigKey, ProjectModelSelection.ChatConfigKey]);
        ValidateRequired(
            cfg,
            ModelCapability.Vision,
            "vision",
            [ProjectModelSelection.VisionConfigKey]);
        ValidateOptional(
            cfg,
            ModelCapability.Chat,
            SupportedModelCatalog.VideoReviewCapabilityId,
            [ProjectModelSelection.QualityConfigKey, VideoReviewModelKey]);
        ValidateOptional(
            cfg,
            ModelCapability.Audio,
            "audio",
            [ProjectModelSelection.AudioConfigKey]);
        ValidateOptional(
            cfg,
            ModelCapability.Voice,
            "voice",
            [ProjectModelSelection.VoiceConfigKey]);
        ValidateOptional(
            cfg,
            ModelCapability.VideoEdit,
            "video-edit",
            [ProjectModelSelection.VideoEditConfigKey]);
        return changed;
    }

    /// <summary>
    /// Replace a stored video model that exists in the catalog but is disabled or
    /// deprecated with the video capability default. Unknown and empty ids are left
    /// unchanged so callers still fail fast. Also updates <c>model_selections.video</c>
    /// and, when <c>model_name</c> changed, <c>video_provider</c> when that key is present.
    /// </summary>
    public static bool TryReplaceDisabledVideo(Dictionary<string, JsonElement> cfg)
    {
        if (cfg is null) return false;

        var capability = ModelCapability.Video;
        var capabilityId = ProjectModelSelection.CatalogCapabilityId(capability);
        var changed = false;

        if (cfg.ContainsKey(ProjectModelSelection.VideoConfigKey)
            && TryCapabilityDefaultReplacement(ReadString(cfg, ProjectModelSelection.VideoConfigKey), capability, out var videoReplacement))
        {
            cfg[ProjectModelSelection.VideoConfigKey] = JsonSerializer.SerializeToElement(videoReplacement);
            changed = true;
            if (cfg.ContainsKey(VideoProviderKey))
            {
                var provider = SupportedModelCatalog.ProviderIdFor(videoReplacement, capability);
                if (!string.IsNullOrWhiteSpace(provider))
                    cfg[VideoProviderKey] = JsonSerializer.SerializeToElement(provider);
            }
        }

        if (TryReplaceSelection(cfg, capabilityId, capability))
            changed = true;

        return changed;
    }

    private static bool TryReplaceSelection(
        Dictionary<string, JsonElement> cfg,
        string capabilityId,
        ModelCapability capability)
    {
        if (!cfg.TryGetValue(ModelSelectionsKey, out var el) || el.ValueKind != JsonValueKind.Object)
            return false;

        string? stored = null;
        var found = false;
        foreach (var property in el.EnumerateObject())
        {
            if (!property.Name.Equals(capabilityId, StringComparison.OrdinalIgnoreCase))
                continue;
            found = true;
            stored = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            break;
        }

        if (!found || !TryCapabilityDefaultReplacement(stored, capability, out var replacement))
            return false;

        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in el.EnumerateObject())
        {
            map[property.Name] = property.Name.Equals(capabilityId, StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.SerializeToElement(replacement)
                : property.Value.Clone();
        }

        cfg[ModelSelectionsKey] = JsonSerializer.SerializeToElement(map);
        return true;
    }

    /// <summary>
    /// True when <paramref name="stored"/> is a catalog row for <paramref name="capability"/>
    /// that is disabled or deprecated, and the capability default is a different id.
    /// </summary>
    private static bool TryCapabilityDefaultReplacement(string? stored, ModelCapability capability, out string replacement)
    {
        replacement = "";
        if (!ProjectModelSelection.IsUsableModelId(stored))
            return false;

        var entry = SupportedModelCatalog.Find(stored, capability);
        if (entry is null || (entry.Enabled && !entry.Deprecated))
            return false;

        var capabilityId = ProjectModelSelection.CatalogCapabilityId(capability);
        var def = SupportedModelCatalog.DefaultModelIdForCapability(capabilityId);
        if (string.IsNullOrWhiteSpace(def)
            || string.Equals(def, entry.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        replacement = def;
        return true;
    }

    private static string? ReadString(Dictionary<string, JsonElement> cfg, string key)
    {
        if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void ValidateRequired(
        Dictionary<string, JsonElement> cfg,
        ModelCapability capability,
        string capabilityId,
        string[] keys)
    {
        if (!SlotPresent(cfg, keys)) return;

        var stored = ProjectModelSelection.TryGet(cfg, keys);
        if (string.IsNullOrWhiteSpace(stored))
            throw new InvalidOperationException(ProjectModelSelection.FormatMissingModel(capabilityId));
        if (IsEnabledForCapability(stored, capability)) return;

        throw new InvalidOperationException(ProjectModelSelection.FormatUnknownModel(capabilityId, stored));
    }

    private static void ValidateOptional(
        Dictionary<string, JsonElement> cfg,
        ModelCapability capability,
        string capabilityId,
        string[] keys)
    {
        if (!SlotPresent(cfg, keys)) return;

        var stored = ProjectModelSelection.TryGet(cfg, keys);
        if (string.IsNullOrWhiteSpace(stored)) return;
        if (IsEnabledForCapability(stored, capability)) return;

        throw new InvalidOperationException(ProjectModelSelection.FormatUnknownModel(capabilityId, stored));
    }

    private static bool SlotPresent(Dictionary<string, JsonElement> cfg, string[] keys) =>
        keys.Any(cfg.ContainsKey);

    private static bool IsEnabledForCapability(string id, ModelCapability capability)
    {
        var entry = SupportedModelCatalog.Find(id, capability);
        return entry is { Enabled: true, Deprecated: false };
    }
}
