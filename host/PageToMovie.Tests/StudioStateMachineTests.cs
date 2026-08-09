using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Contract tests for <see cref="StudioStateMachine"/> — the intended SSoT for pipeline phase
/// and step gating (see host/docs/studio-state-machine-migration-plan.md Phase 1).
/// </summary>
public class StudioStateMachineTests
{
    private static AdaptationStatus BaseStatus(
        Action<AdaptationStatus>? configure = null,
        bool xai = true)
    {
        var s = new AdaptationStatus
        {
            ProjectId = "Demo",
            XaiConfigured = xai,
            Book = new BookSourceStatus(),
            Screenplay = new ScreenplayStatus(),
            Stage1 = new Stage1Status(),
            Stage2 = new Stage2PlanStatus(),
            Cast = new CastStatus { ReadyForShots = true },
        };
        configure?.Invoke(s);
        return s;
    }

    // ── DeterminePhase ──────────────────────────────────────────────────────

    [Fact]
    public void DeterminePhase_null_is_ImportRequired()
    {
        Assert.Equal(StudioPhase.ImportRequired, StudioStateMachine.DeterminePhase(null));
    }

    [Fact]
    public void DeterminePhase_no_keys_is_SetupRequired()
    {
        var s = BaseStatus(st => st.Book.BookTextExists = true, xai: false);
        Assert.Equal(StudioPhase.SetupRequired, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_no_source_is_ImportRequired()
    {
        var s = BaseStatus();
        Assert.Equal(StudioPhase.ImportRequired, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_pdf_without_text_or_draft_is_TextExtractionPending()
    {
        var s = BaseStatus(st =>
        {
            st.Book.PdfExists = true;
            st.Book.BookTextExists = false;
            st.Screenplay.DraftExists = false;
        });
        Assert.Equal(StudioPhase.TextExtractionPending, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_unsigned_draft_is_ScreenplayDraft()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = false;
            st.Screenplay.ReadyForShots = false;
        });
        Assert.Equal(StudioPhase.ScreenplayDraft, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_stage1_alone_is_not_approved()
    {
        // Intentional product rule: Stage 1 package without Fountain sign-off stays Draft.
        var s = BaseStatus(st =>
        {
            st.Book.BookTextExists = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 5;
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = false;
            st.Screenplay.ReadyForShots = false;
        });
        Assert.Equal(StudioPhase.ScreenplayDraft, StudioStateMachine.DeterminePhase(s));
        Assert.False(StudioStateMachine.IsScreenplayApproved(s.Screenplay));
    }

    [Fact]
    public void DeterminePhase_signed_screenplay_without_stage2_is_ScreenplayApproved()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = false;
        });
        Assert.Equal(StudioPhase.ScreenplayApproved, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_readyForShots_without_signed_flag_still_approved()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = false;
            st.Screenplay.ReadyForShots = true;
        });
        Assert.Equal(StudioPhase.ScreenplayApproved, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_stage2_ready_is_ShotPlanReady()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Clips = 12;
            st.Stage2.Stage2Stale = false;
        });
        Assert.Equal(StudioPhase.ShotPlanReady, StudioStateMachine.DeterminePhase(s));
        Assert.True(StudioStateMachine.IsShotPlanReady(s.Stage2));
    }

    [Fact]
    public void DeterminePhase_stale_stage2_stays_ScreenplayApproved()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Clips = 12;
            st.Stage2.Stage2Stale = true;
        });
        Assert.Equal(StudioPhase.ScreenplayApproved, StudioStateMachine.DeterminePhase(s));
        Assert.False(StudioStateMachine.IsShotPlanReady(s.Stage2));
    }

    [Fact]
    public void DeterminePhase_stage2_ready_zero_clips_not_ShotPlanReady()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Clips = 0;
        });
        Assert.Equal(StudioPhase.ScreenplayApproved, StudioStateMachine.DeterminePhase(s));
    }

    [Fact]
    public void DeterminePhase_does_not_return_ReviewReady_until_status_has_clip_rollup()
    {
        // Documented reservation: no AdaptationStatus field yet for all-clips-complete.
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Clips = 99;
            st.Stage2.Stage2Stale = false;
        });
        var phase = StudioStateMachine.DeterminePhase(s);
        Assert.Equal(StudioPhase.ShotPlanReady, phase);
        Assert.NotEqual(StudioPhase.ReviewReady, phase);
    }

    [Fact]
    public void DetectSourceType_prefers_explicit_fountain_book_kind()
    {
        var book = new BookSourceStatus { BookKind = "fountain", BookTextExists = true };
        Assert.Equal(SourceDocumentType.Fountain, StudioStateMachine.DetectSourceType(book, null));
    }

    [Fact]
    public void DetectSourceType_pdf_before_plain_text()
    {
        var book = new BookSourceStatus { PdfExists = true, BookTextExists = true };
        Assert.Equal(SourceDocumentType.Pdf, StudioStateMachine.DetectSourceType(book, null));
    }

    // ── CanNavigateTo ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(StudioPhase.SetupRequired, false)]
    [InlineData(StudioPhase.ImportRequired, false)]
    [InlineData(StudioPhase.TextExtractionPending, false)]
    [InlineData(StudioPhase.ScreenplayDraft, false)]
    [InlineData(StudioPhase.ScreenplayApproved, true)]
    [InlineData(StudioPhase.ShotPlanReady, true)]
    public void CanNavigateTo_Cast_requires_ScreenplayApproved(StudioPhase phase, bool allowed)
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(StudioStep.Cast, phase);
        Assert.Equal(allowed, ok);
        if (!allowed && phase == StudioPhase.TextExtractionPending)
            Assert.Contains("PDF", reason, StringComparison.OrdinalIgnoreCase);
        else if (!allowed)
            Assert.Contains("screenplay", reason, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Equal("", reason);
    }

    [Theory]
    [InlineData(StudioPhase.ScreenplayDraft, false)]
    [InlineData(StudioPhase.ScreenplayApproved, true)]
    [InlineData(StudioPhase.ShotPlanReady, true)]
    public void CanNavigateTo_Estimate_tracks_Cast_unlock_phase(StudioPhase phase, bool allowed)
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(StudioStep.Estimate, phase);
        Assert.Equal(allowed, ok);
        if (!allowed)
            Assert.NotEqual("", reason);
    }

    [Fact]
    public void CanNavigateTo_Film_blocked_when_stage2_stale()
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Film, StudioPhase.ScreenplayApproved, castReady: true, isStage2Stale: true);
        Assert.False(ok);
        Assert.Contains("shot plan", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanNavigateTo_Film_blocked_before_shot_plan()
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Film, StudioPhase.ScreenplayApproved, castReady: true, isStage2Stale: false);
        Assert.False(ok);
        Assert.Contains("shot plan", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanNavigateTo_Film_blocked_when_cast_incomplete()
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Film, StudioPhase.ShotPlanReady, castReady: false, isStage2Stale: false);
        Assert.False(ok);
        Assert.Contains("voice", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanNavigateTo_Film_allowed_when_plan_and_cast_ready()
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Film, StudioPhase.ShotPlanReady, castReady: true, isStage2Stale: false);
        Assert.True(ok);
        Assert.Equal("", reason);
    }

    [Fact]
    public void CanNavigateTo_Review_allowed_at_ShotPlanReady_without_cast()
    {
        // Matches legacy strip: Review unlocked with shot plan; Film still checks cast.
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Review, StudioPhase.ShotPlanReady, castReady: false, isStage2Stale: false);
        Assert.True(ok);
        Assert.Equal("", reason);
    }

    [Fact]
    public void CanNavigateTo_Review_blocked_before_shot_plan()
    {
        var (ok, reason) = StudioStateMachine.CanNavigateTo(
            StudioStep.Review, StudioPhase.ScreenplayApproved);
        Assert.False(ok);
        Assert.Contains("shot plan", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanNavigateTo_Book_blocked_only_when_SetupRequired()
    {
        var blocked = StudioStateMachine.CanNavigateTo(StudioStep.Book, StudioPhase.SetupRequired);
        Assert.False(blocked.Allowed);

        var open = StudioStateMachine.CanNavigateTo(StudioStep.Book, StudioPhase.ImportRequired);
        Assert.True(open.Allowed);
    }

    [Fact]
    public void CanNavigateTo_Setup_always_allowed()
    {
        foreach (StudioPhase phase in Enum.GetValues<StudioPhase>())
        {
            var (ok, reason) = StudioStateMachine.CanNavigateTo(StudioStep.Setup, phase);
            Assert.True(ok);
            Assert.Equal("", reason);
        }
    }

    // ── End-to-end fixture: signed → phase → Cast unlock ────────────────────

    [Fact]
    public void Signoff_status_unlocks_Cast_via_phase()
    {
        var before = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = false;
            st.Screenplay.ReadyForShots = false;
        });
        var phaseBefore = StudioStateMachine.DeterminePhase(before);
        Assert.Equal(StudioPhase.ScreenplayDraft, phaseBefore);
        Assert.False(StudioStateMachine.CanNavigateTo(StudioStep.Cast, phaseBefore).Allowed);

        var after = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
        });
        var phaseAfter = StudioStateMachine.DeterminePhase(after);
        Assert.Equal(StudioPhase.ScreenplayApproved, phaseAfter);
        Assert.True(StudioStateMachine.CanNavigateTo(StudioStep.Cast, phaseAfter).Allowed);
        Assert.True(StudioStateMachine.CanNavigateTo(StudioStep.Estimate, phaseAfter).Allowed);
    }

    // ── DetermineNextStep ───────────────────────────────────────────────────

    [Fact]
    public void DetermineNextStep_import_when_empty()
    {
        Assert.Equal("import_book", StudioStateMachine.DetermineNextStep(null));
        Assert.Equal("import_book", StudioStateMachine.DetermineNextStep(BaseStatus()));
    }

    [Fact]
    public void DetermineNextStep_sign_screenplay_for_unsigned_draft()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = false;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 2;
        });
        Assert.Equal("sign_screenplay", StudioStateMachine.DetermineNextStep(s));
    }

    [Fact]
    public void DetermineNextStep_pin_characters_after_signed_until_cast_ready()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 2;
            st.Cast.ReadyForShots = false;
        });
        Assert.Equal("pin_characters", StudioStateMachine.DetermineNextStep(s));
    }

    [Fact]
    public void DetermineNextStep_run_stage2_when_cast_ready_no_plan()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 2;
            st.Cast.ReadyForShots = true;
            st.Stage2.Stage2Ready = false;
        });
        Assert.Equal("run_stage2", StudioStateMachine.DetermineNextStep(s));
    }

    [Fact]
    public void DetermineNextStep_replan_when_stage2_stale()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 2;
            st.Cast.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Stale = true;
            st.Stage2.Stage2Clips = 4;
        });
        Assert.Equal("replan_stage2", StudioStateMachine.DetermineNextStep(s));
    }

    [Fact]
    public void DetermineNextStep_generate_clips_when_plan_ready()
    {
        var s = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 2;
            st.Cast.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Stale = false;
            st.Stage2.Stage2Clips = 4;
        });
        Assert.Equal("generate_clips", StudioStateMachine.DetermineNextStep(s));
    }

    [Fact]
    public void DetermineNextStep_and_CanNavigateTo_agree_on_Cast_unlock()
    {
        var draft = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 1;
        });
        Assert.Equal("sign_screenplay", StudioStateMachine.DetermineNextStep(draft));
        Assert.False(StudioStateMachine.CanNavigateTo(
            StudioStep.Cast, StudioStateMachine.DeterminePhase(draft)).Allowed);

        var signed = BaseStatus(st =>
        {
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage1.Present = true;
            st.Stage1.SceneCount = 1;
            st.Cast.ReadyForShots = false;
        });
        Assert.Equal("pin_characters", StudioStateMachine.DetermineNextStep(signed));
        Assert.True(StudioStateMachine.CanNavigateTo(
            StudioStep.Cast, StudioStateMachine.DeterminePhase(signed)).Allowed);
    }
}
