using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Header sanitizer used when a Files content GET fails but <c>source_url</c> still streamed.
/// Locks collapse / truncate behavior after the S3776 helper extract.
/// </summary>
public sealed class MediaProxyHeadersTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("line\r\nbreak", "line break")]
    [InlineData("  padded\t\tvalue  ", "padded value")]
    [InlineData("café — dash", "caf dash")]
    [InlineData("\r\n\t", "")]
    [InlineData(null, "")]
    public void SanitizeHeaderValue_keeps_only_printable_ascii(string? input, string expected) =>
        Assert.Equal(expected, MediaProxyHeaders.SanitizeHeaderValue(input));

    [Fact]
    public void SanitizeHeaderValue_truncates_and_drops_surrogates()
    {
        var value = MediaProxyHeaders.SanitizeHeaderValue(new string('x', 5000));
        Assert.Equal(MediaProxyHeaders.MaxHeaderValueLength, value.Length);
        Assert.All(value, ch => Assert.InRange(ch, ' ', '~'));
        Assert.Equal("", MediaProxyHeaders.SanitizeHeaderValue("🎬🎬"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SanitizeHeaderValue_non_positive_max_uses_default(int maxLength)
    {
        var input = new string('a', MediaProxyHeaders.MaxHeaderValueLength + 40);
        var value = MediaProxyHeaders.SanitizeHeaderValue(input, maxLength);
        Assert.Equal(MediaProxyHeaders.MaxHeaderValueLength, value.Length);
        Assert.Equal(new string('a', MediaProxyHeaders.MaxHeaderValueLength), value);
    }

    [Fact]
    public void SanitizeHeaderValue_custom_max_stops_before_pending_space()
    {
        // Last printable would need a collapsed space first; at the cap we drop both.
        Assert.Equal("ab", MediaProxyHeaders.SanitizeHeaderValue("ab\n\tc", maxLength: 2));
        Assert.Equal("ab c", MediaProxyHeaders.SanitizeHeaderValue("ab\n\tc", maxLength: 4));
        Assert.Equal("ab", MediaProxyHeaders.SanitizeHeaderValue("ab   ", maxLength: 3));
    }
}
