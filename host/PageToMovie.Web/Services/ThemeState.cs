namespace PageToMovie.Web.Services;

/// <summary>
/// Scoped UI theme preference ("dark" | "light" | "system"), sourced from the
/// active project's config (<c>ui_theme</c> in pipeline_config.json). Components that
/// render theme-dependent chrome (e.g. NavMenu) apply it to the DOM via JS interop and
/// notify subscribers so the preference stays in sync across the app.
/// </summary>
public sealed class ThemeState
{
    public string Preference { get; private set; } = "dark";

    public UiThemeMode Mode => WebLayerEnumExtensions.ParseUiThemeMode(Preference);

    public event Action? Changed;

    public void Set(string? preference)
    {
        var v = Normalize(preference);
        if (string.Equals(Preference, v, StringComparison.Ordinal)) return;
        Preference = v;
        Changed?.Invoke();
    }

    public void Set(UiThemeMode mode) => Set(mode.ToCssTheme());

    public static string Normalize(string? v) =>
        WebLayerEnumExtensions.ParseUiThemeMode(v).ToCssTheme();
}
