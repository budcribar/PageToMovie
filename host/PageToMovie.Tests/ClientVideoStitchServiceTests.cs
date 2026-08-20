using System.Net;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class ClientVideoStitchServiceTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_PreservesUserCustomSceneOverride_WhenIsUserOverrideIsTrue()
    {
        // Arrange: scene 1 has a custom user scene override
        var projectId = "test-project";
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var sceneSummaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, CompositeExists = true, IsUserOverride = true, ClipsOnDisk = 2 }
        };

        // Act
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, sceneSummaries, staleScenes: null);

        // Assert: MUST return custom user composite URL (1 URL) to preserve editor scene overrides
        Assert.Single(urls);
        Assert.Contains("scenes/1/composite", urls[0]);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_PrefersAtomicClips_PreventsStaleCompositeDuplication()
    {
        // Arrange: scene 1 has both a composite AND individual clips on disk
        var projectId = "test-project";
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = 1,
                compositeExists = true,
                clips = new[]
                {
                    new { clipNumber = 1, onDisk = true },
                    new { clipNumber = 2, onDisk = true }
                }
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("/scenes/1") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sceneDetailJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var sceneSummaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, CompositeExists = true, ClipsOnDisk = 2 }
        };

        // Act
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, sceneSummaries, staleScenes: null);

        // Assert: MUST return individual clips ONLY (2 clip URLs), avoiding stale composite files
        Assert.Equal(2, urls.Count);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Contains("scenes/1/clips/2/video", urls[1]);
        Assert.DoesNotContain(urls, u => u.Contains("/composite"));
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_PrefersIndividualClips_WhenCompositeIsStale()
    {
        // Arrange: scene 1 composite exists BUT scene is stale (clips were edited/regenerated)
        var projectId = "test-project";
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = 1,
                compositeExists = true,
                clips = new[]
                {
                    new { clipNumber = 1, onDisk = true },
                    new { clipNumber = 2, onDisk = true }
                }
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("/scenes/1") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sceneDetailJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var sceneSummaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, CompositeExists = true, ClipsOnDisk = 2 }
        };

        // Act: mark scene 1 as stale
        var staleScenes = new HashSet<int> { 1 };
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, sceneSummaries, staleScenes);

        // Assert: MUST return individual clips ONLY (2 clip URLs), and 0 composite URLs
        Assert.Equal(2, urls.Count);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Contains("scenes/1/clips/2/video", urls[1]);
        Assert.DoesNotContain(urls, u => u.Contains("/composite"));
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_StrictlyOrdersScenesAndClipsSequentially()
    {
        // Arrange: scene 1 and scene 2 requested out-of-order
        var projectId = "test-project";
        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/scenes/1"))
            {
                var s1 = JsonSerializer.Serialize(new
                {
                    ok = true,
                    scene = new
                    {
                        sceneNumber = 1,
                        clips = new[] { new { clipNumber = 1, onDisk = true } }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s1) };
            }
            if (path.Contains("/scenes/2"))
            {
                var s2 = JsonSerializer.Serialize(new
                {
                    ok = true,
                    scene = new
                    {
                        sceneNumber = 2,
                        clips = new[] { new { clipNumber = 1, onDisk = true } }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s2) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        // Act: Pass scenes out of order [2, 1]
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 2, 1 }, null, null);

        // Assert: Must be strictly sorted [scene 1, then scene 2]
        Assert.Equal(2, urls.Count);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Contains("scenes/2/clips/1/video", urls[1]);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_FallsBackToComposite_WhenNoIndividualClipsExist()
    {
        // Arrange: scene 1 has no individual clips, but composite exists
        var projectId = "test-project";
        var handler = new FakeHttpMessageHandler(req =>
        {
            var s = JsonSerializer.Serialize(new
            {
                ok = true,
                scene = new
                {
                    sceneNumber = 1,
                    compositeExists = true,
                    clips = Array.Empty<object>()
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s) };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var sceneSummaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, CompositeExists = true, ClipsOnDisk = 0 }
        };

        // Act
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, sceneSummaries, null);

        // Assert: Must fall back to composite URL
        Assert.Single(urls);
        Assert.Contains("scenes/1/composite", urls[0]);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_IgnoresMissingOrDeletedClips_OnlyGathersActiveOnDiskClips()
    {
        // Arrange: scene 1 has 3 planned clips, but clip 2 was deleted / missing on disk
        var projectId = "test-project";
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = 1,
                clips = new[]
                {
                    new { clipNumber = 1, onDisk = true },
                    new { clipNumber = 2, onDisk = false }, // deleted or missing clip
                    new { clipNumber = 3, onDisk = true }
                }
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sceneDetailJson, System.Text.Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        // Act: mark scene 1 as stale to force clip gathering
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, null, new HashSet<int> { 1 });

        // Assert: Must return 2 URLs (clip 1 and clip 3), skipping missing clip 2
        Assert.Equal(2, urls.Count);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Contains("scenes/1/clips/3/video", urls[1]);
        Assert.DoesNotContain(urls, u => u.Contains("clips/2/video"));
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_HandlesNewlyAddedClips_InSequentialOrder()
    {
        // Arrange: scene 1 has a newly added 3rd clip
        var projectId = "test-project";
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = 1,
                clips = new[]
                {
                    new { clipNumber = 1, onDisk = true },
                    new { clipNumber = 2, onDisk = true },
                    new { clipNumber = 3, onDisk = true } // newly added clip
                }
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sceneDetailJson, System.Text.Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        // Act
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, null, new HashSet<int> { 1 });

        // Assert: Returns all 3 clips sequentially
        Assert.Equal(3, urls.Count);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Contains("scenes/1/clips/2/video", urls[1]);
        Assert.Contains("scenes/1/clips/3/video", urls[2]);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_GracefullySkipsScenesWithNoMedia()
    {
        // Arrange: scene 1 has clips, scene 2 has NO clips and NO composite (empty/deleted scene)
        var projectId = "test-project";
        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/scenes/1"))
            {
                var s1 = JsonSerializer.Serialize(new
                {
                    ok = true,
                    scene = new
                    {
                        sceneNumber = 1,
                        clips = new[] { new { clipNumber = 1, onDisk = true } }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s1) };
            }
            if (path.Contains("/scenes/2"))
            {
                var s2 = JsonSerializer.Serialize(new
                {
                    ok = true,
                    scene = new
                    {
                        sceneNumber = 2,
                        compositeExists = false,
                        clips = Array.Empty<object>()
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s2) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        // Act: Request scenes 1 and 2
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1, 2 }, null, new HashSet<int> { 1, 2 });

        // Assert: Only scene 1's clip is gathered; scene 2 is gracefully skipped
        Assert.Single(urls);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_UsesCompositeUrl_WhenClipsNotOnDisk()
    {
        // Arrange: scene 1 summary indicates composite exists, no individual clips on disk
        var projectId = "test-project";
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var sceneSummaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, CompositeExists = true, ClipsOnDisk = 0 }
        };

        // Act
        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, sceneSummaries, staleScenes: null);

        // Assert: Must return composite URL fallback when no individual clips exist
        Assert.Single(urls);
        Assert.Contains("scenes/1/composite", urls[0]);
    }

    [Fact]
    public void ClientStitchResult_HasPlayableUrl_And_StitchError()
    {
        var ok = ClientStitchResult.Ok("blob:1");
        Assert.True(ok.HasPlayableUrl);
        Assert.Equal("Browser stitch failed", ok.StitchError);

        var fail = ClientStitchResult.Fail("nope");
        Assert.False(fail.HasPlayableUrl);
        Assert.Equal("nope", fail.StitchError);

        var blank = new ClientStitchResult { Success = true, Url = "  " };
        Assert.False(blank.HasPlayableUrl);
        Assert.Equal("Browser stitch failed", blank.StitchError);
    }

    [Fact]
    public async Task TryConcatSceneClipsAsync_EmptyUrls_CallsOnFail_WithoutStatus()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var stitch = new ClientVideoStitchService(null!, new EngineApiClient(httpClient));

        string? status = null;
        string? fail = null;
        var url = await stitch.TryConcatSceneClipsAsync(
            Array.Empty<string>(),
            "No on-disk clips for S01",
            s => status = s,
            e => fail = e);

        Assert.Null(url);
        Assert.Null(status);
        Assert.Equal("No on-disk clips for S01", fail);
    }
}

