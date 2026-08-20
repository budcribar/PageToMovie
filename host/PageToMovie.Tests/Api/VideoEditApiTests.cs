using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Prompt-based clip edit (xAI /v1/videos/edits) end-to-end through the real job-queue HTTP
/// pipeline with fakes: submits POST /api/jobs/video-edit, polls the job to completion, and
/// verifies the result actually lands in the existing Takes system — the current active clip's
/// prior version is archived to history/, the edited bytes become the new active clip, and the
/// sidecar records the edit prompt + provenance. Follows this session's
/// CreditsSceneVideoGenGuardApiTests.cs pattern (PageToMovieApiFactory + job-polling helper).
/// </summary>
public class VideoEditApiTests : IClassFixture<PageToMovieApiFactory>, IAsyncLifetime
{
    private readonly PageToMovieApiFactory _factory;
    private readonly HttpClient _client;
    private string _projectId = "";

    public VideoEditApiTests(PageToMovieApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    public async Task InitializeAsync()
    {
        var slug = "VideoEdit_" + Guid.NewGuid().ToString("N")[..8];
        var create = await _client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Video Edit" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        _projectId = created.GetProperty("active").GetProperty("id").GetString() ?? slug;

        var act = await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
        Assert.True(act.IsSuccessStatusCode, await act.Content.ReadAsStringAsync());

        // Seed a blueprint with one normal scene/clip — video-edit resolves its own model from the
        // VideoEdit capability's catalog default, not project config, so no config PUT is needed.
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var projectDir = store.GetProjectDir(_projectId);
        var blueprintPath = Path.Combine(projectDir, "blueprint.clips.grok.json");
        var blueprint = new JsonObject
        {
            ["movie_title"] = "Video Edit Test",
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
            },
        };
        File.WriteAllText(blueprintPath, blueprint.ToJsonString());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Seeds the active clip file + sidecar on disk so there's something to edit.</summary>
    private void SeedActiveClip(int scene, int clip, double durationSeconds)
    {
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        Directory.CreateDirectory(videoDir);
        var mp4Path = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        File.WriteAllBytes(mp4Path, "original-clip-bytes"u8.ToArray());

        var sidecarPath = Path.ChangeExtension(mp4Path, ".clip.json");
        var sidecar = new JsonObject
        {
            ["schema_version"] = "clip_sidecar.v1",
            ["project_id"] = _projectId,
            ["scene"] = scene,
            ["clip"] = clip,
            ["script_text"] = "",
            ["visual_prompt"] = "original prompt",
            ["model"] = "grok-imagine-video",
            ["resolution"] = "480p",
            ["duration_seconds"] = durationSeconds,
            ["sha256"] = "",
            ["size_bytes"] = new FileInfo(mp4Path).Length,
            ["created_at_utc"] = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(sidecarPath, sidecar.ToJsonString());
    }

    private async Task<(string Status, string? Error, JsonElement Job)> RunJobToCompletionAsync(string startPath, object body)
    {
        var start = await _client.PostAsJsonAsync(startPath, body);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var startJson = await start.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startJson.GetProperty("job").GetProperty("jobId").GetString()!;

        var job = default(JsonElement);
        var status = "queued";
        for (var i = 0; i < 200; i++)
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
    public async Task Edit_saves_the_result_as_a_new_take_and_archives_the_prior_active_clip()
    {
        SeedActiveClip(scene: 1, clip: 1, durationSeconds: 4.0);
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        var activeMp4Path = Path.Combine(videoDir, "scene_01_clip_01.mp4");
        var originalBytes = File.ReadAllBytes(activeMp4Path);

        var (status, error, job) = await RunJobToCompletionAsync("/api/jobs/video-edit", new
        {
            projectId = _projectId,
            scene = 1,
            clip = 1,
            prompt = "change her jacket to red",
        });

        Assert.Equal("done", status);
        Assert.Null(error);

        // The prior active clip must be archived into history/, not silently discarded.
        var historyDir = Path.Combine(videoDir, "history");
        Assert.True(Directory.Exists(historyDir), $"expected a history/ dir; job: {job}");
        var archived = Directory.GetFiles(historyDir, "scene_01_clip_01_*.mp4");
        Assert.True(archived.Length >= 1, "expected the pre-edit clip archived into history/");
        Assert.Equal(originalBytes, File.ReadAllBytes(archived[0]));

        // The active clip's bytes must have changed (replaced by the edited result).
        var newBytes = File.ReadAllBytes(activeMp4Path);
        Assert.NotEqual(originalBytes, newBytes);

        // Sidecar records the edit prompt and which take it was derived from.
        var sidecarPath = Path.ChangeExtension(activeMp4Path, ".clip.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal("change her jacket to red", doc.RootElement.GetProperty("visual_prompt").GetString());
        Assert.True(doc.RootElement.TryGetProperty("edited_from_take", out _), "expected edited_from_take provenance");

        // The versions list must show one more entry, with the new one current.
        var versionsResp = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes/1/clips/1/versions");
        Assert.True(versionsResp.IsSuccessStatusCode, await versionsResp.Content.ReadAsStringAsync());
        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonElement>();
        var versions = versionsJson.GetProperty("versions").EnumerateArray().ToList();
        Assert.True(versions.Count >= 2, $"expected >=2 versions (original archived + new active), got {versions.Count}");
        Assert.Contains(versions, v => v.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Edit_is_rejected_server_side_when_the_clip_exceeds_the_input_duration_cap()
    {
        SupportedModelCatalog.ReloadCatalog();
        var cap = SupportedModelCatalog.VideoEditMaxInputDurationSeconds();
        Assert.True(cap is > 0, "VideoEdit catalog must publish maxEditInputDurationSeconds");
        SeedActiveClip(scene: 1, clip: 1, durationSeconds: cap.Value + 3.3);

        var (status, error, _) = await RunJobToCompletionAsync("/api/jobs/video-edit", new
        {
            projectId = _projectId,
            scene = 1,
            clip = 1,
            prompt = "change her jacket to red",
        });

        Assert.Equal("error", status);
        Assert.Contains(cap.Value.ToString("0.#"), error ?? "");
    }
}
