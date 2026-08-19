using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class AiRetryPolicyTests
{
    // ── CheckCoverage ────────────────────────────────────────────────────────

    [Fact]
    public void CheckCoverage_FullCoverage_ReturnsNoMissingAndTrue()
    {
        var requested = new[] { "b1", "b2", "b3" };
        var returned = new[] { "b1", "b2", "b3" };

        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(requested, returned);

        Assert.Empty(missing);
        Assert.True(fullyCovered);
    }

    [Fact]
    public void CheckCoverage_PartialCoverage_ReturnsMissingIdsAndFalse()
    {
        var requested = new[] { "b1", "b2", "b3", "b4" };
        var returned = new[] { "b1", "b3" };

        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(requested, returned);

        Assert.False(fullyCovered);
        Assert.Equal(new[] { "b2", "b4" }, missing);
    }

    [Fact]
    public void CheckCoverage_TotallyEmptyResponse_AllMissing()
    {
        var requested = new[] { "b1", "b2" };
        var returned = Array.Empty<string>();

        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(requested, returned);

        Assert.False(fullyCovered);
        Assert.Equal(new[] { "b1", "b2" }, missing);
    }

    [Fact]
    public void CheckCoverage_NoIdsRequested_TriviallyFullyCovered()
    {
        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(missing);
        Assert.True(fullyCovered);
    }

    [Fact]
    public void CheckCoverage_IsCaseInsensitive()
    {
        var requested = new[] { "Character_Old_Man" };
        var returned = new[] { "character_old_man" };

        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(requested, returned);

        Assert.Empty(missing);
        Assert.True(fullyCovered);
    }

    [Fact]
    public void CheckCoverage_DuplicateRequestedIds_CountedOnce()
    {
        var requested = new[] { "b1", "b1", "b2" };
        var returned = new[] { "b1" };

        var (missing, fullyCovered) = AiRetryPolicy.CheckCoverage(requested, returned);

        Assert.False(fullyCovered);
        Assert.Equal(new[] { "b2" }, missing);
    }

    // ── IsTransientHttpStatus / IsTransientException ────────────────────────

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    [InlineData(501, false)]
    public void IsTransientHttpStatus_ClassifiesCorrectly(int status, bool expectedTransient)
    {
        Assert.Equal(expectedTransient, AiRetryPolicy.IsTransientHttpStatus(status));
    }

    [Fact]
    public void IsTransientException_HttpRequestException_IsTransient()
    {
        Assert.True(AiRetryPolicy.IsTransientException(new HttpRequestException("boom")));
    }

    [Fact]
    public void IsTransientException_TimeoutException_IsTransient()
    {
        Assert.True(AiRetryPolicy.IsTransientException(new TimeoutException("timed out")));
    }

    [Fact]
    public void IsTransientException_IOException_IsTransient()
    {
        Assert.True(AiRetryPolicy.IsTransientException(new System.IO.IOException("io fail")));
    }

    [Fact]
    public void IsTransientException_OperationCanceledException_IsNeverTransient()
    {
        Assert.False(AiRetryPolicy.IsTransientException(new OperationCanceledException()));
    }

    [Fact]
    public void IsTransientException_InvalidOperationException_IsNotTransient()
    {
        Assert.False(AiRetryPolicy.IsTransientException(new InvalidOperationException("client error")));
    }

    [Fact]
    public void IsTransientException_ArgumentException_IsNotTransient()
    {
        Assert.False(AiRetryPolicy.IsTransientException(new ArgumentException("bad arg")));
    }

    // ── ExecuteWithTransientRetryAsync ───────────────────────────────────────

    [Fact]
    public async Task ExecuteWithTransientRetryAsync_SucceedsFirstTry_NoRetry()
    {
        var calls = 0;
        var result = await AiRetryPolicy.ExecuteWithTransientRetryAsync<string>(
            attempt =>
            {
                calls++;
                return Task.FromResult("ok");
            },
            isTransient: _ => true,
            maxAttempts: 3,
            backoffBaseMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteWithTransientRetryAsync_RetriesTransientFailureThenSucceeds()
    {
        var calls = 0;
        var retryLogged = new List<int>();
        var result = await AiRetryPolicy.ExecuteWithTransientRetryAsync<string>(
            attempt =>
            {
                calls++;
                if (calls < 3) throw new HttpRequestException("transient");
                return Task.FromResult("recovered");
            },
            isTransient: AiRetryPolicy.IsTransientException,
            maxAttempts: 5,
            backoffBaseMs: 1,
            onRetry: (attemptNum, ex) =>
            {
                retryLogged.Add(attemptNum);
                return Task.CompletedTask;
            });

        Assert.Equal("recovered", result);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 1, 2 }, retryLogged);
    }

    [Fact]
    public async Task ExecuteWithTransientRetryAsync_ExhaustsAttempts_RethrowsOriginalException()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AiRetryPolicy.ExecuteWithTransientRetryAsync<string>(
                _ =>
                {
                    calls++;
                    throw new HttpRequestException("still failing");
                },
                isTransient: AiRetryPolicy.IsTransientException,
                maxAttempts: 3,
                backoffBaseMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteWithTransientRetryAsync_NonTransientException_NoRetry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AiRetryPolicy.ExecuteWithTransientRetryAsync<string>(
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("permanent client error");
                },
                isTransient: AiRetryPolicy.IsTransientException,
                maxAttempts: 3,
                backoffBaseMs: 1));

        Assert.Equal(1, calls);
    }

    // ── RunWithCoverageRetryAsync ────────────────────────────────────────────

    [Fact]
    public async Task RunWithCoverageRetryAsync_FullCoverageFirstAttempt_NoRetryNeeded()
    {
        var calls = 0;
        var result = await AiRetryPolicy.RunWithCoverageRetryAsync<int>(
            new[] { "b1", "b2" },
            () =>
            {
                calls++;
                return Task.FromResult("""{"b1":1,"b2":2}""");
            },
            raw => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["b1"] = 1, ["b2"] = 2 },
            maxAttempts: 3,
            backoffBaseMs: 1);

        Assert.Equal(1, calls);
        Assert.Equal(1, result.Attempts);
        Assert.True(result.FullyCovered);
        Assert.Empty(result.Missing);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result!.Count);
    }

    [Fact]
    public async Task RunWithCoverageRetryAsync_PartialThenRecovers_MergesAcrossAttempts()
    {
        var call = 0;
        var result = await AiRetryPolicy.RunWithCoverageRetryAsync<int>(
            new[] { "b1", "b2", "b3" },
            () =>
            {
                call++;
                return Task.FromResult(call.ToString());
            },
            raw =>
            {
                // First attempt only covers b1; second attempt covers the rest.
                return raw == "1"
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["b1"] = 1 }
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["b2"] = 2, ["b3"] = 3 };
            },
            maxAttempts: 3,
            backoffBaseMs: 1);

        Assert.Equal(2, call);
        Assert.Equal(2, result.Attempts);
        Assert.True(result.FullyCovered);
        Assert.Equal(3, result.Result!.Count);
        Assert.Equal(3, result.ReturnedCount);
    }

    [Fact]
    public async Task RunWithCoverageRetryAsync_NeverRecovers_ReturnsPartialAfterExhaustingAttempts()
    {
        var result = await AiRetryPolicy.RunWithCoverageRetryAsync<int>(
            new[] { "b1", "b2", "b3" },
            () => Task.FromResult("x"),
            raw => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["b1"] = 1 },
            maxAttempts: 3,
            backoffBaseMs: 1);

        Assert.Equal(3, result.Attempts);
        Assert.False(result.FullyCovered);
        Assert.Equal(new[] { "b2", "b3" }, result.Missing);
        Assert.NotNull(result.Result);
        Assert.Single(result.Result!);
        Assert.Equal(1, result.ReturnedCount);
    }

    [Fact]
    public async Task RunWithCoverageRetryAsync_ChatThrows_DoesNotPropagate_KeepsRetrying()
    {
        var call = 0;
        var result = await AiRetryPolicy.RunWithCoverageRetryAsync<int>(
            new[] { "b1" },
            () =>
            {
                call++;
                if (call == 1) throw new InvalidOperationException("boom");
                return Task.FromResult("ok");
            },
            raw => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["b1"] = 1 },
            maxAttempts: 3,
            backoffBaseMs: 1);

        Assert.Equal(2, call);
        Assert.True(result.FullyCovered);
        Assert.NotNull(result.LastError);
    }
}

public class GeminiTransientPermissionDeniedTests
{
    [Fact]
    public void Gemini_403_permission_denied_is_retried_but_other_403s_are_not()
    {
        var gemini = new PageToMovie.Engine.ChatHttpStatusException(403,
            "Gemini models/gemini-3.7-flash:generateContent HTTP 403: { \"error\": { \"code\": 403, \"message\": \"The caller does not have permission\", \"status\": \"PERMISSION_DENIED\" } }", null);
        Assert.True(PageToMovie.Engine.AiRetryPolicy.IsTransientChatFailure(gemini));

        var forbidden = new PageToMovie.Engine.ChatHttpStatusException(403, "HTTP 403: API key not valid for this model", null);
        Assert.False(PageToMovie.Engine.AiRetryPolicy.IsTransientChatFailure(forbidden));
    }
}
