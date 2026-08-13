namespace PageToMovie.Web.Components;

public partial class LocationLookPanel : LookPanelBase
{
    /// <summary>Static coach chips for the tweak-mic popover (no API cost).</summary>
    internal static readonly IReadOnlyList<string> PlateTweakSuggestions = new[]
    {
        "make the trees taller",
        "warmer late-day light",
        "wider shot of the courtyard",
        "fewer people in the background",
        "wet stone after rain",
        "clearer sky",
    };

    protected override string DefaultPrefix => "loc";
}
