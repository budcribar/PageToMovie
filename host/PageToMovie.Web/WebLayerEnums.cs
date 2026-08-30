using System.Text.Json.Serialization;

namespace PageToMovie.Web;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UiThemeMode
{
    Dark,
    Light,
    System
}

public static class WebLayerEnumExtensions
{
    private const string ApiSystem = "system";

    public static UiThemeMode ParseUiThemeMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "light" => UiThemeMode.Light,
            ApiSystem => UiThemeMode.System,
            _ => UiThemeMode.Dark
        };

    public static string ToCssTheme(this UiThemeMode theme) => theme switch
    {
        UiThemeMode.Dark => "dark",
        UiThemeMode.Light => "light",
        UiThemeMode.System => ApiSystem,
        _ => "dark"
    };
}
