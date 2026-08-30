using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class VoiceTagWriterTests
{
    [Fact]
    public void SlimToPerformance_drops_sex_age_timbre_keeps_pace_and_manner()
    {
        var slim = VoiceTagWriter.SlimToPerformance(
            "Male, middle years; medium-high tense pitch; precise, controlled pace that sharpens into fevered urgency; intimate confessional energy, same voice on-camera and in V.O.");
        Assert.Contains("pace", slim, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confessional", slim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("male", slim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("middle years", slim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pitch", slim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("same voice", slim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlimToPerformance_drops_profile_that_is_only_identity()
    {
        Assert.True(string.IsNullOrWhiteSpace(
            VoiceTagWriter.SlimToPerformance("Adult male, 40s, warm baritone.")));
    }

    [Fact]
    public void SlimToPerformance_keeps_accent_and_pace()
    {
        var slim = VoiceTagWriter.SlimToPerformance("precise, controlled pace; Irish accent");
        Assert.Contains("pace", slim, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accent", slim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoiceProse_omits_when_VoiceLock_owns_speaker()
    {
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Character_Eve" };
        var prose = VoiceTagWriter.VoiceProseForCharacterLine(
            "Character_Eve",
            "Adult male, 40s, warm baritone, measured pace",
            speakers,
            audioTags: null);
        Assert.Equal("", prose);
    }

    [Fact]
    public void VoiceProse_slims_when_preset_is_attached()
    {
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Character_Eve" };
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = "<AUDIO_0>",
        };
        var prose = VoiceTagWriter.VoiceProseForCharacterLine(
            "Character_Eve",
            "Adult female, 30s, bright soprano; Irish accent; measured pace",
            speakers,
            tags);
        Assert.Contains("accent", prose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pace", prose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("female", prose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30s", prose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soprano", prose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoiceProse_omits_for_non_speakers()
    {
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Character_Eve" };
        var prose = VoiceTagWriter.VoiceProseForCharacterLine(
            "Character_Rex",
            "Adult male, 40s, warm baritone",
            speakers,
            audioTags: null);
        Assert.Equal("", prose);
    }
}
