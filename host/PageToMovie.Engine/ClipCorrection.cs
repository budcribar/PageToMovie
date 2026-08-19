using PageToMovie.Core.Models;
using PageToMovie.Engine.Deterministic.Pronunciation;

namespace PageToMovie.Engine;

/// <summary>
/// A targeted change to apply on a QA retry, derived from what the verifier found — instead of
/// re-rolling the identical prompt and hoping. Phase 1: the wrong character speaking (speaker
/// lock) and a heteronym read with the wrong sense (respelling inside the quoted line). Phase 2:
/// a cut-off line (+duration), a missing / wrong-words line (whole-line emphasis), and degraded
/// delivery — timing / robotic (delivery cue). Later: revoice route, split shot.
/// </summary>
public sealed record ClipCorrection(
    string? SpeakerLockKey,
    IReadOnlyList<Respelling> Respellings,
    IReadOnlyList<string> Reasons,
    int ExtraDurationSec = 0,
    bool EmphasizeWholeLine = false,
    string? DeliveryCue = null)
{
    public bool IsEmpty =>
        SpeakerLockKey is null && Respellings.Count == 0 && ExtraDurationSec == 0
        && !EmphasizeWholeLine && DeliveryCue is null;

    /// <summary>Short machine-readable tag for the take ("qa_auto:speaker_lock,respell:tear,+2s").</summary>
    public string Tag()
    {
        var parts = new List<string>();
        if (SpeakerLockKey is not null) parts.Add("speaker_lock");
        parts.AddRange(Respellings.Select(r => "respell:" + r.Word.ToLowerInvariant()));
        if (ExtraDurationSec > 0) parts.Add($"+{ExtraDurationSec}s");
        if (EmphasizeWholeLine) parts.Add("emphasis");
        if (DeliveryCue is not null) parts.Add("delivery");
        return parts.Count == 0 ? "" : "qa_auto:" + string.Join(",", parts);
    }
}

/// <summary>A word in the line to be spoken as <see cref="Respell"/> (sense <see cref="Meaning"/>).</summary>
public sealed record Respelling(string Word, string Respell, string? Rhymes, string Meaning);

public static class ClipCorrectionPlanner
{
    /// <summary>Seconds added to the clip when the verifier says the line was cut off (per retry).</summary>
    public const int CutOffExtraSeconds = 2;

    /// <summary>Delivery cue used when the verifier flags robotic delivery or timing.</summary>
    public const string NaturalDeliveryCue =
        "Natural, human, unhurried delivery — conversational pace, real breath, no monotone; " +
        "the line starts as the shot opens and finishes before the shot ends.";

    /// <summary>
    /// Plan the correction for a retry from the verification result. Speaker mismatch → lock the
    /// expected speaker. Reduced accuracy / mismatch on a line that contains a resolvable heteronym →
    /// respell that word (the pronunciation hint alone did not land). cut_off → more seconds.
    /// missing_line / wrong_words / no_speech → whole-line emphasis. timing / robotic_delivery →
    /// natural delivery cue. Returns an empty plan when nothing targeted applies (the retry then
    /// falls back to a plain re-roll).
    /// </summary>
    public static ClipCorrection Plan(ClipDialogueVerificationResult ver, PronunciationResolver? resolver = null)
    {
        resolver ??= PronunciationResolver.Default;
        var reasons = new List<string>();
        string? speakerLock = null;
        var status = (ver.Status ?? "").Trim().ToLowerInvariant();
        var kinds = new HashSet<string>(ver.Issues.Select(i => (i.Kind ?? "").ToLowerInvariant()));

        if ((!ver.SpeakerMatch || status == "speaker_swap" || kinds.Contains("wrong_speaker") || kinds.Contains("wrong_voice"))
            && !string.IsNullOrWhiteSpace(ver.ExpectedSpeaker))
        {
            speakerLock = ver.ExpectedSpeaker.Trim();
            reasons.Add(kinds.Contains("wrong_voice") && !kinds.Contains("wrong_speaker")
                ? $"wrong voice for {ver.ExpectedSpeaker} (re-lock voice identity)"
                : $"wrong speaker (heard {ver.DetectedSpeaker ?? "?"}, expected {ver.ExpectedSpeaker})");
        }

        var respellings = new List<Respelling>();
        var wordsWrong = status is "mismatch" || ver.DialogueAccuracyScore < 0.995
                         || kinds.Contains("wrong_sense") || kinds.Contains("mispronounced");
        if (wordsWrong && !string.IsNullOrWhiteSpace(ver.ExpectedDialogue))
        {
            var res = resolver.Resolve(ver.ExpectedDialogue);
            foreach (var a in res.Annotations.Where(a => !string.IsNullOrWhiteSpace(a.Respell)))
            {
                respellings.Add(new Respelling(a.Token, a.Respell!, a.Rhymes, a.Meaning));
            }
            // A verifier issue that names the word wins even if the resolver was unsure.
            foreach (var issue in ver.Issues.Where(i => i.Kind is "wrong_sense" or "mispronounced" && !string.IsNullOrWhiteSpace(i.Word)))
            {
                if (respellings.Any(r => string.Equals(r.Word, issue.Word, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var forced = ForceSense(resolver, issue.Word!, ver.ExpectedDialogue);
                if (forced is not null) respellings.Add(forced);
            }
            if (respellings.Count > 0)
                reasons.Add("pronunciation: " + string.Join(", ", respellings.Select(r => $"{r.Word}→{r.Respell}")));
        }

        // Cut off before the last word: the clip was too short for the line — buy seconds, not luck.
        var extraSec = 0;
        if (kinds.Contains("cut_off"))
        {
            extraSec = CutOffExtraSeconds;
            reasons.Add($"line cut off → +{extraSec}s");
        }

        // Line not spoken / different words: say the whole line, and only that line, clearly.
        var emphasize = kinds.Contains("missing_line") || kinds.Contains("wrong_words") || status == "no_speech";
        if (emphasize)
            reasons.Add(status == "no_speech" ? "no speech heard → whole-line emphasis" : "line missing/wrong words → whole-line emphasis");

        // Usable but flagged: fix the delivery rather than the words.
        string? deliveryCue = null;
        if (kinds.Contains("robotic_delivery") || kinds.Contains("timing"))
        {
            deliveryCue = NaturalDeliveryCue;
            reasons.Add(kinds.Contains("timing") ? "timing off → delivery cue" : "robotic delivery → delivery cue");
        }

        return new ClipCorrection(speakerLock, respellings, reasons, extraSec, emphasize, deliveryCue);
    }

    /// <summary>The verifier flagged a specific word; pick the best sense even below the resolver's confidence bar.</summary>
    private static Respelling? ForceSense(PronunciationResolver resolver, string word, string dialogue)
    {
        var res = resolver.Resolve(dialogue);
        var a = res.Annotations.FirstOrDefault(x => string.Equals(x.Token, word, StringComparison.OrdinalIgnoreCase));
        if (a is not null && !string.IsNullOrWhiteSpace(a.Respell))
            return new Respelling(a.Token, a.Respell!, a.Rhymes, a.Meaning);
        return null;
    }
}
