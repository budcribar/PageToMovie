using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>Mary19 S02C05: a sexless narrator profile let the video model re-cast the voice per clip.</summary>
public class VoiceProfileGuardTests
{
    [Theory]
    [InlineData("Warm adult storytelling voice, even mid register, measured couplet cadence.", false)]
    [InlineData("Adult male, 40s, warm baritone storyteller.", true)]
    [InlineData("Girl, about 8, bright and quick.", true)]
    [InlineData("Soft soprano, airy.", true)]
    [InlineData("", false)]
    public void States_sex_detects_sex_or_sexed_voice_words(string profile, bool expected) =>
        Assert.Equal(expected, VoiceProfileGuard.StatesSex(profile));

    [Fact]
    public void WithSex_prefixes_and_keeps_the_rest()
    {
        var fixedUp = VoiceProfileGuard.WithSex("Warm adult storytelling voice, even mid register.", "male");
        Assert.Equal("Adult male voice, warm adult storytelling voice, even mid register.", fixedUp);
        Assert.True(VoiceProfileGuard.StatesSex(fixedUp));
        Assert.Equal("Young girl's voice.", VoiceProfileGuard.WithSex("", "girl"));
    }
}
