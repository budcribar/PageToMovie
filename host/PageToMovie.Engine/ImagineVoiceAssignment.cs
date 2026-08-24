using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Assign and persist an Imagine preset voice on a character seed (same cast_seeds store).
/// Picks from the generate-role catalog roster on first need.
/// </summary>
public static class ImagineVoiceAssignment
{
    public static IReadOnlyList<PresetVoiceEntry> RosterForProjectVideo(string? videoModelId) =>
        SupportedModelCatalog.GenerateRolePresetVoices(videoModelId);

    public static string? Ensure(
        ProjectStore store,
        string projectId,
        string charKey,
        IReadOnlyList<PresetVoiceEntry> roster,
        ImagineVoicePicker.VoiceHints hints,
        string? existingVoiceId = null)
    {
        if (roster.Count == 0 || string.IsNullOrWhiteSpace(charKey))
            return ImagineVoicePicker.NormalizeVoiceId(roster, existingVoiceId);

        var current = ImagineVoicePicker.NormalizeVoiceId(roster, existingVoiceId);
        if (current is not null)
            return current;

        var picked = ImagineVoicePicker.Pick(roster, hints);
        if (picked is null)
            return null;
        store.UpdateCharacterSeedText(projectId, charKey, imagineVoiceId: picked);
        return picked;
    }

    public static ImagineVoicePicker.VoiceHints HintsFromProfile(
        ClipVideoPromptBuilder.CharacterProfile? profile) =>
        new(
            profile?.Gender,
            profile?.AgeBand,
            profile?.VoiceProfile,
            profile?.LabelOrDisplay(),
            Temperament: null);

    public static ImagineVoicePicker.VoiceHints HintsFromSummary(CharacterSummary? c) =>
        new(
            c?.Gender.ToString(),
            c?.AgeBand?.ToString(),
            c?.VoiceProfile,
            c?.VoiceLabel ?? c?.DisplayName,
            Temperament: null);
}

internal static class CharacterProfileVoiceHints
{
    public static string? LabelOrDisplay(this ClipVideoPromptBuilder.CharacterProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.VoiceLabel) ? profile.VoiceLabel : profile.DisplayName;
}
