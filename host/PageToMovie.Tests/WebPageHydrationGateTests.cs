using System.Net;
using System.Text;
using PageToMovie.Core.Models;
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
            EngineApiClient.ForkableStoriesTimeoutMessage,
            SimpleVoice.TimeoutOrFail(new TaskCanceledException()));
        Assert.Equal(
            EngineApiClient.ForkableStoriesTimeoutMessage,
            SimpleVoice.TimeoutOrFail(new TimeoutException()));
        Assert.Equal(
            EngineApiClient.ForkableStoriesFailMessage,
            SimpleVoice.TimeoutOrFail(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task LoadStoriesAsync_clears_loading_and_shows_titles_on_success()
    {
        var page = new SimpleVoiceStoriesHarness(CatalogEngine());
        await page.LoadStoriesAsync();

        Assert.False(page._storiesLoading);
        Assert.Null(page._storiesError);
        Assert.Equal(2, page._forkableStories.Count);
        Assert.Equal("Buster", page._forkableStories[0].Title);
        Assert.Equal("Mary10", page._forkableStories[1].Title);
        Assert.False(page.NothingReady);
        Assert.False(page.CatalogPending);
    }

    [Fact]
    public async Task LoadStoriesAsync_empty_catalog_is_nothing_ready_not_a_pick_list()
    {
        var json = """{ "ok": true, "projects": [] }""";
        var handler = new ReplyHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var engine = new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        var page = new SimpleVoiceStoriesHarness(engine);
        await page.LoadStoriesAsync();

        Assert.False(page._storiesLoading);
        Assert.Null(page._storiesError);
        Assert.Empty(page._forkableStories);
        Assert.True(page.NothingReady);
        Assert.False(VoiceSubstitutionOverlayGate.ShowEasyStartEntry(page._forkableStories.Count));
    }

    [Fact]
    public async Task LoadStoriesAsync_clears_loading_on_timeout()
    {
        var http = new HttpClient(new HangHandler()) { BaseAddress = new Uri("http://localhost") };
        var engine = new EngineApiClient(http) { ForkableListTimeout = TimeSpan.FromMilliseconds(80) };
        var page = new SimpleVoiceStoriesHarness(engine);

        await page.LoadStoriesAsync();

        Assert.False(page._storiesLoading);
        Assert.Empty(page._forkableStories);
        Assert.Equal(EngineApiClient.ForkableStoriesTimeoutMessage, page._storiesError);
    }

    /// <summary>
    /// #95 awaited InvokeAsync(StateHasChanged) from LoadStoriesAsync during
    /// OnInitializedAsync. The renderer cannot process InvokeAsync until init
    /// returns, so the HTTP call never started and the 8s timeout never painted.
    /// LoadStoriesAsync must use sync Notify() — a hanging PaintAsync must not block it.
    /// </summary>
    [Fact]
    public async Task LoadStoriesAsync_does_not_await_InvokeAsync_paint()
    {
        var page = new HangPaintHarness(CatalogEngine());
        var load = page.LoadStoriesAsync();
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(load, finished);
        await load;
        Assert.False(page._storiesLoading);
        Assert.Equal(2, page._forkableStories.Count);
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

    [Fact]
    public void SimpleVoice_strip_back_from_record_returns_to_story_pick()
    {
        var page = new SimpleVoiceStoriesHarness(CatalogEngine());
        page._phase = SimpleVoice.Phase.Record;
        page._needsCharacterPick = true;
        Assert.Equal(SimpleVoice.BackTarget.Pick, page.ComputeBackTarget());

        page.OnStripBack();
        Assert.Equal(SimpleVoice.Phase.Pick, page._phase);
    }

    [Fact]
    public void SimpleVoice_strip_back_from_voice_returns_to_character_when_user_picked()
    {
        var page = new SimpleVoiceStoriesHarness(CatalogEngine());
        page._phase = SimpleVoice.Phase.Record;
        page._needsCharacterPick = false;
        page._narratorCandidates = new()
        {
            new CharacterSummary { Key = "Character_Teacher", DisplayName = "Teacher" },
            new CharacterSummary { Key = "Character_Mary", DisplayName = "Mary" },
        };
        Assert.Equal(SimpleVoice.BackTarget.Character, page.ComputeBackTarget());

        page.OnStripBack();
        Assert.True(page._needsCharacterPick);
        Assert.Equal(SimpleVoice.Phase.Record, page._phase);
    }

    [Fact]
    public void SimpleVoice_strip_back_from_movie_returns_to_voice()
    {
        var page = new SimpleVoiceStoriesHarness(CatalogEngine());
        page._phase = SimpleVoice.Phase.Movie;
        page._dubbedUrl = "blob:movie";
        Assert.Equal(SimpleVoice.BackTarget.Voice, page.ComputeBackTarget());

        page.OnStripBack();
        Assert.Equal(SimpleVoice.Phase.Done, page._phase);
        Assert.Null(page._dubbedUrl);
    }

    private static EngineApiClient CatalogEngine()
    {
        var json = """
            {
              "ok": true,
              "projects": [
                { "id": "Buster", "title": "Buster" },
                { "id": "Mary10", "title": "Mary10" }
              ]
            }
            """;
        var handler = new ReplyHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
    }

    /// <summary>Sets the injected Engine without a Blazor renderer.</summary>
    private sealed class SimpleVoiceStoriesHarness : SimpleVoice
    {
        public SimpleVoiceStoriesHarness(EngineApiClient engine) => BindEngine(engine);
    }

    /// <summary>PaintAsync never completes — LoadStoriesAsync must not await it.</summary>
    private sealed class HangPaintHarness : SimpleVoice
    {
        public HangPaintHarness(EngineApiClient engine) => BindEngine(engine);

        internal override Task PaintAsync() => new TaskCompletionSource().Task;
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _reply;
        public ReplyHandler(HttpResponseMessage reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_reply);
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            new TaskCompletionSource<HttpResponseMessage>().Task;
    }
}
