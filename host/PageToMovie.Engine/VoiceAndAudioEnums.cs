using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Enums

/// <summary>
/// Status states of custom voice clone profiles.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceCloneStatusState
{
    Draft,
    Training,
    Ready,
    Failed,
    Archived
}

/// <summary>
/// Gender classifications for synthesized voices.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceGenderType
{
    Male,
    Female,
    NonBinary,
    Neutral
}

/// <summary>
/// Age band spectrum categories for voice synthesis models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceAgeBandType
{
    Child,
    Teen,
    YoungAdult,
    Adult,
    MiddleAged,
    Elderly
}

/// <summary>
/// Regional accent presets for text-to-speech voice selection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceAccentPreset
{
    NeutralAmerican,
    BritishRp,
    Australian,
    MidAtlantic,
    SouthernUs,
    Scottish,
    Irish,
    Transatlantic
}

/// <summary>
/// Emotional delivery styles for TTS synthesis.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceEmotionStyle
{
    Neutral,
    Happy,
    Sad,
    Angry,
    Excited,
    Whispering,
    Dramatic,
    Fearful,
    Calm
}

/// <summary>
/// Speed rate categories for dialogue voice clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechRateCategory
{
    VerySlow,
    Slow,
    Normal,
    Fast,
    VeryFast
}

/// <summary>
/// Vocal pitch offset categories.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechPitchCategory
{
    Low,
    Normal,
    High,
    VeryHigh
}

/// <summary>
/// Audio encoding bitrate target presets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioBitratePreset
{
    Kbps64,
    Kbps128,
    Kbps192,
    Kbps256,
    Kbps320
}

/// <summary>
/// Audio sampling frequency presets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioSampleRatePreset
{
    Hz22050,
    Hz44100,
    Hz48000,
    Hz96000
}

/// <summary>
/// Audio channel layout configurations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioChannelMode
{
    Mono,
    Stereo,
    Surround51,
    Surround71
}

/// <summary>
/// Digital audio processing filter effects.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFilterEffect
{
    None,
    HighPass,
    LowPass,
    Equalizer,
    Reverb,
    Compressor,
    NoiseGate,
    DeEsser
}

/// <summary>
/// Crossfade curves for audio transitions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCrossfadeType
{
    Linear,
    ConstantPower,
    Exponential,
    Logarithmic
}

/// <summary>
/// Silence trimming modes applied to generated speech stems.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SilenceCutMode
{
    Off,
    TrimmingStartEnd,
    RemoveAllSilence,
    SmartPadding
}

/// <summary>
/// Musical arrangement style presets for background score.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicArrangementPreset
{
    Minimal,
    CinematicOrchestral,
    AmbientDrone,
    Acoustic,
    Electronic,
    Rock,
    Jazz
}

/// <summary>
/// Musical key signatures for background audio scoring.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicKeySignature
{
    CMajor,
    AMinor,
    GMajor,
    EMinor,
    FMajor,
    DMinor,
    DMajor,
    BMinor
}

/// <summary>
/// Musical time signatures for generated score tracks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicTimeSignature
{
    FourFour,
    ThreeFour,
    SixEight,
    TwoFour,
    FiveFour
}

/// <summary>
/// Audio stems split categories from multi-track audio generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicStemType
{
    Vocals,
    Drums,
    Bass,
    Melody,
    Accompaniment,
    FullMix
}

/// <summary>
/// Audio ducking levels applied to background music during dialogue.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioMixDuckingLevel
{
    Off,
    Light,
    Moderate,
    Heavy,
    Extreme
}

/// <summary>
/// Target audio loudness standards for master rendering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioLoudnessStandard
{
    EbuR128,
    AtscA85,
    ReplayGain,
    PeakNormalization
}

/// <summary>
/// Audio file container extensions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFileExtension
{
    Mp3,
    Wav,
    Flac,
    Aac,
    Ogg,
    M4a
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for voice and audio enums.
/// </summary>
public static class VoiceAndAudioEnumExtensions
{
    public static string ToApiString(this VoiceCloneStatusState val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this VoiceGenderType val) => val switch
    {
        VoiceGenderType.NonBinary => "non_binary",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this VoiceAgeBandType val) => val switch
    {
        VoiceAgeBandType.YoungAdult => "young_adult",
        VoiceAgeBandType.MiddleAged => "middle_aged",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this VoiceAccentPreset val) => val switch
    {
        VoiceAccentPreset.NeutralAmerican => "neutral_american",
        VoiceAccentPreset.BritishRp => "british_rp",
        VoiceAccentPreset.MidAtlantic => "mid_atlantic",
        VoiceAccentPreset.SouthernUs => "southern_us",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this VoiceEmotionStyle val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this SpeechRateCategory val) => val switch
    {
        SpeechRateCategory.VerySlow => "very_slow",
        SpeechRateCategory.VeryFast => "very_fast",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this SpeechPitchCategory val) => val switch
    {
        SpeechPitchCategory.VeryHigh => "very_high",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AudioBitratePreset val) => val switch
    {
        AudioBitratePreset.Kbps64 => "64k",
        AudioBitratePreset.Kbps128 => "128k",
        AudioBitratePreset.Kbps192 => "192k",
        AudioBitratePreset.Kbps256 => "256k",
        AudioBitratePreset.Kbps320 => "320k",
        _ => "192k"
    };
    public static string ToApiString(this AudioSampleRatePreset val) => val switch
    {
        AudioSampleRatePreset.Hz22050 => "22050hz",
        AudioSampleRatePreset.Hz44100 => "44100hz",
        AudioSampleRatePreset.Hz48000 => "48000hz",
        AudioSampleRatePreset.Hz96000 => "96000hz",
        _ => "44100hz"
    };
    public static string ToApiString(this AudioChannelMode val) => val switch
    {
        AudioChannelMode.Surround51 => "surround_51",
        AudioChannelMode.Surround71 => "surround_71",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AudioFilterEffect val) => val switch
    {
        AudioFilterEffect.HighPass => "high_pass",
        AudioFilterEffect.LowPass => "low_pass",
        AudioFilterEffect.NoiseGate => "noise_gate",
        AudioFilterEffect.DeEsser => "de_esser",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AudioCrossfadeType val) => val switch
    {
        AudioCrossfadeType.ConstantPower => "constant_power",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this SilenceCutMode val) => val switch
    {
        SilenceCutMode.TrimmingStartEnd => "trimming_start_end",
        SilenceCutMode.RemoveAllSilence => "remove_all_silence",
        SilenceCutMode.SmartPadding => "smart_padding",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this MusicArrangementPreset val) => val switch
    {
        MusicArrangementPreset.CinematicOrchestral => "cinematic_orchestral",
        MusicArrangementPreset.AmbientDrone => "ambient_drone",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this MusicKeySignature val) => val switch
    {
        MusicKeySignature.CMajor => "c_major",
        MusicKeySignature.AMinor => "a_minor",
        MusicKeySignature.GMajor => "g_major",
        MusicKeySignature.EMinor => "e_minor",
        MusicKeySignature.FMajor => "f_major",
        MusicKeySignature.DMinor => "d_minor",
        MusicKeySignature.DMajor => "d_major",
        MusicKeySignature.BMinor => "b_minor",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this MusicTimeSignature val) => val switch
    {
        MusicTimeSignature.FourFour => "4/4",
        MusicTimeSignature.ThreeFour => "3/4",
        MusicTimeSignature.SixEight => "6/8",
        MusicTimeSignature.TwoFour => "2/4",
        MusicTimeSignature.FiveFour => "5/4",
        _ => "4/4"
    };
    public static string ToApiString(this MusicStemType val) => val switch
    {
        MusicStemType.FullMix => "full_mix",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AudioMixDuckingLevel val) => val.ToString().ToLowerInvariant();
    public static string ToApiString(this AudioLoudnessStandard val) => val switch
    {
        AudioLoudnessStandard.EbuR128 => "ebu_r128",
        AudioLoudnessStandard.AtscA85 => "atsc_a85",
        AudioLoudnessStandard.ReplayGain => "replay_gain",
        AudioLoudnessStandard.PeakNormalization => "peak_normalization",
        _ => val.ToString().ToLowerInvariant()
    };
    public static string ToApiString(this AudioFileExtension val) => val.ToString().ToLowerInvariant();
    public static VoiceCloneStatusState ParseVoiceCloneStatusState(string? s, VoiceCloneStatusState defaultValue = VoiceCloneStatusState.Draft)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<VoiceCloneStatusState>(s, true, out var r) ? r : defaultValue;
    }
    public static VoiceCloneStatusState ToVoiceCloneStatusState(this string? s, VoiceCloneStatusState defaultValue = VoiceCloneStatusState.Draft) => ParseVoiceCloneStatusState(s, defaultValue);

    public static VoiceGenderType ParseVoiceGenderType(string? s, VoiceGenderType defaultValue = VoiceGenderType.Neutral)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<VoiceGenderType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static VoiceGenderType ToVoiceGenderType(this string? s, VoiceGenderType defaultValue = VoiceGenderType.Neutral) => ParseVoiceGenderType(s, defaultValue);

    public static VoiceAgeBandType ParseVoiceAgeBandType(string? s, VoiceAgeBandType defaultValue = VoiceAgeBandType.Adult)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<VoiceAgeBandType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static VoiceAgeBandType ToVoiceAgeBandType(this string? s, VoiceAgeBandType defaultValue = VoiceAgeBandType.Adult) => ParseVoiceAgeBandType(s, defaultValue);

    public static VoiceAccentPreset ParseVoiceAccentPreset(string? s, VoiceAccentPreset defaultValue = VoiceAccentPreset.NeutralAmerican)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<VoiceAccentPreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static VoiceAccentPreset ToVoiceAccentPreset(this string? s, VoiceAccentPreset defaultValue = VoiceAccentPreset.NeutralAmerican) => ParseVoiceAccentPreset(s, defaultValue);

    public static VoiceEmotionStyle ParseVoiceEmotionStyle(string? s, VoiceEmotionStyle defaultValue = VoiceEmotionStyle.Neutral)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<VoiceEmotionStyle>(s, true, out var r) ? r : defaultValue;
    }
    public static VoiceEmotionStyle ToVoiceEmotionStyle(this string? s, VoiceEmotionStyle defaultValue = VoiceEmotionStyle.Neutral) => ParseVoiceEmotionStyle(s, defaultValue);

    public static SpeechRateCategory ParseSpeechRateCategory(string? s, SpeechRateCategory defaultValue = SpeechRateCategory.Normal)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<SpeechRateCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static SpeechRateCategory ToSpeechRateCategory(this string? s, SpeechRateCategory defaultValue = SpeechRateCategory.Normal) => ParseSpeechRateCategory(s, defaultValue);

    public static SpeechPitchCategory ParseSpeechPitchCategory(string? s, SpeechPitchCategory defaultValue = SpeechPitchCategory.Normal)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<SpeechPitchCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static SpeechPitchCategory ToSpeechPitchCategory(this string? s, SpeechPitchCategory defaultValue = SpeechPitchCategory.Normal) => ParseSpeechPitchCategory(s, defaultValue);

    public static AudioBitratePreset ParseAudioBitratePreset(string? s, AudioBitratePreset defaultValue = AudioBitratePreset.Kbps192)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioBitratePreset>(s.Replace("k", "Kbps").Replace("K", "Kbps"), true, out var r) ? r : defaultValue;
    }
    public static AudioBitratePreset ToAudioBitratePreset(this string? s, AudioBitratePreset defaultValue = AudioBitratePreset.Kbps192) => ParseAudioBitratePreset(s, defaultValue);

    public static AudioSampleRatePreset ParseAudioSampleRatePreset(string? s, AudioSampleRatePreset defaultValue = AudioSampleRatePreset.Hz44100)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioSampleRatePreset>(s.Replace("hz", "").Replace("Hz", "Hz"), true, out var r) ? r : defaultValue;
    }
    public static AudioSampleRatePreset ToAudioSampleRatePreset(this string? s, AudioSampleRatePreset defaultValue = AudioSampleRatePreset.Hz44100) => ParseAudioSampleRatePreset(s, defaultValue);

    public static AudioChannelMode ParseAudioChannelMode(string? s, AudioChannelMode defaultValue = AudioChannelMode.Stereo)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioChannelMode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static AudioChannelMode ToAudioChannelMode(this string? s, AudioChannelMode defaultValue = AudioChannelMode.Stereo) => ParseAudioChannelMode(s, defaultValue);

    public static AudioFilterEffect ParseAudioFilterEffect(string? s, AudioFilterEffect defaultValue = AudioFilterEffect.None)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioFilterEffect>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static AudioFilterEffect ToAudioFilterEffect(this string? s, AudioFilterEffect defaultValue = AudioFilterEffect.None) => ParseAudioFilterEffect(s, defaultValue);

    public static AudioCrossfadeType ParseAudioCrossfadeType(string? s, AudioCrossfadeType defaultValue = AudioCrossfadeType.Linear)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioCrossfadeType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static AudioCrossfadeType ToAudioCrossfadeType(this string? s, AudioCrossfadeType defaultValue = AudioCrossfadeType.Linear) => ParseAudioCrossfadeType(s, defaultValue);

    public static SilenceCutMode ParseSilenceCutMode(string? s, SilenceCutMode defaultValue = SilenceCutMode.Off)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<SilenceCutMode>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static SilenceCutMode ToSilenceCutMode(this string? s, SilenceCutMode defaultValue = SilenceCutMode.Off) => ParseSilenceCutMode(s, defaultValue);

    public static MusicArrangementPreset ParseMusicArrangementPreset(string? s, MusicArrangementPreset defaultValue = MusicArrangementPreset.CinematicOrchestral)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<MusicArrangementPreset>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static MusicArrangementPreset ToMusicArrangementPreset(this string? s, MusicArrangementPreset defaultValue = MusicArrangementPreset.CinematicOrchestral) => ParseMusicArrangementPreset(s, defaultValue);

    public static MusicKeySignature ParseMusicKeySignature(string? s, MusicKeySignature defaultValue = MusicKeySignature.CMajor)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<MusicKeySignature>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static MusicKeySignature ToMusicKeySignature(this string? s, MusicKeySignature defaultValue = MusicKeySignature.CMajor) => ParseMusicKeySignature(s, defaultValue);

    public static MusicTimeSignature ParseMusicTimeSignature(string? s, MusicTimeSignature defaultValue = MusicTimeSignature.FourFour)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return s.Trim() switch
        {
            "4/4" or "four_four" => MusicTimeSignature.FourFour,
            "3/4" or "three_four" => MusicTimeSignature.ThreeFour,
            "6/8" or "six_eight" => MusicTimeSignature.SixEight,
            "2/4" or "two_four" => MusicTimeSignature.TwoFour,
            "5/4" or "five_four" => MusicTimeSignature.FiveFour,
            _ => Enum.TryParse<MusicTimeSignature>(s.Replace("_", ""), true, out var r) ? r : defaultValue
        };
    }
    public static MusicTimeSignature ToMusicTimeSignature(this string? s, MusicTimeSignature defaultValue = MusicTimeSignature.FourFour) => ParseMusicTimeSignature(s, defaultValue);

    public static MusicStemType ParseMusicStemType(string? s, MusicStemType defaultValue = MusicStemType.FullMix)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<MusicStemType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static MusicStemType ToMusicStemType(this string? s, MusicStemType defaultValue = MusicStemType.FullMix) => ParseMusicStemType(s, defaultValue);

    public static AudioMixDuckingLevel ParseAudioMixDuckingLevel(string? s, AudioMixDuckingLevel defaultValue = AudioMixDuckingLevel.Moderate)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioMixDuckingLevel>(s, true, out var r) ? r : defaultValue;
    }
    public static AudioMixDuckingLevel ToAudioMixDuckingLevel(this string? s, AudioMixDuckingLevel defaultValue = AudioMixDuckingLevel.Moderate) => ParseAudioMixDuckingLevel(s, defaultValue);

    public static AudioLoudnessStandard ParseAudioLoudnessStandard(string? s, AudioLoudnessStandard defaultValue = AudioLoudnessStandard.EbuR128)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioLoudnessStandard>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    }
    public static AudioLoudnessStandard ToAudioLoudnessStandard(this string? s, AudioLoudnessStandard defaultValue = AudioLoudnessStandard.EbuR128) => ParseAudioLoudnessStandard(s, defaultValue);

    public static AudioFileExtension ParseAudioFileExtension(string? s, AudioFileExtension defaultValue = AudioFileExtension.Mp3)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AudioFileExtension>(s.TrimStart('.'), true, out var r) ? r : defaultValue;
    }
    public static AudioFileExtension ToAudioFileExtension(this string? s, AudioFileExtension defaultValue = AudioFileExtension.Mp3) => ParseAudioFileExtension(s, defaultValue);
}

#endregion
