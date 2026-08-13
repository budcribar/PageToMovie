using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Components;
using Xunit;

namespace PageToMovie.Tests;

public class LookPanelBaseTests
{
    [Fact]
    public void Prefix_UsesDefaultUntilTestIdIsSet()
    {
        var panel = new Harness { DefaultPrefixValue = "look" };
        Assert.Equal("look", panel.PrefixPublic);
        Assert.Equal("look-imgedit", panel.ImgEditFieldIdPublic);
        Assert.Equal("look-imgedit-voice", panel.ImgEditVoiceTestIdPublic);

        panel.SetTestId("char-edit");
        Assert.Equal("char-edit", panel.PrefixPublic);
        Assert.Equal("char-edit-imgedit", panel.ImgEditFieldIdPublic);

        var location = new Harness { DefaultPrefixValue = "loc" };
        Assert.Equal("loc", location.PrefixPublic);
        location.SetTestId("loc-edit");
        Assert.Equal("loc-edit-imgedit-voice", location.ImgEditVoiceTestIdPublic);
    }

    [Fact]
    public async Task OnTweakCommittedAsync_TrimsAndRaisesBothCallbacks()
    {
        string? instruction = null;
        string? requested = null;
        var panel = new Harness { DefaultPrefixValue = "look" };
        panel.SetImageEditInstructionChanged(EventCallback.Factory.Create<string>(this, v => instruction = v));
        panel.SetOnTweakRequested(EventCallback.Factory.Create<string>(this, v => requested = v));

        await panel.CommitAsync("  shorter hair  ");
        Assert.Equal("shorter hair", instruction);
        Assert.Equal("shorter hair", requested);

        instruction = "kept";
        requested = "kept";
        await panel.CommitAsync("   ");
        Assert.Equal("kept", instruction);
        Assert.Equal("kept", requested);
    }

    private sealed class Harness : LookPanelBase
    {
        public string DefaultPrefixValue { get; init; } = "look";
        protected override string DefaultPrefix => DefaultPrefixValue;

        public string PrefixPublic => Prefix;
        public string ImgEditFieldIdPublic => ImgEditFieldId;
        public string ImgEditVoiceTestIdPublic => ImgEditVoiceTestId;

        public void SetTestId(string? value) => TestId = value;
        public void SetImageEditInstructionChanged(EventCallback<string> callback) =>
            ImageEditInstructionChanged = callback;
        public void SetOnTweakRequested(EventCallback<string> callback) =>
            OnTweakRequested = callback;

        public Task CommitAsync(string instruction) => OnTweakCommittedAsync(instruction);
    }
}
