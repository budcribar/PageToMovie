using Microsoft.AspNetCore.Components;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Pages;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutElementFocusTests
{
    [Fact]
    public async Task EditDuration_when_inspector_is_not_mounted_does_not_throw()
    {
        await CutTimeline.EditDurationAsync(inspector: null);

        var unmounted = new CutTimeline_TextInspector();
        await CutTimeline.EditDurationAsync(unmounted);
    }

    [Fact]
    public async Task EditDuration_skips_unconfigured_music_trim_handle()
    {
        Assert.False(CutElementFocus.IsReady(default));
        await CutElementFocus.TryFocusAsync(default);
        await CutElementFocus.TryFocusAsync(new ElementReference());
    }
}
