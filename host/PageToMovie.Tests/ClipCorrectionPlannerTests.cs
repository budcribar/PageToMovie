using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// QA retries must change what was wrong, not re-roll the same prompt: the wrong character speaking →
/// lock the speaker; a heteronym read with the wrong sense ("tear the planks" as /tɪər/) → respell it
/// in the quoted line. And the verifier's score must be explained by typed issues, tiered so that a
/// wrong speaker/sense fails the clip while "off-ee-sir" is a 100% line.
/// </summary>
public class ClipCorrectionPlannerTests
{
    private static ClipDialogueVerificationResult Ver(string expected, string heard, double acc, string status, bool speakerMatch = true,
        string expectedSpeaker = "Character_Narrator", string? detected = null, params DialogueVerificationIssue[] issues) => new()
    {
        ExpectedSpeaker = expectedSpeaker, DetectedSpeaker = detected ?? expectedSpeaker,
        ExpectedDialogue = expected, TranscribedDialogue = heard,
        DialogueAccuracyScore = acc, Status = status, SpeakerMatch = speakerMatch, Issues = issues.ToList(),
    };

    [Fact]
    public void Wrong_speaker_plans_a_speaker_lock()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("Villains! Dissemble no more!", "Villains! Dissemble no more!", 1.0, "speaker_swap",
            speakerMatch: false, detected: "Character_Old_Man"));
        Assert.Equal("Character_Narrator", plan.SpeakerLockKey);
        Assert.Contains("wrong speaker", plan.Reasons[0]);
        Assert.Contains("speaker_lock", plan.Tag());
    }

    [Fact]
    public void Tear_the_planks_read_as_a_tear_plans_a_respelling()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("Tear up the planks! Here, here!", "Tear up the planks! Here, here!", 0.9, "verified",
            issues: new DialogueVerificationIssue { Kind = "wrong_sense", Word = "Tear", Detail = "said as a teardrop" }));
        var r = Assert.Single(plan.Respellings);
        Assert.Equal("Tear", r.Word, ignoreCase: true);
        Assert.Equal("TAIR", r.Respell);
        Assert.Equal("TAIR up the planks! Here, here!", ClipVideoPromptBuilder.ApplyRespellings("Tear up the planks! Here, here!", plan.Respellings));
        Assert.Contains("respell:tear", plan.Tag());
    }

    [Fact]
    public void Cut_off_line_buys_seconds_not_luck()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("I heard all things in the heaven and in the earth.", "I heard all things in the heaven and in the", 0.85, "mismatch",
            issues: new DialogueVerificationIssue { Kind = "cut_off", Detail = "truncated before 'earth'" }));
        Assert.Equal(ClipCorrectionPlanner.CutOffExtraSeconds, plan.ExtraDurationSec);
        Assert.False(plan.IsEmpty);
        Assert.Contains("+2s", plan.Tag());
        Assert.Contains(plan.Reasons, r => r.Contains("cut off"));
    }

    [Fact]
    public void Missing_or_wrong_words_emphasize_the_whole_line()
    {
        var missing = ClipCorrectionPlanner.Plan(Ver("Villains! Dissemble no more!", "", 0.0, "no_speech"));
        Assert.True(missing.EmphasizeWholeLine);
        Assert.Contains("emphasis", missing.Tag());

        var wrong = ClipCorrectionPlanner.Plan(Ver("Villains! Dissemble no more!", "Villains, assemble once more!", 0.4, "mismatch",
            issues: new DialogueVerificationIssue { Kind = "wrong_words" }));
        Assert.True(wrong.EmphasizeWholeLine);
        Assert.Null(wrong.DeliveryCue);
    }

    [Fact]
    public void Robotic_or_off_timing_delivery_gets_a_delivery_cue_only()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("It is the beating of his hideous heart!", "It is the beating of his hideous heart!", 0.9, "verified",
            issues: new DialogueVerificationIssue { Kind = "robotic_delivery", Detail = "flat monotone" }));
        Assert.Equal(ClipCorrectionPlanner.NaturalDeliveryCue, plan.DeliveryCue);
        Assert.False(plan.EmphasizeWholeLine);
        Assert.Equal(0, plan.ExtraDurationSec);
        Assert.Contains("delivery", plan.Tag());
    }

    [Fact]
    public void Inflected_heteronyms_resolve_and_carry_the_suffix_onto_the_respelling()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("He tears up the planks and reads the letter.", "He tears up the planks and reads the letter.", 0.9, "verified",
            issues: new DialogueVerificationIssue { Kind = "wrong_sense", Word = "tears" }));
        var tears = Assert.Single(plan.Respellings, r => r.Word.Equals("tears", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("TAIRS", tears.Respell);
        Assert.StartsWith("He TAIRS up the planks", ClipVideoPromptBuilder.ApplyRespellings("He tears up the planks and reads the letter.", plan.Respellings));
    }

    [Fact]
    public void Verified_line_with_no_issues_plans_nothing()
    {
        var plan = ClipCorrectionPlanner.Plan(Ver("It is the beating of his hideous heart!", "It is the beating of his hideous heart!", 1.0, "verified"));
        Assert.True(plan.IsEmpty);
        Assert.Equal("", plan.Tag());
    }

    [Fact]
    public void Guard_tiers_issues_blocking_fails_cosmetic_is_100()
    {
        // wrong sense: transcript verbatim, model says 0.97 → must NOT snap to 100%; it fails.
        var (acc, status, summary) = ClipDialogueVerificationService.ApplyAccuracyGuards(
            "Tear up the planks!", "Tear up the planks!", 0.97, "verified", "Expected… | Heard…",
            new[] { new DialogueVerificationIssue { Kind = "wrong_sense", Word = "Tear" } });
        Assert.Equal("mismatch", status);
        Assert.True(acc < 0.5);
        Assert.Contains("wrong_sense 'Tear'", summary);

        // wrong speaker → speaker_swap regardless of text
        (_, status, _) = ClipDialogueVerificationService.ApplyAccuracyGuards(
            "Hello", "Hello", 1.0, "verified", "", new[] { new DialogueVerificationIssue { Kind = "wrong_speaker" } });
        Assert.Equal("speaker_swap", status);

        // cosmetic only + verbatim → 100%
        (acc, status, _) = ClipDialogueVerificationService.ApplyAccuracyGuards(
            "The Officer arrived.", "The Officer arrived.", 0.97, "verified", "", new[] { new DialogueVerificationIssue { Kind = "mispronounced", Word = "Officer" } });
        Assert.Equal(1.0, acc);
        Assert.Equal("verified", status);

        // degraded issue: keep the lower of model and text (no snap up)
        (acc, _, _) = ClipDialogueVerificationService.ApplyAccuracyGuards(
            "The Officer arrived.", "The Officer arrived.", 0.8, "verified", "", new[] { new DialogueVerificationIssue { Kind = "unclear_audio" } });
        Assert.Equal(0.8, acc);

        // no issues reported, verbatim → 100% (model noise)
        (acc, _, _) = ClipDialogueVerificationService.ApplyAccuracyGuards("Hello there.", "Hello there.", 0.97, "verified", "");
        Assert.Equal(1.0, acc);
    }

    [Fact]
    public void Issues_parse_and_unknown_kinds_become_other()
    {
        var root = JsonDocument.Parse("""{"issues":[{"kind":"WRONG_SENSE","word":"tear","detail":"teardrop","severity":"major"},{"kind":"weird"},{"kind":""}]}""").RootElement;
        var issues = ClipDialogueVerificationService.ParseIssues(root);
        Assert.Equal(2, issues.Count);
        Assert.Equal("wrong_sense", issues[0].Kind);
        Assert.Equal("tear", issues[0].Word);
        Assert.Equal("other", issues[1].Kind);
    }

    [Fact]
    public void Sense_checks_are_generated_for_heteronyms_in_the_line()
    {
        var checks = ClipDialogueVerificationService.BuildSenseChecks("Tear up the planks! Here, here!");
        Assert.Contains("'Tear'", checks);
        Assert.Contains("TAIR", checks);
        Assert.Contains("wrong_sense", checks);
        Assert.Equal("", ClipDialogueVerificationService.BuildSenseChecks("It is the beating of his hideous heart!"));
    }
}
