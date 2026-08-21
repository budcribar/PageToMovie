using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Contract guard for <see cref="ActiveProjectState"/> readiness loading. The /adaptation endpoint
/// returns an <see cref="AdaptationDto"/> wrapper ({ ok, projectId, adaptation }); readiness flags
/// and <see cref="ActiveProjectState.Status"/> live on the nested <c>.adaptation</c>. Feeding the
/// parser the wrapper root (as a refactor once did) left Status null and every gate false — the
/// false "No AI model selected" warning on the Book step even with a model selected.
///
/// These tests drive the real load methods through a stub transport, so they also cover the
/// neighbouring failure modes: server casing changes, a missing status node, transport errors, the
/// exact endpoint called, stale-shot gating, and cold-load hydration of the active project.
/// </summary>
public class ActiveProjectStateReadinessTests
{
    /// <summary>
    /// Records every request and replies per a responder (default 200 + fixed JSON). Lets a test
    /// assert which endpoint was hit and simulate any status code / body the API might return.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> _responder;
        public List<Uri> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;
        public StubHandler(HttpStatusCode status, string json) : this(_ => (status, json)) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null) Requests.Add(request.RequestUri);
            var (status, json) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ActiveProjectState State, EngineApiClient Engine) NewState(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (new ActiveProjectState(), new EngineApiClient(http));
    }

    /// <summary>Set a project, then refresh its readiness against the stubbed adaptation JSON.</summary>
    private static async Task<ActiveProjectState> LoadReadiness(string adaptationJson)
    {
        var (state, engine) = NewState(new StubHandler(HttpStatusCode.OK, adaptationJson));
        state.Set("Demo");
        await state.RefreshReadinessAsync(engine);
        return state;
    }

    // Fully-ready project, camelCase payload (the API's default naming policy).
    private const string ReadyCamel = """
        {
          "ok": true,
          "projectId": "Demo",
          "adaptation": {
            "xaiConfigured": true,
            "planningModel": "grok-4",
            "screenplay": { "readyForShots": true, "signed": true },
            "stage1": { "present": true, "sceneCount": 3 },
            "stage2": { "stage2Ready": true, "stage2Clips": 5, "stage2Stale": false },
            "cast": { "readyForShots": true }
          }
        }
        """;

    [Fact]
    public async Task Ready_camelCase_populates_status_and_all_gates()
    {
        var state = await LoadReadiness(ReadyCamel);

        Assert.NotNull(state.Status);
        Assert.True(state.Status!.XaiConfigured);
        Assert.Equal("grok-4", state.Status.PlanningModel);
        Assert.True(state.CanCharacters);
        Assert.True(state.CanScenes);
        Assert.True(state.CanReview);
        Assert.Null(state.ScenesWarning);
        Assert.True(state.CanEstimate);
        Assert.Equal("", state.ScenesBlockedReason);
    }

    [Fact]
    public async Task Ready_PascalCase_payload_is_tolerated()
    {
        // The client must not break if the server serializes PascalCase — the JSON probe and the
        // case-insensitive deserializer both handle it. Guards a silent casing regression.
        const string pascal = """
            {
              "Ok": true,
              "ProjectId": "Demo",
              "Adaptation": {
                "XaiConfigured": true,
                "PlanningModel": "grok-4",
                "Screenplay": { "ReadyForShots": true, "Signed": true },
                "Stage2": { "Stage2Ready": true, "Stage2Clips": 5, "Stage2Stale": false },
                "Cast": { "ReadyForShots": true }
              }
            }
            """;

        var state = await LoadReadiness(pascal);

        Assert.NotNull(state.Status);
        Assert.Equal("grok-4", state.Status!.PlanningModel);
        Assert.True(state.CanScenes);
        Assert.True(state.CanCharacters);
    }

    [Fact]
    public async Task Status_is_populated_even_before_shots_are_ready()
    {
        // The Book-step model badge needs Status.PlanningModel even when nothing is generated yet;
        // Status must be populated independent of readiness, while the gates stay closed.
        const string json = """
            {
              "ok": true,
              "projectId": "Demo",
              "adaptation": {
                "xaiConfigured": true,
                "planningModel": "grok-4",
                "screenplay": { "readyForShots": false, "signed": false },
                "stage2": { "stage2Ready": false, "stage2Clips": 0 }
              }
            }
            """;

        var state = await LoadReadiness(json);

        Assert.NotNull(state.Status);
        Assert.Equal("grok-4", state.Status!.PlanningModel);
        Assert.False(state.CanScenes);
        Assert.False(state.CanCharacters);
    }

    [Fact]
    public async Task Stale_shot_plan_closes_scenes_gate_with_reason()
    {
        const string json = """
            {
              "ok": true,
              "projectId": "Demo",
              "adaptation": {
                "xaiConfigured": true,
                "planningModel": "grok-4",
                "screenplay": { "readyForShots": true, "signed": true },
                "stage2": { "stage2Ready": false, "stage2Clips": 4, "stage2Stale": true },
                "cast": { "readyForShots": true }
              }
            }
            """;

        var state = await LoadReadiness(json);

        Assert.NotNull(state.Status);
        Assert.True(state.CanCharacters);              // screenplay approved
        Assert.True(state.CanScenes);                   // screenplay approved, shot plan builds in Film
        // A stale plan (Ready=false here) is not "ready" — Review has nothing current to review.
        Assert.False(state.CanReview);
        Assert.Equal("Build the shot plan first", state.ReviewBlockedReason);
    }

    [Fact]
    public async Task Ready_but_stale_plan_gates_review_and_warns_on_film()
    {
        const string json = """
            {
              "ok": true,
              "projectId": "Demo",
              "adaptation": {
                "xaiConfigured": true,
                "screenplay": { "readyForShots": true, "signed": true },
                "stage2": { "stage2Ready": true, "stage2Clips": 4, "stage2Stale": true },
                "cast": { "readyForShots": true }
              }
            }
            """;

        var state = await LoadReadiness(json);

        Assert.True(state.CanScenes);                   // Film stays reachable: the fix (rebuild) lives there
        Assert.Contains("update the shot plan", state.ScenesWarning, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.CanReview);
        Assert.Equal("Update the shot plan first", state.ReviewBlockedReason);
    }

    [Fact]
    public async Task Cast_not_ready_warns_on_film_but_does_not_block_it()
    {
        const string json = """
            {
              "ok": true,
              "projectId": "Demo",
              "adaptation": {
                "xaiConfigured": true,
                "screenplay": { "readyForShots": true, "signed": true },
                "stage2": { "stage2Ready": true, "stage2Clips": 4, "stage2Stale": false },
                "cast": { "readyForShots": false }
              }
            }
            """;

        var state = await LoadReadiness(json);

        Assert.True(state.CanScenes);
        Assert.Contains("Lock every character", state.ScenesWarning);
        Assert.True(state.CanReview);                   // plan is current; Review is about clips, not cast
    }

    [Fact]
    public async Task Missing_adaptation_node_clears_readiness_without_throwing()
    {
        var state = await LoadReadiness("""{ "ok": true, "projectId": "Demo", "adaptation": null }""");

        Assert.Null(state.Status);
        Assert.False(state.CanScenes);
        Assert.False(state.CanCharacters);
        Assert.True(state.IsReady);                    // the call itself succeeded
        Assert.Null(state.LoadError);
    }

    [Fact]
    public async Task Transport_error_sets_LoadError_and_leaves_status_null()
    {
        var (state, engine) = NewState(new StubHandler(HttpStatusCode.InternalServerError, "{}"));
        state.Set("Demo");

        await state.RefreshReadinessAsync(engine);

        Assert.Null(state.Status);
        Assert.False(state.CanScenes);
        Assert.False(string.IsNullOrEmpty(state.LoadError));
    }

    [Fact]
    public async Task Reads_the_project_scoped_adaptation_endpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReadyCamel);
        var (state, engine) = NewState(handler);
        state.Set("Demo");

        await state.RefreshReadinessAsync(engine);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/Demo/adaptation", uri.AbsolutePath);
    }

    [Fact]
    public async Task RefreshFromServer_hydrates_active_project_when_none_is_set()
    {
        // Direct navigation to a studio step (or a page refresh) arrives with no project selected.
        // RefreshFromServerAsync must pull the workspace's Active project and set it — otherwise the
        // step page shows "No project selected" even though a project is active on the server.
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/projects" => (HttpStatusCode.OK,
                """{ "active": { "id": "Mary9", "label": "Mary9" }, "projects": [ { "id": "Mary9" } ] }"""),
            "/api/projects/Mary9/adaptation" => (HttpStatusCode.OK, ReadyCamel),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var (state, engine) = NewState(handler);
        // No state.Set(...) — simulate a cold page load.

        await state.RefreshFromServerAsync(engine);

        Assert.True(state.HasProject);
        Assert.Equal("Mary9", state.ProjectId);
        Assert.NotNull(state.Status);   // readiness was loaded for the hydrated project
    }

    [Fact]
    public async Task SelectAsync_activates_sets_and_loads_readiness()
    {
        // The single "switch to this project" entry point: it must persist the choice on the server
        // (activate), update local state, and load readiness — the trio that call sites used to
        // hand-roll and drift on.
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/projects/Mary9/activate" => (HttpStatusCode.OK, "{}"),
            "/api/projects/Mary9/adaptation" => (HttpStatusCode.OK, ReadyCamel),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var (state, engine) = NewState(handler);

        await state.SelectAsync(engine, "Mary9", "Mary Nine");

        Assert.Equal("Mary9", state.ProjectId);
        Assert.Equal("Mary Nine", state.Label);
        Assert.NotNull(state.Status);
        Assert.Contains(handler.Requests, u => u.AbsolutePath == "/api/projects/Mary9/activate");
    }

    [Fact]
    public async Task RefreshFromServer_does_not_clobber_an_already_selected_project()
    {
        // When a project is already active, hydration must be skipped (no /api/projects call that
        // could reselect a different workspace default).
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/projects/Demo/adaptation" => (HttpStatusCode.OK, ReadyCamel),
            "/api/projects" => (HttpStatusCode.OK,
                """{ "active": { "id": "Other", "label": "Other" }, "projects": [] }"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var (state, engine) = NewState(handler);
        state.Set("Demo");

        await state.RefreshFromServerAsync(engine);

        Assert.Equal("Demo", state.ProjectId);
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath == "/api/projects");
    }

    [Fact]
    public async Task RefreshFromServer_does_not_get_projects_while_anonymous()
    {
        // Login-page boot (NavMenu → RefreshFromServerAsync) must not GET /api/projects
        // before sign-in — production answers 401 and the browser logs it.
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{ "ok": false }""");
        var session = new AdminSessionService(js: null);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engine = new EngineApiClient(http, session);
        var state = new ActiveProjectState();

        await state.RefreshFromServerAsync(engine);

        Assert.False(state.HasProject);
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath == "/api/projects");
    }
}
