using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>Voice card opens by default exactly when cast readiness will demand a voice: a speaking,
/// non-group character without a profile. Otherwise it stays collapsed.</summary>
public class VoiceCardDefaultTests
{
    [Fact]
    public void Opens_for_speaking_role_without_voice_only()
    {
        Assert.True(Characters.CharactersListState.VoiceCardOpensByDefault(new CharacterSummary { Speaks = true }));
        Assert.False(Characters.CharactersListState.VoiceCardOpensByDefault(new CharacterSummary { Speaks = true, VoiceProfile = "warm baritone" }));
        Assert.False(Characters.CharactersListState.VoiceCardOpensByDefault(new CharacterSummary { Speaks = false }));
        Assert.False(Characters.CharactersListState.VoiceCardOpensByDefault(new CharacterSummary { Speaks = true, IsGroup = true }));
        Assert.False(Characters.CharactersListState.VoiceCardOpensByDefault(null));
    }
}
