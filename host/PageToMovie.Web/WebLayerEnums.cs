using System.Text.Json.Serialization;

namespace PageToMovie.Web;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NavMenuItem
{
    Home,
    Adaptation,
    Characters,
    Scenes,
    Review,
    Configuration,
    Admin,
    Cost,
    About
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdminSectionTab
{
    SystemInfo,
    Jobs,
    Logs,
    Storage,
    LoadSim,
    Settings,
    Diagnostics
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModalDialogSizePreset
{
    Small,
    Medium,
    Large,
    ExtraLarge,
    Full
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UiThemeMode
{
    Dark,
    Light,
    System
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToastNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BadgeColorStyle
{
    Primary,
    Secondary,
    Success,
    Danger,
    Warning,
    Info,
    Dark,
    Light
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ButtonVariantStyle
{
    Primary,
    Secondary,
    Success,
    Danger,
    Warning,
    Info,
    Outline,
    Ghost,
    Link
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectSortField
{
    Name,
    CreatedAt,
    UpdatedAt,
    CharacterCount,
    SceneCount,
    Status
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectSortDirection
{
    Ascending,
    Descending
}

public static class WebLayerEnumExtensions
{
    public static UiThemeMode ParseUiThemeMode(string? value) =>
            value?.ToLowerInvariant() switch
            {
                "light" => UiThemeMode.Light,
                "system" => UiThemeMode.System,
                _ => UiThemeMode.Dark
            };

    public static string ToApiString(this ProjectSortField field) => field switch
        {
            ProjectSortField.Name => "name",
            ProjectSortField.CreatedAt => "created_at",
            ProjectSortField.UpdatedAt => "updated_at",
            ProjectSortField.CharacterCount => "character_count",
            ProjectSortField.SceneCount => "scene_count",
            ProjectSortField.Status => "status",
            _ => "updated_at"
        };

    public static string ToApiString(this ProjectSortDirection dir) => dir switch
        {
            ProjectSortDirection.Ascending => "asc",
            ProjectSortDirection.Descending => "desc",
            _ => "asc"
        };

    public static string ToCssClass(this ModalDialogSizePreset size) => size switch
        {
            ModalDialogSizePreset.Small => "modal-sm",
            ModalDialogSizePreset.Medium => "modal-md",
            ModalDialogSizePreset.Large => "modal-lg",
            ModalDialogSizePreset.ExtraLarge => "modal-xl",
            ModalDialogSizePreset.Full => "modal-full",
            _ => "modal-md"
        };

    public static string ToCssClass(this ToastNotificationSeverity severity) => severity switch
        {
            ToastNotificationSeverity.Info => "toast-info",
            ToastNotificationSeverity.Success => "toast-success",
            ToastNotificationSeverity.Warning => "toast-warning",
            ToastNotificationSeverity.Error => "toast-error",
            _ => "toast-info"
        };

    public static string ToCssClass(this BadgeColorStyle style) => style switch
        {
            BadgeColorStyle.Primary => "badge-primary",
            BadgeColorStyle.Secondary => "badge-secondary",
            BadgeColorStyle.Success => "badge-success",
            BadgeColorStyle.Danger => "badge-danger",
            BadgeColorStyle.Warning => "badge-warning",
            BadgeColorStyle.Info => "badge-info",
            BadgeColorStyle.Dark => "badge-dark",
            BadgeColorStyle.Light => "badge-light",
            _ => "badge-primary"
        };

    public static string ToCssClass(this ButtonVariantStyle variant) => variant switch
        {
            ButtonVariantStyle.Primary => "btn-primary",
            ButtonVariantStyle.Secondary => "btn-secondary",
            ButtonVariantStyle.Success => "btn-success",
            ButtonVariantStyle.Danger => "btn-danger",
            ButtonVariantStyle.Warning => "btn-warning",
            ButtonVariantStyle.Info => "btn-info",
            ButtonVariantStyle.Outline => "btn-outline",
            ButtonVariantStyle.Ghost => "btn-ghost",
            ButtonVariantStyle.Link => "btn-link",
            _ => "btn-primary"
        };

    public static string ToCssTheme(this UiThemeMode theme) => theme switch
        {
            UiThemeMode.Dark => "dark",
            UiThemeMode.Light => "light",
            UiThemeMode.System => "system",
            _ => "dark"
        };

    public static string ToRoutePath(this NavMenuItem item) => item switch
        {
            NavMenuItem.Home => "",
            NavMenuItem.Adaptation => "adaptation",
            NavMenuItem.Characters => "characters",
            NavMenuItem.Scenes => "scenes",
            NavMenuItem.Review => "review",
            NavMenuItem.Configuration => "configuration",
            NavMenuItem.Admin => "admin",
            NavMenuItem.Cost => "cost",
            NavMenuItem.About => "about",
            _ => ""
        };

    public static string ToTabId(this AdminSectionTab tab) => tab switch
        {
            AdminSectionTab.SystemInfo => "system",
            AdminSectionTab.Jobs => "jobs",
            AdminSectionTab.Logs => "logs",
            AdminSectionTab.Storage => "storage",
            AdminSectionTab.LoadSim => "loadsim",
            AdminSectionTab.Settings => "settings",
            AdminSectionTab.Diagnostics => "diagnostics",
            _ => "system"
        };

}
