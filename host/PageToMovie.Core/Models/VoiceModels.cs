using System;
using System.Collections.Generic;

namespace PageToMovie.Core.Models;

/// <summary>
/// Domain models for character voice profiles, voice clone state, and TTS configurations.
/// </summary>
public sealed class VoiceProfileDetails
{
    public string CharKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string VoiceProfile { get; set; } = "";
    public string VoiceLabel { get; set; } = "";
    public VoiceGender Gender { get; set; } = VoiceGender.Neutral;
    public VoiceAgeBand AgeBand { get; set; } = VoiceAgeBand.Adult;
    public VoiceCloneStatus Status { get; set; } = VoiceCloneStatus.Ready;
    public SpeechSubstitutionMode SubstitutionMode { get; set; } = SpeechSubstitutionMode.Narrator;
}

public sealed class VoiceCloneStatusItem
{
    public string CharKey { get; set; } = "";
    public string? VoiceProvider { get; set; }
    public string? VoiceProviderVoiceId { get; set; }
    public VoiceCloneStatus Status { get; set; } = VoiceCloneStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}
