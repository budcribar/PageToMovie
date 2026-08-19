using PageToMovie.Core.Models;
using PageToMovie.Engine.Deterministic.Pronunciation;

namespace PageToMovie.Engine;

/// <summary>
/// A targeted change to apply on a QA retry, derived from what the verifier found — instead of
/// re-rolling the identical prompt and hoping. Phase 1 covers the two failure modes seen in the
/// field: the wrong character speaking, and a heteronym read with the wrong sense ("tear the
/// planks" as /tɪər/). Later phases: cut-off (+duration), missing line, revoice route, split shot.
/// </summary>
public sealed record ClipCorrection(
    string? SpeakerLockKey,
    IReadOnlyList<Respelling> Respellings,
    IReadOnlyList<string> Reasons)
{
    public bool IsEmpty => SpeakerLockKey is null && Respellings.Count == 0;

    /// <summary>Short machine-readable tag for the take ("qa_auto:speaker_lock,respell:tear").</summary>
    public string Tag()
    {
        var parts = new List<string>();
        if (SpeakerLockKey is not null) parts.Add("speaker_lock");
        parts.AddRange(Respellings.Select(r => "respell:" + r.Word.ToLowerInvariant()));
        return parts.Count == 0 ? "" : "qa_auto:" + string.Join(",", parts);
    }
}

/// <summary>A word in the line to be spoken as <see cref="Respell"/> (sense <see cref="Meaning"/>).</summary>
public sealed record Respelling(string Word, string Respell, string? Rhymes, string Meaning);

public static class ClipCorrectionPlanner
{
    /// <summary>
    /// Plan the correction for a retry from the verification result. Speaker mismatch → lock the
    /// expected speaker. Reduced accuracy / mismatch on a line that contains a resolvable heteronym →
    /// respell that word (the pronunciation hint alone did not land). Returns an empty plan when
    /// nothing targeted applies (the retry then falls back to a plain re-roll).
    /// </summary>
    public static ClipCorrection Plan(ClipDialogueVerificationResult ver, PronunciationResolver? resolver = null)
    {
        resolver ??= PronunciationResolver.Default;
        var reasons = new List<string>();
        string? speakerLock = null;
        var status = (ver.Status ?? "").Trim().ToLowerInvariant();

        if ((!ver.SpeakerMatch || status == "speaker_swap") && !string.IsNullOrWhiteSpace(ver.ExpectedSpeaker))
        {
            speakerLock = ver.ExpectedSpeaker.Trim();
            reasons.Add($"wrong speaker (heard {ver.DetectedSpeaker ?? "?"}, expected {ver.ExpectedSpeaker})");
        }

        var respellings = new List<Respelling>();
        var wordsWrong = status is "mismatch" || ver.DialogueAccuracyScore < 0.995
                         || ver.Issues.Any(i => i.Kind is "wrong_sense" or "mispronounced");
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

        return new ClipCorrection(speakerLock, respellings, reasons);
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
