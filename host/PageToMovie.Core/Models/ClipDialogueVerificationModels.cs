using System;
using System.Collections.Generic;

namespace PageToMovie.Core.Models;

/// <summary>
/// Result model for automated clip dialogue & character speaker verification.
/// </summary>
public sealed class ClipDialogueVerificationResult
{
    public int SceneNumber { get; set; }
    public int ClipNumber { get; set; }
    public string ExpectedSpeaker { get; set; } = "";
    public string ExpectedDialogue { get; set; } = "";
    public string? DetectedSpeaker { get; set; }
    public string? TranscribedDialogue { get; set; }

    /// <summary>Dialogue match similarity score (0.0 to 1.0).</summary>
    public double DialogueAccuracyScore { get; set; }

    /// <summary>True if detected speaker matches expected speaker plate identity.</summary>
    public bool SpeakerMatch { get; set; }

    /// <summary>Status: verified, mismatch, speaker_swap, no_speech.</summary>
    public string Status { get; set; } = "verified";

    /// <summary>Summary notes from multimodal AI evaluation.</summary>
    public string SummaryNote { get; set; } = "";

    /// <summary>
    /// What the verifier actually found — the reasons behind the score. A single number cannot
    /// separate "said off-ee-sir" from "the wrong character spoke", and both are needed to pick a
    /// correction. Empty when the model reported none.
    /// </summary>
    public List<DialogueVerificationIssue> Issues { get; set; } = new();

    /// <summary>Estimated / planned clip duration (seconds).</summary>
    public double EstimatedDurationSeconds { get; set; }

    /// <summary>Number of words in expected dialogue.</summary>
    public int WordCount { get; set; }

    /// <summary>Number of syllables in expected dialogue.</summary>
    public int SyllableCount { get; set; }

    /// <summary>Estimated speech dialogue time (seconds).</summary>
    public double SpeechDurationSeconds { get; set; }

    /// <summary>Estimated action & camera movement overhead time (seconds).</summary>
    public double ActionDurationSeconds { get; set; }

    /// <summary>Actual measured clip MP4 duration (seconds).</summary>
    public double ActualDurationSeconds { get; set; }

    /// <summary>True if spoken dialogue was cut off mid-delivery.</summary>
    public bool SpeechTruncated { get; set; }

    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One finding from dialogue/speaker verification. Kinds are a closed vocabulary; see
/// <see cref="DialogueIssueKinds"/> for the tiers that drive status and correction.</summary>
public sealed class DialogueVerificationIssue
{
    /// <summary>wrong_speaker | wrong_words | wrong_sense | cut_off | missing_line | visual_defect (anatomy / identity / style break seen in the picture) | unplanned_speech (a line or sentence that is not in the script — silent clip with talking, or an extra character speaking) | wrong_voice (the right character/narrator speaks, but the voice contradicts the locked profile — sex or age) | unclear_audio | robotic_delivery | timing | mispronounced | extra_word | missing_word | accent | other</summary>
    public string Kind { get; set; } = "other";
    /// <summary>The word concerned, when the issue is about one (mispronounced, wrong_sense, missing_word…).</summary>
    public string? Word { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>minor | major (the model's own view; the tier below is what the app acts on).</summary>
    public string Severity { get; set; } = "minor";
}

/// <summary>Tiering of issue kinds. Blocking: the clip is wrong (regenerate). Degraded: usable but
/// flagged. Cosmetic: notes only — a line with only cosmetic issues is a 100% line.</summary>
public static class DialogueIssueKinds
{
    public static readonly HashSet<string> Blocking = new(StringComparer.OrdinalIgnoreCase)
        { "wrong_speaker", "wrong_words", "wrong_sense", "cut_off", "missing_line", "visual_defect", "unplanned_speech", "wrong_voice" };
    public static readonly HashSet<string> Degraded = new(StringComparer.OrdinalIgnoreCase)
        { "unclear_audio", "robotic_delivery", "timing" };
    public static readonly HashSet<string> Cosmetic = new(StringComparer.OrdinalIgnoreCase)
        { "mispronounced", "extra_word", "missing_word", "accent" };

    public static bool IsBlocking(string? kind) => kind is not null && Blocking.Contains(kind);
    public static bool IsDegraded(string? kind) => kind is not null && Degraded.Contains(kind);
    public static bool IsCosmetic(string? kind) => kind is not null && Cosmetic.Contains(kind);
}
