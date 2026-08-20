using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ElevenLabsClientHelpersTests
{
    [Theory]
    [InlineData("line.mp3", "audio/mpeg")]
    [InlineData("line.wav", "audio/wav")]
    [InlineData("line.m4a", "audio/mp4")]
    [InlineData("line.aac", "audio/mp4")]
    [InlineData("line.ogg", "audio/ogg")]
    [InlineData("line.webm", "audio/webm")]
    [InlineData("line.mp4", "video/mp4")]
    [InlineData("line.unknown", "application/octet-stream")]
    [InlineData("LINE.MP3", "audio/mpeg")]
    public void GuessAudioMime_MapsKnownExtensions(string fileName, string expected) =>
        Assert.Equal(expected, ElevenLabsClientHelpers.GuessAudioMime(fileName));

    [Fact]
    public void Trunc_Empty_ReturnsEmpty() =>
        Assert.Equal("", ElevenLabsClientHelpers.Trunc(null));

    [Fact]
    public void Trunc_Short_Unchanged() =>
        Assert.Equal("hello", ElevenLabsClientHelpers.Trunc("hello", 8));

    [Fact]
    public void Trunc_Long_Ellipsis() =>
        Assert.Equal("hello…", ElevenLabsClientHelpers.Trunc("hello world", 5));
}
