using System.Text.Json.Serialization;

namespace PageToMovie.ScreenplayEditor.Models;

/// <summary>
/// Tabs available in the screenplay outline sidebar.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayOutlineTab
{
    Scenes = 0,
    Cast = 1,
    Locations = 2,
    Beats = 3,
    Notes = 4,
    Shots = 5
}

/// <summary>
/// Active view modes for the screenplay editor.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayEditorActiveView
{
    Outline = 0,
    Screenplay = 1,
    Characters = 2,
    ShotPlan = 3,
    Review = 4,
    Credits = 5,
    Visual = 6,
    Code = 7,
    Split = 8
}

/// <summary>
/// Types of modal dialogs in the screenplay editor UI.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenplayModalType
{
    None = 0,
    Import = 1,
    Export = 2,
    Settings = 3,
    CharacterDetails = 4,
    ShotDetails = 5,
    ConfirmDelete = 6,
    Help = 7
}

/// <summary>
/// Extension methods for ScreenplayEditor layer enums.
/// </summary>
public static class EditorLayerEnumExtensions
{
    public static string ToDisplayString(this ScreenplayOutlineTab tab) => tab switch
    {
        ScreenplayOutlineTab.Scenes => "Scenes",
        ScreenplayOutlineTab.Cast => "Cast",
        ScreenplayOutlineTab.Locations => "Locations",
        ScreenplayOutlineTab.Beats => "Beats",
        ScreenplayOutlineTab.Notes => "Notes",
        ScreenplayOutlineTab.Shots => "Shots",
        _ => "Scenes"
    };

    public static string ToDisplayString(this ScreenplayEditorActiveView view) => view switch
    {
        ScreenplayEditorActiveView.Outline => "Outline",
        ScreenplayEditorActiveView.Screenplay => "Screenplay",
        ScreenplayEditorActiveView.Characters => "Characters",
        ScreenplayEditorActiveView.ShotPlan => "Shot Plan",
        ScreenplayEditorActiveView.Review => "Review",
        ScreenplayEditorActiveView.Credits => "Credits",
        ScreenplayEditorActiveView.Visual => "Visual",
        ScreenplayEditorActiveView.Code => "Code",
        ScreenplayEditorActiveView.Split => "Split View",
        _ => "Screenplay"
    };
}
