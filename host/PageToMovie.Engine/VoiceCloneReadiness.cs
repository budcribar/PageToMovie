using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>Operator-facing "key missing" text for a catalog voice provider.</summary>
public static class VoiceCloneReadiness
{
    public static string NotReadyMessage(SupportedModelEntry? entry)
    {
        if (entry?.Provider == ModelProviderFamily.Xai)
            return "xAI is not configured. Save the xAI key in Settings. This voice option is English.";
        if (entry?.Provider == ModelProviderFamily.Fal)
            return "Voice provider (Fal) is not configured.";
        if (entry is null || string.IsNullOrWhiteSpace(entry.ProviderLabel))
            return "Voice provider is not configured.";
        return entry.ProviderLabel + " is not configured.";
    }
}
