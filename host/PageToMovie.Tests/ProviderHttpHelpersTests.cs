using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ProviderHttpHelpersTests
{
    [Fact]
    public void Trim_leaves_short_strings_unchanged() =>
        Assert.Equal("abc", ProviderHttpHelpers.Trim("abc", 10));

    [Fact]
    public void Trim_cuts_to_max_without_ellipsis() =>
        Assert.Equal("abcd", ProviderHttpHelpers.Trim("abcdefgh", 4));

    [Fact]
    public void RequireJsonString_returns_the_named_property()
    {
        var id = ProviderHttpHelpers.RequireJsonString(
            """{"request_id":"abc-123","status":"queued"}""",
            "request_id",
            "missing request_id");
        Assert.Equal("abc-123", id);
    }

    [Fact]
    public void RequireJsonString_throws_with_trimmed_body_when_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ProviderHttpHelpers.RequireJsonString("""{"status":"queued"}""", "request_id", "Grok response missing request_id"));
        Assert.StartsWith("Grok response missing request_id:", ex.Message);
        Assert.Contains("queued", ex.Message);
    }

    [Fact]
    public async Task ReadSuccessBodyAsync_returns_body_on_2xx()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        };
        var body = await ProviderHttpHelpers.ReadSuccessBodyAsync(resp, CancellationToken.None, "Grok poll");
        Assert.Contains("ok", body);
    }

    [Fact]
    public async Task ReadSuccessBodyAsync_throws_chat_http_status_on_error()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("slow down", Encoding.UTF8, "text/plain"),
        };
        var ex = await Assert.ThrowsAsync<ChatHttpStatusException>(() =>
            ProviderHttpHelpers.ReadSuccessBodyAsync(resp, CancellationToken.None, "Grok submit"));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("Grok submit HTTP 429: slow down", ex.Message);
    }

    [Fact]
    public async Task ReadRequiredJsonStringAsync_parses_id_from_success()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"request_id":"job-9"}""", Encoding.UTF8, "application/json"),
        };
        var id = await ProviderHttpHelpers.ReadRequiredJsonStringAsync(
            resp, CancellationToken.None, "request_id", "Grok submit", "Grok response missing request_id");
        Assert.Equal("job-9", id);
    }

    [Fact]
    public void EnsureTrailingSlashBaseAddress_sets_address_once()
    {
        using var http = new HttpClient();
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(http, "https://api.example.com/v1");
        Assert.Equal(new Uri("https://api.example.com/v1/"), http.BaseAddress);

        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(http, "https://other.example/");
        Assert.Equal(new Uri("https://api.example.com/v1/"), http.BaseAddress);
    }

    [Fact]
    public async Task DownloadToFileAsync_writes_bytes_and_creates_directory()
    {
        var dest = Path.Combine(Path.GetTempPath(), "ptm-http-helpers-" + Guid.NewGuid().ToString("N"), "clip.mp4");
        try
        {
            using var handler = new StubGetHandler(new byte[] { 1, 2, 3, 4 });
            using var http = new HttpClient(handler);
            await ProviderHttpHelpers.DownloadToFileAsync(
                http, "https://cdn.example/clip.mp4", dest, CancellationToken.None,
                NullLogger.Instance);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
            try
            {
                var dir = Path.GetDirectoryName(dest);
                if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("EXPIRED", true)]
    [InlineData("done", false)]
    [InlineData(null, false)]
    public void IsPollFailedOrExpired_matches_failed_and_expired(string? status, bool expected) =>
        Assert.Equal(expected, VideoClientHelpers.IsPollFailedOrExpired(status));

    [Fact]
    public void FormatPollProgress_includes_percent_when_present()
    {
        Assert.Equal("status=running", VideoClientHelpers.FormatPollProgress("running", null));
        Assert.Equal("status=running (40%)", VideoClientHelpers.FormatPollProgress("running", "40"));
    }

    [Fact]
    public void TranscribePageNotSupported_names_the_provider()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ChatClientHelpers.TranscribePageNotSupported("Gemini"));
        Assert.Contains("Gemini", ex.Message);
        Assert.Contains("Grok", ex.Message);
    }

    [Fact]
    public void ClassifyCharactersNotSupported_names_the_provider()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ChatClientHelpers.ClassifyCharactersNotSupported("Anthropic"));
        Assert.Contains("Anthropic", ex.Message);
    }

    private sealed class StubGetHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        public StubGetHandler(byte[] bytes) => _bytes = bytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes),
            });
        }
    }
}
