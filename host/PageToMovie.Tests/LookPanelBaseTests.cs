using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Components;
using Xunit;

namespace PageToMovie.Tests;

public class LookPanelBaseTests
{
    [Fact]
    public void CharacterAndLocationPanels_KeepDistinctPrefixesAndSuggestions()
    {
        Assert.Equal(6, CharacterLookPanel.FaceTweakSuggestions.Count);
        Assert.Contains("make his beard longer", CharacterLookPanel.FaceTweakSuggestions);
        Assert.Equal(6, LocationLookPanel.PlateTweakSuggestions.Count);
        Assert.Contains("make the trees taller", LocationLookPanel.PlateTweakSuggestions);

        var character = new CharacterLookPanelHarness();
        Assert.Equal("look", character.PrefixPublic);
        character.TestId = "char-edit";
        Assert.Equal("char-edit", character.PrefixPublic);
        Assert.Equal("char-edit-imgedit", character.ImgEditFieldIdPublic);

        var location = new LocationLookPanelHarness();
        Assert.Equal("loc", location.PrefixPublic);
        location.TestId = "loc-edit";
        Assert.Equal("loc-edit", location.PrefixPublic);
        Assert.Equal("loc-edit-imgedit-voice", location.ImgEditVoiceTestIdPublic);
    }

    [Fact]
    public async Task OnTweakCommittedAsync_TrimsAndRaisesBothCallbacks()
    {
        string? instruction = null;
        string? requested = null;
        var panel = new CharacterLookPanelHarness
        {
            ImageEditInstructionChanged = EventCallback.Factory.Create<string>(this, v => instruction = v),
            OnTweakRequested = EventCallback.Factory.Create<string>(this, v => requested = v),
        };

        await panel.CommitAsync("  shorter hair  ");
        Assert.Equal("shorter hair", instruction);
        Assert.Equal("shorter hair", requested);

        instruction = "kept";
        requested = "kept";
        await panel.CommitAsync("   ");
        Assert.Equal("kept", instruction);
        Assert.Equal("kept", requested);
    }

    private sealed class CharacterLookPanelHarness : CharacterLookPanel
    {
        public string PrefixPublic => Prefix;
        public string ImgEditFieldIdPublic => ImgEditFieldId;
        public string ImgEditVoiceTestIdPublic => ImgEditVoiceTestId;
        public Task CommitAsync(string instruction) => OnTweakCommittedAsync(instruction);
    }

    private sealed class LocationLookPanelHarness : LocationLookPanel
    {
        public string PrefixPublic => Prefix;
        public string ImgEditVoiceTestIdPublic => ImgEditVoiceTestId;
    }
}
