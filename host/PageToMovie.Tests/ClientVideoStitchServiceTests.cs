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
    public async Task CollectSceneMediaUrlsAsync_DoesNotStitchAroundAMissingClip()
    {
        // Arrange: scene 1 has 3 planned clips, but clip 2 is missing (404)
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
                    new { clipNumber = 2, onDisk = false },
                    new { clipNumber = 3, onDisk = true }
                }
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/clips/2/video", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.Contains("/clips/", StringComparison.Ordinal) && path.EndsWith("/video", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1 })
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sceneDetailJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        var stitchService = new ClientVideoStitchService(null!, engineClient);

        var urls = await stitchService.CollectSceneMediaUrlsAsync(projectId, new[] { 1 }, null, new HashSet<int> { 1 });

        Assert.Empty(urls);
        Assert.Contains("S01 C02", stitchService.LastSkippedClipLabels);
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

    [Fact]
    public async Task CollectClipUrlsAsync_SkipsUnreachableServerFallback_WhenOnlyClientMarker()
    {
        var (stitch, _) = CreateStitchWithSceneClipsOnDisk(scene: 1, clipCount: 2, videoStatus: HttpStatusCode.NotFound);

        var urls = await stitch.CollectClipUrlsAsync("test-project", 1);

        Assert.Empty(urls);
        Assert.Contains("S01 C01", stitch.LastCollectError);
        Assert.Contains("S01 C02", stitch.LastCollectError);
        Assert.DoesNotContain("404", stitch.LastCollectError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectClipUrlsAsync_UsesReachableServerUrl_WhenNoLocalBlob()
    {
        var (stitch, _) = CreateStitchWithSceneClipsOnDisk(scene: 1, clipCount: 1, videoStatus: HttpStatusCode.OK);

        var urls = await stitch.CollectClipUrlsAsync("test-project", 1);

        Assert.Single(urls);
        Assert.Contains("scenes/1/clips/1/video", urls[0]);
        Assert.Null(stitch.LastCollectError);
    }

    [Fact]
    public async Task CollectClipUrlsAsync_FourClipsOne404_ReturnsOthersAndNamesMissing()
    {
        var (stitch, _) = CreateStitchWithMixedVideoStatus(scene: 1, playable: new[] { 1, 2, 4 }, missing: new[] { 3 });

        var urls = await stitch.CollectClipUrlsAsync("test-project", 1);

        Assert.Equal(3, urls.Count);
        Assert.DoesNotContain(urls, u => u.Contains("/clips/3/", StringComparison.Ordinal));
        Assert.Contains("S01 C03", stitch.LastSkippedClipLabels);
        Assert.Contains("S01 C03", stitch.LastCollectError);
    }

    [Fact]
    public async Task CollectClipUrlsAsync_RequestedPresentClip_StillResolves_WhenSiblingIsMissing()
    {
        var (stitch, _) = CreateStitchWithMixedVideoStatus(scene: 2, playable: new[] { 1, 2, 4 }, missing: new[] { 3 });

        var urls = await stitch.CollectClipUrlsAsync("test-project", 2, clipNumbers: new[] { 1 });

        Assert.Single(urls);
        Assert.Contains("/clips/1/", urls[0], StringComparison.Ordinal);
        Assert.Empty(stitch.LastSkippedClipLabels);
        Assert.Null(stitch.LastCollectError);
    }

    [Fact]
    public async Task CollectClipUrlsAsync_RequestedMissingClip_StaysEmpty_AndNamesThatClip()
    {
        var (stitch, _) = CreateStitchWithMixedVideoStatus(scene: 2, playable: new[] { 1, 2, 4 }, missing: new[] { 3 });

        var urls = await stitch.CollectClipUrlsAsync("test-project", 2, clipNumbers: new[] { 3 });

        Assert.Empty(urls);
        Assert.Contains("S02 C03", stitch.LastSkippedClipLabels);
        Assert.Contains("S02 C03", stitch.LastCollectError);
        Assert.DoesNotContain("S02 C01", stitch.LastCollectError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectClipUrlsAsync_DoesNotAddServerUrl_WhenFallbackDisabled()
    {
        var (stitch, _) = CreateStitchWithSceneClipsOnDisk(scene: 1, clipCount: 1, videoStatus: HttpStatusCode.OK);

        var urls = await stitch.CollectClipUrlsAsync("test-project", 1, includeServerFallback: false);

        Assert.Empty(urls);
        Assert.Contains("S01 C01", stitch.LastCollectError);
    }

    [Fact]
    public void FormatMissingClipPlayError_DoesNotSurfaceRawHttpStatus()
    {
        var connected = ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S01 C01" }, mediaFolderConnected: true);
        var disconnected = ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S01 C01", "S01 C02" }, mediaFolderConnected: false);

        Assert.Contains("S01 C01", connected);
        Assert.Contains("local media folder", connected);
        Assert.DoesNotContain("404", connected);
        Assert.Contains("S01 C02", disconnected);
        Assert.Contains("Connect your local media folder", disconnected);
        Assert.DoesNotContain("404", disconnected);
    }

    private static (ClientVideoStitchService Stitch, EngineApiClient Engine) CreateStitchWithMixedVideoStatus(
        int scene, int[] playable, int[] missing)
    {
        var all = playable.Concat(missing).Distinct().OrderBy(x => x).ToArray();
        var clips = all.Select(cn => new { clipNumber = cn, onDisk = true }).ToArray();
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new { sceneNumber = scene, clips }
        });
        var missingSet = missing.ToHashSet();

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains($"/scenes/{scene}/clips/", StringComparison.Ordinal)
                && path.EndsWith("/video", StringComparison.Ordinal))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var clipIdx = Array.IndexOf(parts, "clips");
                var cn = clipIdx >= 0 && clipIdx + 1 < parts.Length && int.TryParse(parts[clipIdx + 1], out var n)
                    ? n
                    : 0;
                var status = missingSet.Contains(cn) ? HttpStatusCode.NotFound : HttpStatusCode.OK;
                return new HttpResponseMessage(status)
                {
                    Content = new ByteArrayContent(status == HttpStatusCode.OK ? new byte[] { 0 } : Array.Empty<byte>())
                };
            }

            if (path.Contains($"/scenes/{scene}", StringComparison.Ordinal)
                && !path.Contains("/clips/", StringComparison.Ordinal))
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
        return (new ClientVideoStitchService(null!, engineClient), engineClient);
    }

    private static (ClientVideoStitchService Stitch, EngineApiClient Engine) CreateStitchWithSceneClipsOnDisk(
        int scene, int clipCount, HttpStatusCode videoStatus)
    {
        var clips = Enumerable.Range(1, clipCount)
            .Select(cn => new { clipNumber = cn, onDisk = true })
            .ToArray();
        var sceneDetailJson = JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = scene,
                clips
            }
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains($"/scenes/{scene}/clips/", StringComparison.Ordinal)
                && path.EndsWith("/video", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(videoStatus)
                {
                    Content = new ByteArrayContent(videoStatus == HttpStatusCode.OK ? new byte[] { 0 } : Array.Empty<byte>())
                };
            }

            if (path.Contains($"/scenes/{scene}", StringComparison.Ordinal)
                && !path.Contains("/clips/", StringComparison.Ordinal))
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
        return (new ClientVideoStitchService(null!, engineClient), engineClient);
    }
}
