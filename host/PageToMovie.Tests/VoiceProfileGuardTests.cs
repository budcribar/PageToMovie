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

public class VoiceProfileLockTests
{
    [Theory]
    [InlineData("Adult male, 40s, warm baritone.", true)]
    [InlineData("Adult male voice, warm adult storytelling voice, even mid register.", true)]
    [InlineData("Girl, about 8, bright.", true)]
    [InlineData("Warm storytelling voice, even mid register.", false)]   // neither
    [InlineData("Male, warm and bold.", false)]                         // sex only ("bold" must not read as "old")
    [InlineData("Elderly, slow and kind.", false)]                      // age only
    public void IsLocked_requires_sex_and_age(string profile, bool expected) =>
        Assert.Equal(expected, PageToMovie.Core.Models.VoiceProfileGuard.IsLocked(profile));

    [Fact]
    public void UnlockedReason_names_what_is_missing()
    {
        Assert.Null(PageToMovie.Core.Models.VoiceProfileGuard.UnlockedReason("Adult female, 30s, bright."));
        Assert.Contains("male/female", PageToMovie.Core.Models.VoiceProfileGuard.UnlockedReason("Elderly, slow.")!);
        Assert.Contains("age", PageToMovie.Core.Models.VoiceProfileGuard.UnlockedReason("Male, warm.")!);
        Assert.Equal("voice profile", PageToMovie.Core.Models.VoiceProfileGuard.UnlockedReason(""));
    }
}
