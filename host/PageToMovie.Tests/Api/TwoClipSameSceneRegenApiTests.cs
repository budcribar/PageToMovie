using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Regenerating C1+C2 in one scene (gen-batch clips) must finish as a job error/done,
/// never take down the API host. C2 is an extend hop; C1 is a fresh 1.5 generate.
/// Leftover local C1 bytes from a previous take must not be re-chained.
/// </summary>
public class TwoClipSameSceneRegenApiTests : IClassFixture<PageToMovieApiFactory>, IAsyncLifetime
{
    private readonly PageToMovieApiFactory _factory;
    private readonly HttpClient _client;
    private string _projectId = "";

    public TwoClipSameSceneRegenApiTests(PageToMovieApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    public async Task InitializeAsync()
    {
        var slug = "TwoClipRegen_" + Guid.NewGuid().ToString("N")[..8];
        var create = await _client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Two Clip Regen" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        _projectId = created.GetProperty("active").GetProperty("id").GetString() ?? slug;

        var act = await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
        Assert.True(act.IsSuccessStatusCode, await act.Content.ReadAsStringAsync());

        var cfg = await _client.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/config",
            new Dictionary<string, object?>
            {
                ["model_name"] = "imagine-video-1.5-extend",
                ["resolution"] = "480p",
            });
        Assert.True(cfg.IsSuccessStatusCode, await cfg.Content.ReadAsStringAsync());

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        _factory.StampDecidedVision(_projectId);
        var projectDir = store.GetProjectDir(_projectId);
        var blueprintPath = Path.Combine(projectDir, "blueprint.clips.grok.json");
        var blueprint = new JsonObject
        {
            ["movie_title"] = "Two Clip Regen",
            ["scenes"] = new JsonArray
            {
                new JsonObject
                {
                    ["scene_number"] = 2,
                    ["setting"] = "INT. HALL - DAY",
                    ["veo_clips"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["clip_number"] = 1,
                            ["visual_prompt"] = "A quiet hall, static shot.",
                            ["veo_continuation_source"] = "none",
                            ["audio_payload"] = new JsonObject { ["speaker"] = "", ["dialogue"] = "" },
                        },
                        new JsonObject
                        {
                            ["clip_number"] = 2,
                            ["visual_prompt"] = "The same hall, a step forward.",
                            ["veo_continuation_source"] = "extend_previous",
                            ["audio_payload"] = new JsonObject { ["speaker"] = "", ["dialogue"] = "" },
                        },
                    },
                },
            },
        };
        File.WriteAllText(blueprintPath, blueprint.ToJsonString());

        // Stale leftover from a previous combined hop — the crash path used this
        // after C1's new take MP4 was deleted and had no file_id yet.
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_02_clip_01_take_01.mp4"), new byte[80_000]);
        File.WriteAllText(
            Path.Combine(videoDir, FilmJobService.ExtendSourceMarkerName(2, 2)),
            """{"file_id":"file_stale_old_c1","duration_seconds":80}""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GenBatch_C1_and_C2_same_scene_completes_without_killing_the_host()
    {
        var start = await _client.PostAsJsonAsync("/api/jobs/gen-batch", new
        {
            projectId = _projectId,
            requireLockedCharacters = false,
            clips = new[]
            {
                new { scene = 2, clip = 1 },
                new { scene = 2, clip = 2 },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var startJson = await start.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startJson.GetProperty("job").GetProperty("jobId").GetString()!;

        var job = default(JsonElement);
        var status = "queued";
        for (var i = 0; i < 400; i++)
        {
            var resp = await _client.GetAsync($"/api/jobs/{jobId}");
            Assert.True(resp.IsSuccessStatusCode, "heartbeat-equivalent job poll must stay reachable");
            var jobJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
            job = jobJson.GetProperty("job");
            status = job.GetProperty("status").GetString() ?? "";
            if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(150);
        }

        Assert.True(
            status is "done" or "partial",
            $"two-clip regen ended '{status}': {job}");

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        Assert.True(ClipSidecarService.ReadCurrentTake(videoDir, 2, 1) >= 1);
        Assert.True(ClipSidecarService.ReadCurrentTake(videoDir, 2, 2) >= 1);

        var live = await _client.GetAsync("/api/health");
        Assert.True(live.IsSuccessStatusCode || live.StatusCode == HttpStatusCode.NotFound);
        var heartbeat = await _client.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/presence/heartbeat", null);
        Assert.True(
            heartbeat.IsSuccessStatusCode || heartbeat.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"presence heartbeat must not 502 after two-clip regen (got {(int)heartbeat.StatusCode})");
    }
}
