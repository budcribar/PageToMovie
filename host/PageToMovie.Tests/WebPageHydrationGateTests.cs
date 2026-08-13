using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class WebPageHydrationGateTests
{
    [Fact]
    public void QualityGate_AdminSession_Uninitialized_State_Hydrates_Cleanly()
    {
        var session = new AdminSessionService(js: null);

        Assert.False(session.IsAuthenticated, "Cold session service must start un-authenticated.");
        Assert.False(session.IsAdmin, "Cold session service must default to non-admin.");
        Assert.Equal("local", session.UserId);
    }

    [Fact]
    public async Task EnsureHydratedAsync_with_no_js_completes_without_hanging()
    {
        var session = new AdminSessionService(js: null);
        var hydrate = session.EnsureHydratedAsync();
        var finished = await Task.WhenAny(hydrate, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(hydrate, finished);
        await hydrate;
        Assert.False(session.IsLoggedIn);
    }

    [Fact]
    public void SimpleVoice_story_load_errors_clear_spinner_copy_on_cancel_and_failure()
    {
        Assert.Equal(
            "Stories took too long to load. Try again.",
            SimpleVoice.TimeoutOrFail(new TaskCanceledException()));
        Assert.Equal(
            "Stories took too long to load. Try again.",
            SimpleVoice.TimeoutOrFail(new TimeoutException()));
        Assert.Equal(
            "Could not load stories. Try again.",
            SimpleVoice.TimeoutOrFail(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData(true, true, "proj-1", true)]
    [InlineData(false, true, "proj-1", false)]
    [InlineData(true, false, "proj-1", false)]
    [InlineData(true, true, "", false)]
    [InlineData(true, true, null, false)]
    public void SimpleVoice_resume_record_requires_logged_in_simple_voice_project(
        bool loggedIn, bool simpleVoice, string? projectId, bool expected)
    {
        Assert.Equal(expected, SimpleVoice.ShouldResumeRecordPhase(loggedIn, simpleVoice, projectId));
    }
}
