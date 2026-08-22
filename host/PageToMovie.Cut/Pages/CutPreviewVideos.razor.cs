using Microsoft.AspNetCore.Components;
using PageToMovie.Cut.Cut;

namespace PageToMovie.Cut.Pages;

public partial class CutPreviewVideos : ComponentBase
{
    [Parameter] public bool Freeze { get; set; }
    [Parameter] public string? ClipSrc { get; set; }
    [Parameter] public string? MovieSrc { get; set; }
    [Parameter] public bool ShowMovie { get; set; }
    [Parameter] public EventCallback OnClipMetadata { get; set; }

    internal ElementReference ClipPlayer { get; set; }
    internal ElementReference MoviePlayer { get; set; }

    protected override bool ShouldRender() => !CutPlayClock.FreezePreviewMarkup(Freeze);
}
