using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// When the wire model writes a native audio track (catalog
/// <c>supportsReferenceAudios</c>), do not also run TTS / voice-clone overlay
/// on that take. Extend-sibling hops use the extend-role catalog row.
/// </summary>
public static class NativeVideoAudioPolicy
{
    public static bool HasNativeAudioTrack(string? wireModelId)
    {
        if (string.IsNullOrWhiteSpace(wireModelId))
            return false;
        var entry = SupportedModelCatalog.Find(wireModelId.Trim(), ModelCapability.Video);
        return entry is { SupportsReferenceAudios: true };
    }

    public static bool ShouldSkipVoiceOverlay(string? takeWireModelId) =>
        HasNativeAudioTrack(takeWireModelId);
}
