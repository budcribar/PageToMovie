using System.Net;
using System.Text.Json;
using Microsoft.JSInterop;
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
    public async Task CollectClipUrlsAsync_RequestedClip2_DoesNotIncludeClip1()
    {
        var (stitch, _) = CreateStitchWithMixedVideoStatus(scene: 1, playable: new[] { 1, 2 }, missing: Array.Empty<int>());

        var urls = await stitch.CollectClipUrlsAsync("test-project", 1, clipNumbers: new[] { 2 });

        Assert.Single(urls);
        Assert.Contains("/clips/2/", urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain(urls, u => u.Contains("/clips/1/", StringComparison.Ordinal));
        Assert.Empty(stitch.LastSkippedClipLabels);
    }

    [Fact]
    public async Task ResolveServerClipUrlAsync_slices_this_clip_hop_not_previous_clip_url()
    {
        var js = new StitchJsRuntime(identifier => identifier switch
        {
            "PageToMovieFfmpeg.probeDurationAsync" =>
                """{"success":true,"seconds":10.0}""",
            "PageToMovieFfmpeg.keepLastSecondsAsync" =>
                """{"success":true,"url":"blob:s01-c02-sliced"}""",
            _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
        });
        var engine = new EngineApiClient(new HttpClient(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK))) { BaseAddress = new Uri("http://localhost") });
        var stitch = new ClientVideoStitchService(js, engine);
        var clip = new ClipSummary { ClipNumber = 2, ProviderLeadInSeconds = 4.9 };

        var url = await stitch.ResolveServerClipUrlAsync("test-project", 1, clip);

        Assert.Equal("blob:s01-c02-sliced", url);
        Assert.Contains("PageToMovieFfmpeg.keepLastSecondsAsync", js.Calls);
        Assert.DoesNotContain(js.Calls, c => c.Contains("/clips/1/", StringComparison.Ordinal));
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

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_second_call_reuses_cached_scene_details()
    {
        var detailHits = 0;
        var (stitch, _) = CreateStitchWithSceneClipsOnDisk(
            scene: 1, clipCount: 2, videoStatus: HttpStatusCode.OK, onSceneDetail: () => detailHits++);

        var summaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2 }
        };

        var first = await stitch.CollectSceneMediaUrlsAsync("test-project", [1], summaries, staleScenes: null);
        var firstMisses = stitch.LastCollectStats.SceneDetailMisses;
        var firstHits = stitch.LastCollectStats.SceneDetailHits;

        var second = await stitch.CollectSceneMediaUrlsAsync("test-project", [1], summaries, staleScenes: null);

        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(1, detailHits);
        Assert.Equal(1, firstMisses);
        Assert.Equal(0, firstHits);
        Assert.Equal(0, stitch.LastCollectStats.SceneDetailMisses);
        Assert.Equal(1, stitch.LastCollectStats.SceneDetailHits);
        Assert.Equal(2, stitch.LastCollectStats.ClipUrlHits);
    }

    [Fact]
    public async Task WarmSceneIndexAsync_then_collect_skips_scene_json_walk()
    {
        var detailHits = 0;
        var (stitch, _) = CreateStitchWithSceneClipsOnDisk(
            scene: 1, clipCount: 1, videoStatus: HttpStatusCode.OK, onSceneDetail: () => detailHits++);
        var summaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 1, ClipsOnDisk = 1 }
        };

        await stitch.WarmSceneIndexAsync("test-project", summaries);
        Assert.Equal(1, detailHits);

        var urls = await stitch.CollectSceneMediaUrlsAsync("test-project", [1], summaries, staleScenes: null);

        Assert.Single(urls);
        Assert.Equal(1, detailHits);
        Assert.Equal(1, stitch.LastCollectStats.SceneDetailHits);
        Assert.Equal(0, stitch.LastCollectStats.SceneDetailMisses);
    }

    [Fact]
    public async Task CollectAndMix_second_play_reuses_cached_scene_segment()
    {
        var concatCalls = 0;
        var js = new StitchJsRuntime(identifier =>
        {
            if (identifier == "PageToMovieCut.concatVideosOptimizedAsync")
            {
                concatCalls++;
                return """{"success":true,"url":"blob:mixed-s1","count":2}""";
            }

            throw new InvalidOperationException($"Unexpected JS call: {identifier}");
        });
        var (baseStitch, engine) = CreateStitchWithSceneClipsOnDisk(
            scene: 1, clipCount: 2, videoStatus: HttpStatusCode.OK);
        var stitch = new ClientVideoStitchService(js, engine);
        var summaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2 }
        };

        var first = await stitch.CollectAndMixSceneSegmentInfosAsync(
            "test-project", [1], summaries, staleScenes: null);
        var second = await stitch.CollectAndMixSceneSegmentInfosAsync(
            "test-project", [1], summaries, staleScenes: null);

        Assert.Single(first);
        Assert.Equal("blob:mixed-s1", first[0].Url);
        Assert.Single(second);
        Assert.Equal(first[0].Url, second[0].Url);
        Assert.Equal(1, concatCalls);
        Assert.Equal(1, stitch.LastCollectStats.SegmentHits);
        Assert.Equal(0, stitch.LastCollectStats.SegmentMisses);
    }

    [Fact]
    public async Task CollectSceneMediaUrlsAsync_take_change_invalidates_only_that_scene()
    {
        var scene1Details = 0;
        var scene2Details = 0;
        var scene1Json = SceneDetailJson(1, 1);
        var scene1Take2Json = SceneDetailJson(1, 1, fileName: "scene_01_clip_01_take_02.mp4", sizeBytes: 99);
        var scene2Json = SceneDetailJson(2, 1);
        var useTake2 = false;

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/scenes/1/clips/", StringComparison.Ordinal) && path.EndsWith("/video", StringComparison.Ordinal))
                return VideoOk();
            if (path.Contains("/scenes/2/clips/", StringComparison.Ordinal) && path.EndsWith("/video", StringComparison.Ordinal))
                return VideoOk();
            if (path.Contains("/scenes/1", StringComparison.Ordinal) && !path.Contains("/clips/", StringComparison.Ordinal))
            {
                scene1Details++;
                return JsonOk(useTake2 ? scene1Take2Json : scene1Json);
            }

            if (path.Contains("/scenes/2", StringComparison.Ordinal) && !path.Contains("/clips/", StringComparison.Ordinal))
            {
                scene2Details++;
                return JsonOk(scene2Json);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var engine = new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        var stitch = new ClientVideoStitchService(null!, engine);
        var summaries = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 1, ClipsOnDisk = 1 },
            new() { SceneNumber = 2, ClipCount = 1, ClipsOnDisk = 1 },
        };

        await stitch.CollectSceneMediaUrlsAsync("test-project", [1, 2], summaries, staleScenes: null);
        Assert.Equal(1, scene1Details);
        Assert.Equal(1, scene2Details);

        useTake2 = true;
        summaries[0] = new SceneSummary { SceneNumber = 1, ClipCount = 1, ClipsOnDisk = 1, HasStaleClips = true };
        stitch.MediaIndex.SyncSceneList("test-project", summaries);

        await stitch.CollectSceneMediaUrlsAsync("test-project", [1, 2], summaries, staleScenes: null);
        Assert.Equal(2, scene1Details);
        Assert.Equal(1, scene2Details);
    }

    [Fact]
    public async Task ConcatAsync_uses_shared_optimized_stitch_before_legacy_stitcher()
    {
        var js = new StitchJsRuntime(identifier => identifier switch
        {
            "PageToMovieCut.concatVideosOptimizedAsync" =>
                """{"success":true,"url":"blob:optimized","count":2,"sha256":"abc","byteLength":321}""",
            _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
        });
        var engine = new EngineApiClient(new HttpClient(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://localhost") });
        var service = new ClientVideoStitchService(js, engine);

        var result = await service.ConcatAsync(["blob:first", "blob:second"]);

        Assert.True(result.Success);
        Assert.Equal("blob:optimized", result.Url);
        Assert.Equal("abc", result.Sha256);
        Assert.Equal(321, result.ByteLength);
        Assert.Equal(["PageToMovieCut.concatVideosOptimizedAsync"], js.Calls);
    }

    [Fact]
    public async Task ConcatAsync_falls_back_to_legacy_when_shared_stitch_fails()
    {
        var js = new StitchJsRuntime(identifier => identifier switch
        {
            "PageToMovieCut.concatVideosOptimizedAsync" => """{"success":false,"error":"pool failed"}""",
            "PageToMovieFfmpeg.concatVideosAsync" =>
                """{"success":true,"url":"blob:legacy","count":2}""",
            _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
        });
        var engine = new EngineApiClient(new HttpClient(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://localhost") });
        var service = new ClientVideoStitchService(js, engine);

        var result = await service.ConcatAsync(["blob:first", "blob:second"]);

        Assert.True(result.Success);
        Assert.Equal("blob:legacy", result.Url);
        Assert.Equal(
            ["PageToMovieCut.concatVideosOptimizedAsync", "PageToMovieFfmpeg.concatVideosAsync"],
            js.Calls);
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
        int scene, int clipCount, HttpStatusCode videoStatus, Action? onSceneDetail = null)
    {
        var sceneDetailJson = SceneDetailJson(scene, clipCount);

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
                onSceneDetail?.Invoke();
                return JsonOk(sceneDetailJson);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var engineClient = new EngineApiClient(httpClient);
        return (new ClientVideoStitchService(null!, engineClient), engineClient);
    }

    private static string SceneDetailJson(
        int scene, int clipCount, string? fileName = null, long sizeBytes = 0)
    {
        var clips = Enumerable.Range(1, clipCount)
            .Select(cn => new
            {
                clipNumber = cn,
                onDisk = true,
                fileName = fileName ?? $"scene_{scene:D2}_clip_{cn:D2}_take_01.mp4",
                sizeBytes,
            })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            scene = new
            {
                sceneNumber = scene,
                clipCount,
                clipsOnDisk = clipCount,
                clips
            }
        });
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage VideoOk() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0 })
        };

    private sealed class StitchJsRuntime(Func<string, string> resultJson) : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Calls.Add(identifier);
            var value = JsonSerializer.Deserialize<TValue>(
                resultJson(identifier),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return ValueTask.FromResult(value!);
        }
    }

    [Fact]
    public void Play_resolves_the_take_the_Film_page_is_showing()
    {
        var row = new ClipSummary { ClipNumber = 1, FileName = "scene_01_clip_01_take_03.mp4" };

        // The pointer is the current take, so it is tried first even when the server row names a
        // different one — that drift is what had Review playing take 3 while Film showed take 5.
        Assert.Equal(
            new[] { "assets/video/scene_01_clip_01_take_05.mp4", "assets/video/scene_01_clip_01_take_03.mp4" },
            ClientVideoStitchService.ClipPathCandidates("assets/video/scene_01_clip_01_take_05.mp4", row));
    }

    [Fact]
    public void Without_a_pointer_the_row_take_is_the_fallback()
    {
        var row = new ClipSummary { ClipNumber = 1, FileName = "scene_01_clip_01_take_03.mp4" };

        Assert.Equal(
            new[] { "assets/video/scene_01_clip_01_take_03.mp4" },
            ClientVideoStitchService.ClipPathCandidates(null, row));
    }

    [Fact]
    public void The_canonical_alias_is_never_a_candidate()
    {
        // scene_SS_clip_CC.mp4 is a leftover from before takes; it is not a current take.
        var row = new ClipSummary { ClipNumber = 1, FileName = "scene_01_clip_01.mp4" };

        Assert.Empty(ClientVideoStitchService.ClipPathCandidates(null, row));
    }
}
