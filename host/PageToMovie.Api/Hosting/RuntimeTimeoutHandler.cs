using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

namespace PageToMovie.Api;

/// <summary>Which Admin → Network &amp; Timeouts bucket governs a provider client.</summary>
public enum TimeoutBucket { Image, Video, Chat, Audio }

/// <summary>
/// Applies the hot-applied runtime timeout (Admin → Network &amp; Timeouts, <see cref="TimeoutsOptions"/>)
/// per request. <c>HttpClient.Timeout</c> is fixed after the first request and shared by every
/// caller, so it can only be a ceiling; the effective per-call limit is read here, at send time,
/// from the same options object <c>RuntimeConfigStore</c> mutates. A timeout surfaces as a
/// <see cref="TimeoutException"/> naming the bucket — never as a bare cancellation that callers
/// could mistake for a user cancel.
/// </summary>
public sealed class RuntimeTimeoutHandler : DelegatingHandler
{
    private readonly IOptions<PageToMovieOptions> _opts;
    private readonly TimeoutBucket _bucket;

    public RuntimeTimeoutHandler(IOptions<PageToMovieOptions> opts, TimeoutBucket bucket)
    {
        _opts = opts;
        _bucket = bucket;
    }

    public static int SecondsFor(TimeoutsOptions t, TimeoutBucket bucket) => bucket switch
    {
        TimeoutBucket.Image => t.ImageTimeoutSeconds,
        TimeoutBucket.Video => t.VideoTimeoutSeconds,
        TimeoutBucket.Chat => t.ChatTimeoutSeconds,
        TimeoutBucket.Audio => t.AudioTimeoutSeconds,
        _ => t.ChatTimeoutSeconds,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var seconds = SecondsFor(_opts.Value.Timeouts ?? new TimeoutsOptions(), _bucket);
        if (seconds <= 0)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await base.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{_bucket} provider call exceeded the {seconds}s limit ({request.Method} {request.RequestUri?.Host}). " +
                "Raise it under Admin → Network & Timeouts if this provider is legitimately slow.");
        }
    }
}

public static class RuntimeTimeoutHandlerExtensions
{
    /// <summary>Attach the per-request runtime timeout for <paramref name="bucket"/> to a named client.</summary>
    public static IHttpClientBuilder WithRuntimeTimeout(this IHttpClientBuilder b, TimeoutBucket bucket) =>
        b.AddHttpMessageHandler(sp => new RuntimeTimeoutHandler(sp.GetRequiredService<IOptions<PageToMovieOptions>>(), bucket));
}
