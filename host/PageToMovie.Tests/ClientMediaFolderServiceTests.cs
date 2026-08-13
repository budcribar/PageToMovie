using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Covers the Step 2 fix from docs/archive/client-storage-implementation-plan.md: OnJobUpdated must accept both
/// "running" and "done" so every clip in a multi-clip batch gets saved (Status stays "running" for
/// the whole batch loop — only the final clip's tick flips to "done"), while a second notification
/// for a path that already finished saving (e.g. a single-clip job's "done" tick after its "running"
/// tick already saved it) must be a no-op rather than a duplicate download+hash+write.
/// </summary>
public class ClientMediaFolderServiceTests
{
    private static (ClientMediaFolderService svc, FakeJsRuntime js) CreateService(HttpMessageHandler? handler = null)
    {
        var js = new FakeJsRuntime();
        var http = new HttpClient(handler ?? new StubHandler()) { BaseAddress = new Uri("http://localhost") };
        var api = new EngineApiClient(http);
        var hub = new JobHubClient(Options.Create(new EngineApiOptions()));
        var activeProject = new ActiveProjectState();
        var svc = new ClientMediaFolderService(js, api, hub, activeProject);
        return (svc, js);
    }

    private static void FireOnJobUpdated(ClientMediaFolderService svc, JobSnapshot snap)
    {
        var method = typeof(ClientMediaFolderService).GetMethod(
            "OnJobUpdated", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(svc, new object?[] { snap });
    }

    [Fact]
    public async Task Each_clip_in_a_multi_clip_batch_is_saved_even_though_status_stays_running()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.connectFolderAsync"] = """{"success":true,"folderName":"Test"}""";
        js.Responses["PageToMovieFfmpeg.analyzeSilenceAsync"] = """{"success":false,"error":"no ffmpeg in test"}""";
        js.Responses["PageToMovieMedia.saveFromUrlAsync"] = """{"success":true,"sha256":"abc","sizeBytes":100,"relativePath":"x"}""";

        var clip1 = new JobSnapshot
        {
            Status = "running",
            ProjectId = "proj1",
            ClientMediaUrl = "/api/media/proxy/tok1",
            ClientRelativePath = "assets/video/scene_01_clip_01.mp4",
            Scene = 1,
            Clip = 1,
        };
        var clip2 = new JobSnapshot
        {
            Status = "running", // batch stays "running" for every clip but the last
            ProjectId = "proj1",
            ClientMediaUrl = "/api/media/proxy/tok2",
            ClientRelativePath = "assets/video/scene_01_clip_02.mp4",
            Scene = 1,
            Clip = 2,
        };

        FireOnJobUpdated(svc, clip1);
        await WaitForIdleAsync(svc);
        FireOnJobUpdated(svc, clip2);
        await WaitForIdleAsync(svc);

        Assert.Equal(2, js.CallCount("PageToMovieMedia.saveFromUrlAsync"));
    }

    [Fact]
    public async Task A_later_done_tick_for_an_already_saved_path_is_not_re_saved()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.connectFolderAsync"] = """{"success":true,"folderName":"Test"}""";
        js.Responses["PageToMovieFfmpeg.analyzeSilenceAsync"] = """{"success":false,"error":"no ffmpeg in test"}""";
        js.Responses["PageToMovieMedia.saveFromUrlAsync"] = """{"success":true,"sha256":"abc","sizeBytes":100,"relativePath":"x"}""";

        var running = new JobSnapshot
        {
            Status = "running",
            ProjectId = "proj1",
            ClientMediaUrl = "/api/media/proxy/tok1",
            ClientRelativePath = "assets/video/scene_01_clip_01.mp4",
            Scene = 1,
            Clip = 1,
        };
        var done = new JobSnapshot
        {
            Status = "done", // same path, carried over from the last clip's snapshot
            ProjectId = running.ProjectId,
            ClientMediaUrl = running.ClientMediaUrl,
            ClientRelativePath = running.ClientRelativePath,
            Scene = running.Scene,
            Clip = running.Clip,
        };

        FireOnJobUpdated(svc, running);
        await WaitForIdleAsync(svc);
        FireOnJobUpdated(svc, done);
        await WaitForIdleAsync(svc);

        Assert.Equal(1, js.CallCount("PageToMovieMedia.saveFromUrlAsync"));
    }

    [Fact]
    public async Task TryReconnect_succeeds_silently_when_the_browser_still_grants_permission()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.tryReconnectAsync"] = """{"success":true,"folderName":"MyMovies","silent":true}""";

        await svc.TryReconnectAsync();

        Assert.True(svc.IsConnected);
        Assert.Equal("MyMovies", svc.FolderName);
        Assert.False(svc.NeedsReconnect);
    }

    [Fact]
    public async Task TryReconnect_surfaces_a_reconnect_prompt_when_permission_needs_a_gesture()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.tryReconnectAsync"] = """{"success":false,"reason":"prompt","folderName":"MyMovies"}""";

        await svc.TryReconnectAsync();

        Assert.False(svc.IsConnected);
        Assert.True(svc.NeedsReconnect);
        Assert.Equal("MyMovies", svc.PendingReconnectFolderName);
    }

    [Fact]
    public async Task TryReconnect_is_a_no_op_when_no_folder_was_ever_connected()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.tryReconnectAsync"] = """{"success":false,"reason":"none"}""";

        await svc.TryReconnectAsync();

        Assert.False(svc.IsConnected);
        Assert.False(svc.NeedsReconnect);
    }

    [Fact]
    public async Task ReconnectAsync_from_a_button_click_completes_the_pending_reconnect()
    {
        var (svc, js) = CreateService();
        js.Responses["PageToMovieMedia.tryReconnectAsync"] = """{"success":false,"reason":"prompt","folderName":"MyMovies"}""";
        await svc.TryReconnectAsync();
        Assert.True(svc.NeedsReconnect);

        js.Responses["PageToMovieMedia.reconnectAsync"] = """{"success":true,"folderName":"MyMovies"}""";
        var ok = await svc.ReconnectAsync();

        Assert.True(ok);
        Assert.True(svc.IsConnected);
        Assert.False(svc.NeedsReconnect);
    }

    private static async Task WaitForIdleAsync(ClientMediaFolderService svc)
    {
        // SaveJobMediaAsync is fired via `_ = SaveJobMediaAsync(snap)` (fire-and-forget); give the
        // in-process async chain a moment to reach its awaited JS/HTTP fakes and complete.
        for (var i = 0; i < 50; i++)
            await Task.Delay(10);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public Dictionary<string, string> Responses { get; } = new();
        private readonly Dictionary<string, int> _calls = new();

        public int CallCount(string identifier) => _calls.TryGetValue(identifier, out var n) ? n : 0;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            lock (_calls)
                _calls[identifier] = _calls.TryGetValue(identifier, out var n) ? n + 1 : 1;

            if (!Responses.TryGetValue(identifier, out var json))
            {
                // Default: a generic success-shaped payload covers InvokeVoidAsync (TValue=object) calls.
                json = """{"success":true}""";
            }

            var result = JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;
            return ValueTask.FromResult(result);
        }
    }
}
