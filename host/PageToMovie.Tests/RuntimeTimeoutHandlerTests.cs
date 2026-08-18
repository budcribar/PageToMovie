using System.Net;
using Microsoft.Extensions.Options;
using PageToMovie.Api;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Admin → Network &amp; Timeouts must actually govern provider calls: the handler reads the bucket
/// at send time (hot-apply), a hit surfaces as a TimeoutException naming the bucket (never a bare
/// cancellation a caller could mistake for a user cancel), and a real caller cancel still cancels.
/// </summary>
public class RuntimeTimeoutHandlerTests
{
    private sealed class SlowHandler : HttpMessageHandler
    {
        public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(5);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }

    private static (HttpClient Client, PageToMovieOptions Opts) Make(TimeoutBucket bucket, TimeSpan slow)
    {
        var opts = new PageToMovieOptions();
        var handler = new RuntimeTimeoutHandler(Options.Create(opts), bucket) { InnerHandler = new SlowHandler { Delay = slow } };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromHours(2) }, opts);
    }

    [Fact]
    public async Task Bucket_limit_is_read_per_request_and_surfaces_as_TimeoutException()
    {
        var (client, opts) = Make(TimeoutBucket.Image, slow: TimeSpan.FromSeconds(3));
        opts.Timeouts.ImageTimeoutSeconds = 1; // hot-applied: set after the client was built

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync("http://example.invalid/gen"));
        Assert.Contains("Image", ex.Message);
        Assert.Contains("1s", ex.Message);
        Assert.Contains("Network & Timeouts", ex.Message);
    }

    [Fact]
    public async Task Under_the_limit_the_call_completes()
    {
        var (client, opts) = Make(TimeoutBucket.Video, slow: TimeSpan.FromMilliseconds(200));
        opts.Timeouts.VideoTimeoutSeconds = 5;
        var resp = await client.GetAsync("http://example.invalid/gen");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_still_a_cancellation_not_a_timeout()
    {
        var (client, opts) = Make(TimeoutBucket.Chat, slow: TimeSpan.FromSeconds(10));
        opts.Timeouts.ChatTimeoutSeconds = 30;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("http://example.invalid/gen", cts.Token));
    }

    [Fact]
    public void Every_bucket_maps_to_its_option()
    {
        var t = new TimeoutsOptions { ImageTimeoutSeconds = 1, VideoTimeoutSeconds = 2, ChatTimeoutSeconds = 3, AudioTimeoutSeconds = 4 };
        Assert.Equal(1, RuntimeTimeoutHandler.SecondsFor(t, TimeoutBucket.Image));
        Assert.Equal(2, RuntimeTimeoutHandler.SecondsFor(t, TimeoutBucket.Video));
        Assert.Equal(3, RuntimeTimeoutHandler.SecondsFor(t, TimeoutBucket.Chat));
        Assert.Equal(4, RuntimeTimeoutHandler.SecondsFor(t, TimeoutBucket.Audio));
    }

    [Fact]
    public void Percentiles_and_bucket_mapping_for_the_evidence_card()
    {
        var secs = new List<double> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(5, UserDatabaseService.Percentile(secs, 0.50));
        Assert.Equal(10, UserDatabaseService.Percentile(secs, 0.95));
        Assert.Equal(0, UserDatabaseService.Percentile(new List<double>(), 0.99));
        Assert.Equal("Video", UserDatabaseService.BucketForCategory("video"));
        Assert.Equal("Image", UserDatabaseService.BucketForCategory("characters"));
        Assert.Equal("Chat", UserDatabaseService.BucketForCategory("screenplay"));
        Assert.Equal("Chat", UserDatabaseService.BucketForCategory("review"));
        Assert.Equal("Audio", UserDatabaseService.BucketForCategory("music"));
        Assert.Null(UserDatabaseService.BucketForCategory("other"));
    }
}
