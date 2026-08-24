using System.Text.Json;

namespace PageToMovie.Core.Models;

/// <summary>
/// Validate project config model slots against the enabled catalog. Never writes a
/// replacement id — a missing or unknown id must fail fast so Settings stays the
/// single source of truth. Required slots (video, image, chat, vision) fail when
/// a stored value is empty/whitespace or not enabled in the catalog. Optional
/// slots (audio, voice, video-review) may stay unset or <c>none</c>; an unknown
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
    /// Validate <paramref name="cfg"/> in place. Never rewrites slots (always
    /// returns false). Throws <see cref="InvalidOperationException"/> when a
    /// present required slot is empty/unknown or a present optional slot has an
    /// unknown non-empty id.
    /// </summary>
    public static bool Apply(Dictionary<string, JsonElement> cfg)
    {
        if (cfg is null) return false;
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
        return false;
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
