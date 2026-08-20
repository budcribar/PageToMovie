using Microsoft.AspNetCore.Http;
using PageToMovie.Api;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Provider file handles (xAI Files) are only retrievable by the account that created them.
/// After a key rotation (or when generation used the server env key while the user later
/// saved a different key in Settings), the proxy's first key gets HTTP 500 "Failed to
/// retrieve file" — the Mary19 recovery outage. These cover the retry-with-the-other-key
/// pieces: candidate selection and the ProviderFileUnavailable signal the retry loop keys on.
/// </summary>
public class MediaProxyKeyCandidateTests
{
    [Fact]
    public void Distinct_stored_and_env_keys_yield_two_candidates_in_order()
    {
        var c = MediaEndpoints.BuildKeyCandidates("stored-key", "env-key", retryWorthwhile: true);
        Assert.Equal(new string?[] { "stored-key", "env-key" }, c);
    }

    [Fact]
    public void Matching_or_missing_env_key_yields_single_candidate()
    {
        Assert.Equal(new string?[] { "k" }, MediaEndpoints.BuildKeyCandidates("k", "k", retryWorthwhile: true));
        Assert.Equal(new string?[] { "k" }, MediaEndpoints.BuildKeyCandidates("k", null, retryWorthwhile: true));
        Assert.Equal(new string?[] { "k" }, MediaEndpoints.BuildKeyCandidates("k", " ", retryWorthwhile: true));
    }

    [Fact]
    public void No_stored_key_yields_single_null_candidate_since_downstream_falls_to_env()
    {
        Assert.Equal(new string?[] { null }, MediaEndpoints.BuildKeyCandidates(null, "env-key", retryWorthwhile: true));
        Assert.Equal(new string?[] { null }, MediaEndpoints.BuildKeyCandidates(" ", "env-key", retryWorthwhile: true));
    }

    [Fact]
    public void No_file_id_disables_the_env_retry()
    {
        Assert.Equal(
            new string?[] { "stored-key" },
            MediaEndpoints.BuildKeyCandidates("stored-key", "env-key", retryWorthwhile: false));
    }

    [Fact]
    public async Task Failed_file_download_reports_unavailable_so_the_next_key_is_tried()
    {
        var (result, unavailable) = await MediaEndpoints.StreamProviderCopyDetailedAsync(
            url: null,
            fileId: "file_x",
            openUrl: (_, _) => Task.FromResult<IResult?>(null),
            openFileId: (_, _) => throw new InvalidOperationException("xAI file content HTTP 500: nope"),
            ct: CancellationToken.None);

        Assert.True(unavailable);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Successful_file_download_is_not_flagged_unavailable()
    {
        var ok = Results.Ok();
        var (result, unavailable) = await MediaEndpoints.StreamProviderCopyDetailedAsync(
            url: null,
            fileId: "file_x",
            openUrl: (_, _) => Task.FromResult<IResult?>(null),
            openFileId: (_, _) => Task.FromResult<IResult?>(ok),
            ct: CancellationToken.None);

        Assert.False(unavailable);
        Assert.Same(ok, result);
    }

    [Fact]
    public async Task Hosted_fallback_after_a_dead_file_is_not_flagged_unavailable()
    {
        var hosted = Results.Ok();
        var (result, unavailable) = await MediaEndpoints.StreamProviderCopyDetailedAsync(
            url: null,
            fileId: "file_x",
            openUrl: (_, _) => Task.FromResult<IResult?>(null),
            openFileId: (_, _) => Task.FromResult<IResult?>(null),
            ct: CancellationToken.None,
            recoverAfterProvider: (_, _, _) => Task.FromResult<IResult?>(hosted));

        Assert.False(unavailable);
        Assert.Same(hosted, result);
    }
}
