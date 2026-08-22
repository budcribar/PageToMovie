using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
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

        // Leftover bare alias is not refreshed as the player file.
        Assert.Equal(originalBytes, File.ReadAllBytes(activeMp4Path));
        var take02Mp4 = Path.Combine(videoDir, "scene_01_clip_01_take_02.mp4");
        Assert.True(File.Exists(take02Mp4));
        Assert.NotEqual(originalBytes, File.ReadAllBytes(take02Mp4));

        // Take identity is the numbered sidecar, not the leftover player-alias .clip.json.
        var take01Sidecar = Path.Combine(videoDir, "scene_01_clip_01_take_01.clip.json");
        var take02Sidecar = Path.Combine(videoDir, "scene_01_clip_01_take_02.clip.json");
        Assert.True(File.Exists(take01Sidecar), "original must stay as take 1");
        Assert.True(File.Exists(take02Sidecar), "edit must be a new take_02 sidecar");
        using (var origDoc = JsonDocument.Parse(File.ReadAllText(take01Sidecar)))
            Assert.Equal("original prompt", origDoc.RootElement.GetProperty("visual_prompt").GetString());
        using (var editDoc = JsonDocument.Parse(File.ReadAllText(take02Sidecar)))
        {
            Assert.Equal("change her jacket to red", editDoc.RootElement.GetProperty("visual_prompt").GetString());
            Assert.Equal(2, editDoc.RootElement.GetProperty("take").GetInt32());
            Assert.Equal(1, editDoc.RootElement.GetProperty("edited_from_take").GetInt32());
        }
        using (var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(videoDir, "scene_01_clip_01.current.json"))))
            Assert.Equal(2, pointer.RootElement.GetProperty("take").GetInt32());
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_01_take_02.mp4")));
        AssertPublishedClientTake(job, scene: 1, clip: 1, take: 2);

        // The versions list must show one more entry, with the new one current.
        var versionsResp = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes/1/clips/1/versions");
        Assert.True(versionsResp.IsSuccessStatusCode, await versionsResp.Content.ReadAsStringAsync());
        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonElement>();
        var versions = versionsJson.GetProperty("versions").EnumerateArray().ToList();
        Assert.True(versions.Count >= 2, $"expected >=2 versions (original archived + new active), got {versions.Count}");
        var current = versions.Single(v => v.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(2, current.GetProperty("take").GetInt32());
        var takeNumbers = versions.Select(v => v.GetProperty("take").GetInt32()).ToList();
        Assert.Equal(takeNumbers.Count, takeNumbers.Distinct().Count());
        Assert.Contains(1, takeNumbers);
        Assert.Contains(2, takeNumbers);
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

    [Fact]
    public async Task Edit_keeps_the_previous_take_and_a_second_edit_publishes_the_next_take()
    {
        SeedActiveClip(scene: 1, clip: 1, durationSeconds: 4.0);
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        var take01Mp4 = Path.Combine(videoDir, "scene_01_clip_01_take_01.mp4");
        var take01Bytes = "original-take-01-bytes"u8.ToArray();
        File.WriteAllBytes(take01Mp4, take01Bytes);
        File.WriteAllText(Path.ChangeExtension(take01Mp4, ".clip.json"), new JsonObject
        {
            ["schema_version"] = "clip_sidecar.v1",
            ["project_id"] = _projectId,
            ["scene"] = 1,
            ["clip"] = 1,
            ["take"] = 1,
            ["script_text"] = "",
            ["visual_prompt"] = "original prompt",
            ["model"] = "",
            ["resolution"] = "480p",
            ["duration_seconds"] = 4.0,
            ["sha256"] = "",
            ["size_bytes"] = take01Bytes.Length,
            ["created_at_utc"] = DateTime.UtcNow.ToString("o"),
        }.ToJsonString());

        var first = await RunJobToCompletionAsync("/api/jobs/video-edit", new
        {
            projectId = _projectId,
            scene = 1,
            clip = 1,
            prompt = "first edit",
        });
        Assert.Equal("done", first.Status);
        Assert.Null(first.Error);
        AssertPublishedClientTake(first.Job, scene: 1, clip: 1, take: 2);
        Assert.True(File.Exists(take01Mp4), "previous take MP4 must remain");
        Assert.Equal(take01Bytes, File.ReadAllBytes(take01Mp4));
        var take02Mp4 = Path.Combine(videoDir, "scene_01_clip_01_take_02.mp4");
        Assert.True(File.Exists(take02Mp4));
        var take02Bytes = File.ReadAllBytes(take02Mp4);

        var second = await RunJobToCompletionAsync("/api/jobs/video-edit", new
        {
            projectId = _projectId,
            scene = 1,
            clip = 1,
            prompt = "second edit",
        });
        Assert.Equal("done", second.Status);
        Assert.Null(second.Error);
        AssertPublishedClientTake(second.Job, scene: 1, clip: 1, take: 3);
        Assert.True(File.Exists(take01Mp4), "take 1 still present after second edit");
        Assert.Equal(take01Bytes, File.ReadAllBytes(take01Mp4));
        Assert.True(File.Exists(take02Mp4), "take 2 still present after second edit");
        Assert.Equal(take02Bytes, File.ReadAllBytes(take02Mp4));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_01_take_03.mp4")));
        using var take03 = JsonDocument.Parse(File.ReadAllText(Path.Combine(videoDir, "scene_01_clip_01_take_03.clip.json")));
        Assert.Equal(3, take03.RootElement.GetProperty("take").GetInt32());
        Assert.True(take03.RootElement.TryGetProperty("edited_from_take", out var from));
        Assert.True(from.GetInt32() >= 1);
        var firstJobId = first.Job.GetProperty("jobId").GetString();
        var secondJobId = second.Job.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstJobId));
        Assert.False(string.IsNullOrWhiteSpace(secondJobId));
        Assert.NotEqual(firstJobId, secondJobId);
        Assert.NotEqual(
            ClipTakeNaming.JobMediaSaveKey(_projectId, firstJobId, ClipTakeNaming.TakeRelativePath(1, 1, 2), 2),
            ClipTakeNaming.JobMediaSaveKey(_projectId, secondJobId, ClipTakeNaming.TakeRelativePath(1, 1, 3), 3));
    }

    [Fact]
    public async Task Edit_of_an_is_credits_clip_still_writes_a_numbered_take()
    {
        SeedActiveClip(scene: 2, clip: 1, durationSeconds: 4.0);
        var (status, error, job) = await RunJobToCompletionAsync("/api/jobs/video-edit", new
        {
            projectId = _projectId,
            scene = 2,
            clip = 1,
            prompt = "brighten the card",
        });

        Assert.Equal("done", status);
        Assert.Null(error);
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var videoDir = Path.Combine(store.GetProjectDir(_projectId), "assets", "video");
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_02.mp4")));
        Assert.True(File.Exists(Path.Combine(videoDir, "scene_02_clip_01_take_02.clip.json")));
        AssertPublishedClientTake(job, scene: 2, clip: 1, take: 2);
    }

    private static void AssertPublishedClientTake(JsonElement job, int scene, int clip, int take)
    {
        Assert.Equal(ClipTakeNaming.TakeRelativePath(scene, clip, take), job.GetProperty("clientRelativePath").GetString());
        Assert.Equal(take, job.GetProperty("clientTakeNumber").GetInt32());
        var url = job.GetProperty("clientMediaUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.StartsWith("/api/media/proxy/", url);
        Assert.False(string.Equals(
            job.GetProperty("clientRelativePath").GetString(),
            ClipTakeNaming.CanonicalRelativePath(scene, clip),
            StringComparison.OrdinalIgnoreCase));
    }
}
