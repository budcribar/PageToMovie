using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

#region Enums

/// <summary>
/// Extended custom voice clone state kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceCloneStateKind
{
    Draft,
    Training,
    Ready,
    Failed,
    Archived,
    Processing,
    Verifying,
    QuotaExceeded
}

/// <summary>
/// Extended gender spectrum categories for synthesized voices.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceGenderCategory
{
    Male,
    Female,
    NonBinary,
    Neutral,
    ChildMale,
    ChildFemale,
    Androgynous
}

/// <summary>
/// Extended age group spectrum categories for vocal models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceAgeGroupCategory
{
    Child,
    Teen,
    YoungAdult,
    Adult,
    MiddleAged,
    Elderly,
    Senior
}

/// <summary>
/// Extended regional accent preset kinds for speech synthesis.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceAccentPresetKind
{
    NeutralAmerican,
    BritishRp,
    Australian,
    MidAtlantic,
    SouthernUs,
    Scottish,
    Irish,
    Transatlantic,
    Canadian,
    French,
    German,
    Spanish,
    Indian,
    Japanese
}

/// <summary>
/// Extended emotional delivery style kinds for speech models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceEmotionStyleKind
{
    Neutral,
    Happy,
    Sad,
    Angry,
    Excited,
    Whispering,
    Dramatic,
    Fearful,
    Calm,
    Sarcastic,
    Nostalgic,
    Hopeful,
    Suspicious
}

/// <summary>
/// Extended speech rate categories for voice generation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechRateCategoryKind
{
    VerySlow,
    Slow,
    Normal,
    Fast,
    VeryFast,
    UltraFast
}

/// <summary>
/// Extended vocal pitch category offsets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeechPitchCategoryKind
{
    Deep,
    Low,
    Normal,
    High,
    VeryHigh,
    UltraHigh
}

/// <summary>
/// Extended target bitrate preset kinds for audio encoding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioBitratePresetKind
{
    Kbps64,
    Kbps96,
    Kbps128,
    Kbps160,
    Kbps192,
    Kbps256,
    Kbps320,
    Lossless
}

/// <summary>
/// Extended sample rate preset kinds for digital audio rendering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioSampleRatePresetKind
{
    Hz16000,
    Hz22050,
    Hz32000,
    Hz44100,
    Hz48000,
    Hz88200,
    Hz96000,
    Hz192000
}

/// <summary>
/// Extended channel mode layouts for multi-channel audio tracks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioChannelModeKind
{
    Mono,
    Stereo,
    JointStereo,
    Surround51,
    Surround71,
    Binaural,
    Ambisonic
}

/// <summary>
/// Extended digital audio filter processing effect kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFilterEffectKind
{
    None,
    HighPass,
    LowPass,
    Equalizer,
    Reverb,
    Compressor,
    NoiseGate,
    DeEsser,
    Chorus,
    Flanger,
    PitchShift,
    Delay
}

/// <summary>
/// Extended crossfade type curves for smooth transition mixing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCrossfadeTypeKind
{
    Linear,
    ConstantPower,
    Exponential,
    Logarithmic,
    SCurve,
    EqualGain
}

/// <summary>
/// Extended silence cut mode kinds for speech stem trimming.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SilenceCutModeKind
{
    Off,
    TrimmingStartEnd,
    RemoveAllSilence,
    SmartPadding,
    ThresholdBased,
    DynamicGapping
}

/// <summary>
/// Extended music arrangement style preset kinds for musical scoring.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicArrangementPresetKind
{
    Minimal,
    CinematicOrchestral,
    AmbientDrone,
    Acoustic,
    Electronic,
    Rock,
    Jazz,
    Synthwave,
    Classical,
    HybridScore,
    Choral,
    EpicTrailer
}

/// <summary>
/// Extended musical key signature kinds for soundtrack composition.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicKeySignatureKind
{
    CMajor,
    AMinor,
    GMajor,
    EMinor,
    FMajor,
    DMinor,
    DMajor,
    BMinor,
    EMajor,
    CSharpMinor,
    AbMajor,
    FMinor
}

/// <summary>
/// Extended musical time signature kinds for score rhythm.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicTimeSignatureKind
{
    FourFour,
    ThreeFour,
    SixEight,
    TwoFour,
    FiveFour,
    SevenEight,
    TwelveEight
}

/// <summary>
/// Extended music stem category kinds from multi-track splits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicStemTypeKind
{
    Vocals,
    Drums,
    Bass,
    Melody,
    Accompaniment,
    FullMix,
    Percussion,
    Synths,
    FX
}

/// <summary>
/// Extended ducking level kinds applied during dialogue playback.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioMixDuckingLevelKind
{
    Off,
    Light,
    Moderate,
    Heavy,
    Extreme,
    SidechainAuto
}

/// <summary>
/// Extended target loudness standard kinds for master mixing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioLoudnessStandardKind
{
    EbuR128,
    AtscA85,
    ReplayGain,
    PeakNormalization,
    TruePeak,
    SpotifyStandard,
    YoutubeStandard
}

/// <summary>
/// Extended audio container file extension kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFileExtensionKind
{
    Mp3,
    Wav,
    Flac,
    Aac,
    Ogg,
    M4a,
    Opus,
    Wma,
    Aiff
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods and string parsers for extended voice and music enums.
/// </summary>
public static class VoiceAndMusicExtendedEnumExtensions
{
    public static string ToApiString(this VoiceCloneStateKind val) => val switch
    {
        VoiceCloneStateKind.QuotaExceeded => "quota_exceeded",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VoiceCloneStateKind ParseVoiceCloneStateKind(string? s, VoiceCloneStateKind defaultValue = VoiceCloneStateKind.Draft) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceCloneStateKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VoiceCloneStateKind ToVoiceCloneStateKind(this string? s, VoiceCloneStateKind defaultValue = VoiceCloneStateKind.Draft) => ParseVoiceCloneStateKind(s, defaultValue);

    public static string ToApiString(this VoiceGenderCategory val) => val switch
    {
        VoiceGenderCategory.NonBinary => "non_binary",
        VoiceGenderCategory.ChildMale => "child_male",
        VoiceGenderCategory.ChildFemale => "child_female",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VoiceGenderCategory ParseVoiceGenderCategory(string? s, VoiceGenderCategory defaultValue = VoiceGenderCategory.Neutral) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceGenderCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VoiceGenderCategory ToVoiceGenderCategory(this string? s, VoiceGenderCategory defaultValue = VoiceGenderCategory.Neutral) => ParseVoiceGenderCategory(s, defaultValue);

    public static string ToApiString(this VoiceAgeGroupCategory val) => val switch
    {
        VoiceAgeGroupCategory.YoungAdult => "young_adult",
        VoiceAgeGroupCategory.MiddleAged => "middle_aged",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VoiceAgeGroupCategory ParseVoiceAgeGroupCategory(string? s, VoiceAgeGroupCategory defaultValue = VoiceAgeGroupCategory.Adult) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceAgeGroupCategory>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VoiceAgeGroupCategory ToVoiceAgeGroupCategory(this string? s, VoiceAgeGroupCategory defaultValue = VoiceAgeGroupCategory.Adult) => ParseVoiceAgeGroupCategory(s, defaultValue);

    public static string ToApiString(this VoiceAccentPresetKind val) => val switch
    {
        VoiceAccentPresetKind.NeutralAmerican => "neutral_american",
        VoiceAccentPresetKind.BritishRp => "british_rp",
        VoiceAccentPresetKind.MidAtlantic => "mid_atlantic",
        VoiceAccentPresetKind.SouthernUs => "southern_us",
        _ => val.ToString().ToLowerInvariant()
    };
    public static VoiceAccentPresetKind ParseVoiceAccentPresetKind(string? s, VoiceAccentPresetKind defaultValue = VoiceAccentPresetKind.NeutralAmerican) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceAccentPresetKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static VoiceAccentPresetKind ToVoiceAccentPresetKind(this string? s, VoiceAccentPresetKind defaultValue = VoiceAccentPresetKind.NeutralAmerican) => ParseVoiceAccentPresetKind(s, defaultValue);

    public static string ToApiString(this VoiceEmotionStyleKind val) => val.ToString().ToLowerInvariant();
    public static VoiceEmotionStyleKind ParseVoiceEmotionStyleKind(string? s, VoiceEmotionStyleKind defaultValue = VoiceEmotionStyleKind.Neutral) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<VoiceEmotionStyleKind>(s, true, out var r) ? r : defaultValue;
    public static VoiceEmotionStyleKind ToVoiceEmotionStyleKind(this string? s, VoiceEmotionStyleKind defaultValue = VoiceEmotionStyleKind.Neutral) => ParseVoiceEmotionStyleKind(s, defaultValue);

    public static string ToApiString(this SpeechRateCategoryKind val) => val switch
    {
        SpeechRateCategoryKind.VerySlow => "very_slow",
        SpeechRateCategoryKind.VeryFast => "very_fast",
        SpeechRateCategoryKind.UltraFast => "ultra_fast",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SpeechRateCategoryKind ParseSpeechRateCategoryKind(string? s, SpeechRateCategoryKind defaultValue = SpeechRateCategoryKind.Normal) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SpeechRateCategoryKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SpeechRateCategoryKind ToSpeechRateCategoryKind(this string? s, SpeechRateCategoryKind defaultValue = SpeechRateCategoryKind.Normal) => ParseSpeechRateCategoryKind(s, defaultValue);

    public static string ToApiString(this SpeechPitchCategoryKind val) => val switch
    {
        SpeechPitchCategoryKind.VeryHigh => "very_high",
        SpeechPitchCategoryKind.UltraHigh => "ultra_high",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SpeechPitchCategoryKind ParseSpeechPitchCategoryKind(string? s, SpeechPitchCategoryKind defaultValue = SpeechPitchCategoryKind.Normal) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SpeechPitchCategoryKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SpeechPitchCategoryKind ToSpeechPitchCategoryKind(this string? s, SpeechPitchCategoryKind defaultValue = SpeechPitchCategoryKind.Normal) => ParseSpeechPitchCategoryKind(s, defaultValue);

    public static string ToApiString(this AudioBitratePresetKind val) => val switch
    {
        AudioBitratePresetKind.Kbps64 => "64k",
        AudioBitratePresetKind.Kbps96 => "96k",
        AudioBitratePresetKind.Kbps128 => "128k",
        AudioBitratePresetKind.Kbps160 => "160k",
        AudioBitratePresetKind.Kbps192 => "192k",
        AudioBitratePresetKind.Kbps256 => "256k",
        AudioBitratePresetKind.Kbps320 => "320k",
        AudioBitratePresetKind.Lossless => "lossless",
        _ => "192k"
    };
    public static AudioBitratePresetKind ParseAudioBitratePresetKind(string? s, AudioBitratePresetKind defaultValue = AudioBitratePresetKind.Kbps192) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioBitratePresetKind>(s.Replace("k", "Kbps").Replace("K", "Kbps"), true, out var r) ? r : defaultValue;
    public static AudioBitratePresetKind ToAudioBitratePresetKind(this string? s, AudioBitratePresetKind defaultValue = AudioBitratePresetKind.Kbps192) => ParseAudioBitratePresetKind(s, defaultValue);

    public static string ToApiString(this AudioSampleRatePresetKind val) => val switch
    {
        AudioSampleRatePresetKind.Hz16000 => "16000hz",
        AudioSampleRatePresetKind.Hz22050 => "22050hz",
        AudioSampleRatePresetKind.Hz32000 => "32000hz",
        AudioSampleRatePresetKind.Hz44100 => "44100hz",
        AudioSampleRatePresetKind.Hz48000 => "48000hz",
        AudioSampleRatePresetKind.Hz88200 => "88200hz",
        AudioSampleRatePresetKind.Hz96000 => "96000hz",
        AudioSampleRatePresetKind.Hz192000 => "192000hz",
        _ => "44100hz"
    };
    public static AudioSampleRatePresetKind ParseAudioSampleRatePresetKind(string? s, AudioSampleRatePresetKind defaultValue = AudioSampleRatePresetKind.Hz44100) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioSampleRatePresetKind>(s.Replace("hz", "").Replace("Hz", "Hz"), true, out var r) ? r : defaultValue;
    public static AudioSampleRatePresetKind ToAudioSampleRatePresetKind(this string? s, AudioSampleRatePresetKind defaultValue = AudioSampleRatePresetKind.Hz44100) => ParseAudioSampleRatePresetKind(s, defaultValue);

    public static string ToApiString(this AudioChannelModeKind val) => val switch
    {
        AudioChannelModeKind.JointStereo => "joint_stereo",
        AudioChannelModeKind.Surround51 => "surround_51",
        AudioChannelModeKind.Surround71 => "surround_71",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AudioChannelModeKind ParseAudioChannelModeKind(string? s, AudioChannelModeKind defaultValue = AudioChannelModeKind.Stereo) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioChannelModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AudioChannelModeKind ToAudioChannelModeKind(this string? s, AudioChannelModeKind defaultValue = AudioChannelModeKind.Stereo) => ParseAudioChannelModeKind(s, defaultValue);

    public static string ToApiString(this AudioFilterEffectKind val) => val switch
    {
        AudioFilterEffectKind.HighPass => "high_pass",
        AudioFilterEffectKind.LowPass => "low_pass",
        AudioFilterEffectKind.NoiseGate => "noise_gate",
        AudioFilterEffectKind.DeEsser => "de_esser",
        AudioFilterEffectKind.PitchShift => "pitch_shift",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AudioFilterEffectKind ParseAudioFilterEffectKind(string? s, AudioFilterEffectKind defaultValue = AudioFilterEffectKind.None) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioFilterEffectKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AudioFilterEffectKind ToAudioFilterEffectKind(this string? s, AudioFilterEffectKind defaultValue = AudioFilterEffectKind.None) => ParseAudioFilterEffectKind(s, defaultValue);

    public static string ToApiString(this AudioCrossfadeTypeKind val) => val switch
    {
        AudioCrossfadeTypeKind.ConstantPower => "constant_power",
        AudioCrossfadeTypeKind.SCurve => "s_curve",
        AudioCrossfadeTypeKind.EqualGain => "equal_gain",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AudioCrossfadeTypeKind ParseAudioCrossfadeTypeKind(string? s, AudioCrossfadeTypeKind defaultValue = AudioCrossfadeTypeKind.Linear) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioCrossfadeTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AudioCrossfadeTypeKind ToAudioCrossfadeTypeKind(this string? s, AudioCrossfadeTypeKind defaultValue = AudioCrossfadeTypeKind.Linear) => ParseAudioCrossfadeTypeKind(s, defaultValue);

    public static string ToApiString(this SilenceCutModeKind val) => val switch
    {
        SilenceCutModeKind.TrimmingStartEnd => "trimming_start_end",
        SilenceCutModeKind.RemoveAllSilence => "remove_all_silence",
        SilenceCutModeKind.SmartPadding => "smart_padding",
        SilenceCutModeKind.ThresholdBased => "threshold_based",
        SilenceCutModeKind.DynamicGapping => "dynamic_gapping",
        _ => val.ToString().ToLowerInvariant()
    };
    public static SilenceCutModeKind ParseSilenceCutModeKind(string? s, SilenceCutModeKind defaultValue = SilenceCutModeKind.Off) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SilenceCutModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SilenceCutModeKind ToSilenceCutModeKind(this string? s, SilenceCutModeKind defaultValue = SilenceCutModeKind.Off) => ParseSilenceCutModeKind(s, defaultValue);

    public static string ToApiString(this MusicArrangementPresetKind val) => val switch
    {
        MusicArrangementPresetKind.CinematicOrchestral => "cinematic_orchestral",
        MusicArrangementPresetKind.AmbientDrone => "ambient_drone",
        MusicArrangementPresetKind.HybridScore => "hybrid_score",
        MusicArrangementPresetKind.EpicTrailer => "epic_trailer",
        _ => val.ToString().ToLowerInvariant()
    };
    public static MusicArrangementPresetKind ParseMusicArrangementPresetKind(string? s, MusicArrangementPresetKind defaultValue = MusicArrangementPresetKind.CinematicOrchestral) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MusicArrangementPresetKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static MusicArrangementPresetKind ToMusicArrangementPresetKind(this string? s, MusicArrangementPresetKind defaultValue = MusicArrangementPresetKind.CinematicOrchestral) => ParseMusicArrangementPresetKind(s, defaultValue);

    public static string ToApiString(this MusicKeySignatureKind val) => val switch
    {
        MusicKeySignatureKind.CMajor => "c_major",
        MusicKeySignatureKind.AMinor => "a_minor",
        MusicKeySignatureKind.GMajor => "g_major",
        MusicKeySignatureKind.EMinor => "e_minor",
        MusicKeySignatureKind.FMajor => "f_major",
        MusicKeySignatureKind.DMinor => "d_minor",
        MusicKeySignatureKind.DMajor => "d_major",
        MusicKeySignatureKind.BMinor => "b_minor",
        MusicKeySignatureKind.EMajor => "e_major",
        MusicKeySignatureKind.CSharpMinor => "c_sharp_minor",
        MusicKeySignatureKind.AbMajor => "ab_major",
        MusicKeySignatureKind.FMinor => "f_minor",
        _ => val.ToString().ToLowerInvariant()
    };
    public static MusicKeySignatureKind ParseMusicKeySignatureKind(string? s, MusicKeySignatureKind defaultValue = MusicKeySignatureKind.CMajor) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MusicKeySignatureKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static MusicKeySignatureKind ToMusicKeySignatureKind(this string? s, MusicKeySignatureKind defaultValue = MusicKeySignatureKind.CMajor) => ParseMusicKeySignatureKind(s, defaultValue);

    public static string ToApiString(this MusicTimeSignatureKind val) => val switch
    {
        MusicTimeSignatureKind.FourFour => "4/4",
        MusicTimeSignatureKind.ThreeFour => "3/4",
        MusicTimeSignatureKind.SixEight => "6/8",
        MusicTimeSignatureKind.TwoFour => "2/4",
        MusicTimeSignatureKind.FiveFour => "5/4",
        MusicTimeSignatureKind.SevenEight => "7/8",
        MusicTimeSignatureKind.TwelveEight => "12/8",
        _ => "4/4"
    };
    public static MusicTimeSignatureKind ParseMusicTimeSignatureKind(string? s, MusicTimeSignatureKind defaultValue = MusicTimeSignatureKind.FourFour) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : s.Trim() switch
        {
            "4/4" or "four_four" => MusicTimeSignatureKind.FourFour,
            "3/4" or "three_four" => MusicTimeSignatureKind.ThreeFour,
            "6/8" or "six_eight" => MusicTimeSignatureKind.SixEight,
            "2/4" or "two_four" => MusicTimeSignatureKind.TwoFour,
            "5/4" or "five_four" => MusicTimeSignatureKind.FiveFour,
            "7/8" or "seven_eight" => MusicTimeSignatureKind.SevenEight,
            "12/8" or "twelve_eight" => MusicTimeSignatureKind.TwelveEight,
            _ => Enum.TryParse<MusicTimeSignatureKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue
        };
    public static MusicTimeSignatureKind ToMusicTimeSignatureKind(this string? s, MusicTimeSignatureKind defaultValue = MusicTimeSignatureKind.FourFour) => ParseMusicTimeSignatureKind(s, defaultValue);

    public static string ToApiString(this MusicStemTypeKind val) => val switch
    {
        MusicStemTypeKind.FullMix => "full_mix",
        _ => val.ToString().ToLowerInvariant()
    };
    public static MusicStemTypeKind ParseMusicStemTypeKind(string? s, MusicStemTypeKind defaultValue = MusicStemTypeKind.FullMix) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<MusicStemTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static MusicStemTypeKind ToMusicStemTypeKind(this string? s, MusicStemTypeKind defaultValue = MusicStemTypeKind.FullMix) => ParseMusicStemTypeKind(s, defaultValue);

    public static string ToApiString(this AudioMixDuckingLevelKind val) => val switch
    {
        AudioMixDuckingLevelKind.SidechainAuto => "sidechain_auto",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AudioMixDuckingLevelKind ParseAudioMixDuckingLevelKind(string? s, AudioMixDuckingLevelKind defaultValue = AudioMixDuckingLevelKind.Moderate) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioMixDuckingLevelKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AudioMixDuckingLevelKind ToAudioMixDuckingLevelKind(this string? s, AudioMixDuckingLevelKind defaultValue = AudioMixDuckingLevelKind.Moderate) => ParseAudioMixDuckingLevelKind(s, defaultValue);

    public static string ToApiString(this AudioLoudnessStandardKind val) => val switch
    {
        AudioLoudnessStandardKind.EbuR128 => "ebu_r128",
        AudioLoudnessStandardKind.AtscA85 => "atsc_a85",
        AudioLoudnessStandardKind.ReplayGain => "replay_gain",
        AudioLoudnessStandardKind.PeakNormalization => "peak_normalization",
        AudioLoudnessStandardKind.TruePeak => "true_peak",
        AudioLoudnessStandardKind.SpotifyStandard => "spotify_standard",
        AudioLoudnessStandardKind.YoutubeStandard => "youtube_standard",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AudioLoudnessStandardKind ParseAudioLoudnessStandardKind(string? s, AudioLoudnessStandardKind defaultValue = AudioLoudnessStandardKind.EbuR128) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioLoudnessStandardKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AudioLoudnessStandardKind ToAudioLoudnessStandardKind(this string? s, AudioLoudnessStandardKind defaultValue = AudioLoudnessStandardKind.EbuR128) => ParseAudioLoudnessStandardKind(s, defaultValue);

    public static string ToApiString(this AudioFileExtensionKind val) => val.ToString().ToLowerInvariant();
    public static AudioFileExtensionKind ParseAudioFileExtensionKind(string? s, AudioFileExtensionKind defaultValue = AudioFileExtensionKind.Mp3) =>
        string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AudioFileExtensionKind>(s.TrimStart('.'), true, out var r) ? r : defaultValue;
    public static AudioFileExtensionKind ToAudioFileExtensionKind(this string? s, AudioFileExtensionKind defaultValue = AudioFileExtensionKind.Mp3) => ParseAudioFileExtensionKind(s, defaultValue);
}

#endregion
