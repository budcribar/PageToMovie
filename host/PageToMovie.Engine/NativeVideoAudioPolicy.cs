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

    /// <summary>
    /// A role bundle can pair a generate model that bakes character voices into the video with an
    /// extend model that cannot. Extending a SPEAKING clip across that pair gives the same
    /// character a native voice on one clip and a dubbed voice on the next, and the native track
    /// cannot be removed afterwards. Generate such a clip fresh instead — continuity is worth less
    /// than one voice per character. Silent clips still extend normally.
    /// </summary>
    public static bool ExtendWouldDropNativeVoices(VideoModelRoles roles, bool clipHasSpokenLines)
    {
        if (!clipHasSpokenLines)
            return false;
        if (roles.Extend is null)
            return false;
        if (!roles.Generate.SupportsReferenceAudios)
            return false;
        if (roles.Generate.PresetVoices is not { Count: > 0 })
            return false;
        return !roles.Extend.SupportsReferenceAudios;
    }
}
