namespace PageToMovie.Web.Components;

public partial class CharacterLookPanel : LookPanelBase
{
    /// <summary>Static coach chips for the tweak-mic popover (no API cost).</summary>
    internal static readonly IReadOnlyList<string> FaceTweakSuggestions = new[]
    {
        "make his beard longer",
        "remove the beard",
        "shorter hair",
        "softer light on the face",
        "look a little older",
        "more front-facing",
    };

    protected override string DefaultPrefix => "look";
}
