using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Book-strip unlock helpers must track <see cref="StudioStateMachine"/> (migration PR3).
/// </summary>
public class AdaptationStepUiTests
{
    private static AdaptationStatus Status(Action<AdaptationStatus> configure)
    {
        var s = new AdaptationStatus
        {
            ProjectId = "Demo",
            XaiConfigured = true,
            Book = new BookSourceStatus(),
            Screenplay = new ScreenplayStatus(),
            Stage1 = new Stage1Status(),
            Stage2 = new Stage2PlanStatus(),
            Cast = new CastStatus(),
        };
        configure(s);
        return s;
    }

    [Fact]
    public void OutlineEnabled_false_when_no_source()
    {
        Assert.False(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(null));
        Assert.False(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(Status(_ => { })));
    }

    [Fact]
    public void OutlineEnabled_true_for_book_text_or_draft()
    {
        Assert.True(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(
            Status(s => s.Book.BookTextExists = true)));
        Assert.True(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(
            Status(s => s.Screenplay.DraftExists = true)));
        Assert.True(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(
            Status(s => s.Book.PdfExists = true)));
    }

    [Fact]
    public void OutlineEnabled_true_for_stage1_package_without_fountain_flags()
    {
        Assert.True(AdaptationPageBase.AdaptationStepUi.OutlineEnabled(
            Status(s =>
            {
                s.Stage1.Present = true;
                s.Stage1.SceneCount = 3;
            })));
    }

    [Fact]
    public void ShotsEnabled_requires_screenplay_approval()
    {
        Assert.False(AdaptationPageBase.AdaptationStepUi.ShotsEnabled(null));
        Assert.False(AdaptationPageBase.AdaptationStepUi.ShotsEnabled(
            Status(s =>
            {
                s.Screenplay.DraftExists = true;
                s.Screenplay.Signed = false;
                s.Screenplay.ReadyForShots = false;
            })));
        Assert.True(AdaptationPageBase.AdaptationStepUi.ShotsEnabled(
            Status(s =>
            {
                s.Screenplay.DraftExists = true;
                s.Screenplay.Signed = true;
            })));
        Assert.True(AdaptationPageBase.AdaptationStepUi.ShotsEnabled(
            Status(s => s.Screenplay.ReadyForShots = true)));
    }

    [Fact]
    public void ShotsEnabled_stage1_alone_does_not_unlock()
    {
        Assert.False(AdaptationPageBase.AdaptationStepUi.ShotsEnabled(
            Status(s =>
            {
                s.Stage1.Present = true;
                s.Stage1.SceneCount = 5;
                s.Screenplay.DraftExists = true;
            })));
    }

    [Fact]
    public void SuggestedStepPath_uses_NextStep_when_present()
    {
        var s = Status(st =>
        {
            st.NextStep = "run_stage2";
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
        });
        Assert.Equal("/adaptation/shots", AdaptationPageBase.AdaptationStepUi.SuggestedStepPath(s));
    }

    [Fact]
    public void SuggestedStepPath_falls_back_to_phase_when_NextStep_empty()
    {
        var draft = Status(st =>
        {
            st.NextStep = "";
            st.Screenplay.DraftExists = true;
        });
        Assert.Equal("/adaptation/screenplay", AdaptationPageBase.AdaptationStepUi.SuggestedStepPath(draft));

        var approved = Status(st =>
        {
            st.NextStep = "";
            st.Screenplay.DraftExists = true;
            st.Screenplay.Signed = true;
            st.Screenplay.ReadyForShots = true;
            st.Stage2.Stage2Ready = true;
            st.Stage2.Stage2Clips = 4;
        });
        Assert.Equal("/adaptation/shots", AdaptationPageBase.AdaptationStepUi.SuggestedStepPath(approved));
    }
}
