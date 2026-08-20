using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Deterministic.Pronunciation;

namespace PageToMovie.Engine;

public sealed partial class ClipDialogueVerificationService
{
    private static string FormatSenseCheckLine(PronunciationAnnotation a)
    {
        var extra = FormatSenseCheckExtra(a);
        return $"   - '{a.Token}' must be pronounced /{a.Ipa}/{extra}" +
               $" — meaning: {a.Meaning}. If it was said with the other meaning, report kind \"wrong_sense\" with word '{a.Token}'.";
    }

    private static string FormatSenseCheckExtra(PronunciationAnnotation a)
    {
        if (string.IsNullOrWhiteSpace(a.Respell))
            return "";
        var rhymes = string.IsNullOrWhiteSpace(a.Rhymes) ? "" : $", rhymes with '{a.Rhymes}'";
        return $" (\"{a.Respell}\"{rhymes})";
    }

    private static (string Status, double Accuracy, bool SpeakerMatch, string Detected, string Summary)
        ApplySilentClipVerdict(
            IReadOnlyList<DialogueVerificationIssue> issues,
            string transcribed,
            string detected,
            string summary)
    {
        // Silent clip: the only verdicts are "silent as planned" or "picture broken".
        var broken = issues.Any(i => string.Equals(i.Kind, StatusVisualDefect, StringComparison.OrdinalIgnoreCase));
        var talked = HasUnplannedSpeech(issues, transcribed);
        if (string.IsNullOrWhiteSpace(detected))
            detected = "None";
        if (string.IsNullOrWhiteSpace(summary))
            summary = SilentClipSummary(broken, talked, transcribed);
        return (SilentClipStatus(broken, talked), 1.0, true, detected, summary);
    }

    private static bool HasUnplannedSpeech(IReadOnlyList<DialogueVerificationIssue> issues, string transcribed) =>
        issues.Any(i => string.Equals(i.Kind, KindUnplannedSpeech, StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrWhiteSpace(transcribed);

    private static string SilentClipStatus(bool broken, bool talked)
    {
        if (broken)
            return StatusVisualDefect;
        if (talked)
            return StatusMismatch;
        return StatusNoSpeech;
    }

    private static string SilentClipSummary(bool broken, bool talked, string transcribed)
    {
        if (broken)
            return "Picture defect found in a silent clip.";
        if (talked)
            return $"Speech heard in a silent clip: '{transcribed}'";
        return "No spoken dialogue planned; picture checked.";
    }

    private static async Task<(double EstSec, double SpeechSec, double ActionSec, double ActualSec)>
        ProbeClipDurationsAsync(ClipSummary? clip, string expectedDialogue, string? clipPath, CancellationToken ct)
    {
        var estSec = clip?.DurationSeconds > 0
            ? (double)clip.DurationSeconds
            : ClipDurationEstimator.Estimate(expectedDialogue, "", "dialogue", "none");
        var (speechSec, actionSec) = ClipDurationEstimator.EstimateBreakdown(
            expectedDialogue, clip?.VisualPrompt ?? "", "", clip?.Delivery ?? "none");
        var durationProbe = new MediaDurationProbe(
            Microsoft.Extensions.Options.Options.Create(new PageToMovie.Core.Options.PageToMovieOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaDurationProbe>.Instance);
        var actualSec = await durationProbe.TryProbeSecondsAsync(clipPath, ct).ConfigureAwait(false) ?? 0.0;
        return (estSec, speechSec, actionSec, actualSec);
    }

    private static bool TryMissingSpeechMismatch(
        string expectedDialogue,
        string transcribed,
        out (double Accuracy, string Status, string Summary) result)
    {
        if (!string.IsNullOrWhiteSpace(expectedDialogue) && string.IsNullOrWhiteSpace(transcribed))
        {
            result = (0.0, StatusMismatch, $"Expected: '{expectedDialogue}' | Heard: (no audio/speech detected) (0% match)");
            return true;
        }
        result = default;
        return false;
    }

    private static (double Accuracy, string Status, string Summary) ApplyBlockingIssueGuards(
        double accuracy,
        string summary,
        List<DialogueVerificationIssue> blocking)
    {
        var kinds = string.Join(", ", blocking.Select(FormatBlockingKind).Distinct());
        return (Math.Min(accuracy, 0.49), StatusForBlockingIssues(blocking), $"{summary} | Blocking: {kinds}".Trim(' ', '|'));
    }

    private static string FormatBlockingKind(DialogueVerificationIssue i) =>
        i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : $" '{i.Word}'");

    private static string StatusForBlockingIssues(List<DialogueVerificationIssue> blocking)
    {
        if (blocking.Any(i => i.Kind is KindWrongSpeaker or KindWrongVoice))
            return StatusSpeakerSwap;
        if (blocking.Any(i => string.Equals(i.Kind, StatusVisualDefect, StringComparison.OrdinalIgnoreCase)))
            return StatusVisualDefect;
        return StatusMismatch;
    }

    private static (double Accuracy, string Status, string Summary) ApplyComputedAccuracyGuards(
        string expectedDialogue,
        string transcribed,
        double accuracy,
        string status,
        string summary,
        IReadOnlyList<DialogueVerificationIssue> issues)
    {
        var computedAcc = CalculateAccuracyScore(expectedDialogue, transcribed);
        var onlyCosmetic = issues.Count > 0 && issues.All(i => DialogueIssueKinds.IsCosmetic(i.Kind));
        if (computedAcc >= 0.99 && (issues.Count == 0 || onlyCosmetic))
            accuracy = 1.0;
        else if (computedAcc < accuracy)
            accuracy = computedAcc;

        if (accuracy < 0.5 && string.Equals(status, StatusVerified, StringComparison.OrdinalIgnoreCase))
            status = StatusMismatch;
        return (accuracy, status, summary);
    }

    private static DialogueVerificationIssue? TryParseIssue(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        var kind = ReadIssueKind(el);
        if (string.IsNullOrWhiteSpace(kind))
            return null;
        if (!DialogueIssueKinds.IsBlocking(kind) && !DialogueIssueKinds.IsDegraded(kind) && !DialogueIssueKinds.IsCosmetic(kind))
            kind = KindOther;
        return new DialogueVerificationIssue
        {
            Kind = kind,
            Word = ReadOptionalIssueString(el, "word")?.Trim(),
            Detail = ReadOptionalIssueString(el, "detail") ?? "",
            Severity = ReadOptionalIssueString(el, "severity") ?? "minor",
        };
    }

    private static string? ReadIssueKind(JsonElement el)
    {
        if (!el.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            return null;
        return k.GetString()?.Trim().ToLowerInvariant();
    }

    private static string? ReadOptionalIssueString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return v.GetString();
    }
}
