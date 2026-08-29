using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// The end-credits scene must never reach the AI video model — it is rendered deterministically
/// client-side (canvas -> ffmpeg.wasm) because a video model asked to render a text-heavy title card
/// hallucinates unrelated footage. The Scenes page already diverts credits scenes before calling the
/// gen-scene/gen-batch endpoints, but <see cref="FilmJobService.RunSceneGenAsync"/> and
/// <see cref="FilmJobService.RunBatchGenAsync"/> must refuse/skip them too, in case some other caller
/// (stale scene list, retry, admin batch) reaches these endpoints directly. Exercises the real HTTP
/// job pipeline with fakes so the guard is proven end-to-end, not just the
/// <see cref="ProjectStore.IsCreditsScene(JsonElement?)"/> predicate (see CreditsSceneCastGateTests).
/// </summary>
public class CreditsSceneVideoGenGuardApiTests : IClassFixture<PageToMovieApiFactory>, IAsyncLifetime
{
    private readonly PageToMovieApiFactory _factory;
    private readonly HttpClient _client;
    private string _projectId = "";

    public CreditsSceneVideoGenGuardApiTests(PageToMovieApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    public async Task InitializeAsync()
    {
        var slug = "CreditsGuard_" + Guid.NewGuid().ToString("N")[..8];
        var create = await _client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Credits Guard" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        _projectId = created.GetProperty("active").GetProperty("id").GetString() ?? slug;

        var act = await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
        Assert.True(act.IsSuccessStatusCode, await act.Content.ReadAsStringAsync());

        // Pick a video model so the normal scene can actually generate (RequireVideo throws "no
        // model selected" without an explicit choice — fakes never invent a default). The fake
        // catalog is a copy of the real rows plus the Fake Studio vendor, so this id resolves the
        // same under fakes — IVideoClient is swapped for FakeGrokVideoClient, not the catalog.
        var cfg = await _client.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/config",
            new Dictionary<string, object?> { ["model_name"] = "grok-imagine-video", ["resolution"] = "480p" });
        Assert.True(cfg.IsSuccessStatusCode, await cfg.Content.ReadAsStringAsync());

        // Seed a blueprint with a normal scene (1) and an end-credits scene (2), in the same shape
        // ProjectStore.AddCreditsScene writes (scene-level + clip-level is_credits).
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        _factory.StampDecidedVision(_projectId);
        var blueprintPath = Path.Combine(store.GetProjectDir(_projectId), "blueprint.clips.grok.json");
        var blueprint = new JsonObject
        {
            ["movie_title"] = "Credits Guard Test",
            ["scenes"] = new JsonArray
            {
                new JsonObject
                {
                    ["scene_number"] = 1,
                    ["setting"] = "INT. ROOM - DAY",
                    ["veo_clips"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["clip_number"] = 1,
                            ["visual_prompt"] = "A quiet room, static shot.",
                            ["audio_payload"] = new JsonObject { ["speaker"] = "", ["dialogue"] = "" },
                        },
                    },
                },
                new JsonObject
                {
                    ["scene_number"] = 2,
                    ["setting"] = "END CREDITS",
                    ["is_credits"] = true,
                    ["veo_clips"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["clip_number"] = 1,
                            ["is_credits"] = true,
                            ["visual_prompt"] = "Elegant cinematic end-credits title card.",
                            ["audio_payload"] = new JsonObject { ["speaker"] = "", ["dialogue"] = "" },
                        },
                    },
                },
            },
        };
        File.WriteAllText(blueprintPath, blueprint.ToJsonString());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(string Status, string? Error, JsonElement Job)> RunJobToCompletionAsync(string startPath, object body)
    {
        var start = await _client.PostAsJsonAsync(startPath, body);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var startJson = await start.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startJson.GetProperty("job").GetProperty("jobId").GetString()!;

        var job = default(JsonElement);
        var status = "queued";
        for (var i = 0; i < 400; i++)
        {
            var resp = await _client.GetAsync($"/api/jobs/{jobId}");
            var jobJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
            job = jobJson.GetProperty("job");
            status = job.GetProperty("status").GetString() ?? "";
            if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(150);
        }

        var error = job.TryGetProperty("error", out var e) ? e.GetString() : null;
        return (status, error, job);
    }

    [Fact]
    public async Task GenScene_on_credits_scene_errors_without_calling_the_video_model()
    {
        var (status, error, _) = await RunJobToCompletionAsync("/api/jobs/gen-scene", new
        {
            projectId = _projectId,
            scene = 2,
            requireLockedCharacters = false,
        });

        Assert.Equal("error", status);
        Assert.Contains("credits", error ?? "", StringComparison.OrdinalIgnoreCase);

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01.mp4")),
            "The credits clip must never be sent to the video model.");
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4")),
            "The credits clip must never produce a take file.");
    }

    [Fact]
    public async Task GenBatch_skips_the_credits_scene_but_still_generates_the_normal_scene()
    {
        var (status, _, job) = await RunJobToCompletionAsync("/api/jobs/gen-batch", new
        {
            projectId = _projectId,
            scenes = new[] { 1, 2 },
            requireLockedCharacters = false,
        });

        Assert.True(status is "done" or "partial", $"batch ended '{status}': {job}");

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4")),
            "The normal scene must still be generated by the batch.");
        Assert.Equal(1, ClipSidecarService.ReadCurrentTake(videoDir, 1, 1));
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_01_clip_01.mp4")),
            "Batch gen must not write a leftover current-clip alias.");
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01.mp4")),
            "The credits scene must never be sent to the video model, even inside a mixed batch.");
        Assert.False(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4")),
            "The credits scene must never produce a take file.");

        var log = job.TryGetProperty("log", out var l)
            ? l.EnumerateArray().Select(x => x.GetString()).ToList()
            : new List<string?>();
        Assert.Contains(log, line => line != null && line.Contains("credits", StringComparison.OrdinalIgnoreCase));
    }
}
