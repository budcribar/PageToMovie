using System.Text.Json.Serialization;

namespace PageToMovie.Web;

/// <summary>
/// Routes available for primary page navigation in the Blazor UI.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageNavigationRoute
{
    Home,
    Adaptation,
    Characters,
    Scenes,
    Review,
    Configuration,
    Admin,
    Cost,
    About,
    Unknown
}

/// <summary>
/// Max-width layout container modes for responsive UI templates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutContainerWidth
{
    Fluid,
    FixedSmall,
    FixedMedium,
    FixedLarge,
    FixedExtraLarge,
    FullViewport
}

/// <summary>
/// Collapsing behavior for navigation sidebars.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SidebarCollapseMode
{
    Expanded,
    Collapsed,
    Hidden,
    MiniOverlay,
    Auto
}

/// <summary>
/// Visual and operational state of a UI component.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComponentDisplayState
{
    Hidden,
    Visible,
    Loading,
    Disabled,
    Error,
    Empty
}

/// <summary>
/// Direction for sorting table columns.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TableSortColumnDirection
{
    None,
    Ascending,
    Descending
}

/// <summary>
/// Pagination navigation modes for data tables and lists.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaginationMode
{
    Paged,
    InfiniteScroll,
    LoadMoreButton,
    VirtualScroll
}

/// <summary>
/// HTML/Blazor input control type categories.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormInputType
{
    Text,
    Password,
    Email,
    Number,
    TextArea,
    Select,
    Checkbox,
    Radio,
    SwitchToggle,
    ColorPicker,
    DatePicker,
    File
}

/// <summary>
/// Validation status of form controls or composite forms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormValidationState
{
    Untouched,
    Validating,
    Valid,
    Invalid,
    Warning
}

/// <summary>
/// Screen positioning for toast notifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToastPosition
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
    TopCenter,
    BottomCenter
}

/// <summary>
/// Dismissal and expiration behavior for alert messages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertDismissBehavior
{
    Manual,
    AutoDismiss,
    Permanent,
    TimerWithHoverPause
}

/// <summary>
/// Placement location of a hover tooltip relative to its trigger element.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TooltipPlacement
{
    Top,
    Bottom,
    Left,
    Right,
    TopStart,
    TopEnd,
    BottomStart,
    BottomEnd
}

/// <summary>
/// Shadow and elevation styling for UI card components.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardElevationStyle
{
    Flat,
    Raised,
    Bordered,
    Floating,
    Glassmorphism
}

/// <summary>
/// Source trigger that initiated a tab navigation change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TabChangeSource
{
    UserClick,
    Programmatic,
    KeyboardNavigation,
    UrlHash
}

/// <summary>
/// User gesture required to trigger dropdown menus.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DropdownTriggerMode
{
    Click,
    Hover,
    ContextMenu,
    Manual
}

/// <summary>
/// Expansion interaction mode for accordion panel groups.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccordionExpandMode
{
    Single,
    Multiple,
    AllExpanded,
    AllCollapsed
}

/// <summary>
/// Aspect ratio framing for image previews and asset thumbnails.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImagePreviewAspect
{
    Square1x1,
    Standard4x3,
    Widescreen16x9,
    Cinema21x9,
    Portrait9x16,
    Original
}

/// <summary>
/// Playback state of the media video player control.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoPlayerState
{
    Idle,
    Buffering,
    Playing,
    Paused,
    Ended,
    Error
}

/// <summary>
/// Audio visualizer rendering style during audio playback.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioPlayerVisualizer
{
    None,
    Waveform,
    FrequencyBars,
    SpectrumAnalyzer,
    Oscilloscope
}

/// <summary>
/// Status of an asynchronous text/asset clipboard copy action.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardCopyStatus
{
    Idle,
    Copying,
    Success,
    Failed
}

/// <summary>
/// Standardized keyboard shortcut keys recognized in global event listeners.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardShortcutKey
{
    Enter,
    Escape,
    Space,
    Tab,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    KeyDelete,
    KeyBackspace
}

/// <summary>
/// Extension methods and string parsers for Blazor UI enums.
/// </summary>
public static class BlazorUiEnumExtensions
{
    public static AccordionExpandMode ParseAccordionExpandMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "single" => AccordionExpandMode.Single,
                "multiple" => AccordionExpandMode.Multiple,
                "all_expanded" => AccordionExpandMode.AllExpanded,
                "all_collapsed" => AccordionExpandMode.AllCollapsed,
                _ => AccordionExpandMode.Single
            };

    public static AlertDismissBehavior ParseAlertDismissBehavior(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "manual" => AlertDismissBehavior.Manual,
                "auto_dismiss" or "auto" => AlertDismissBehavior.AutoDismiss,
                "permanent" => AlertDismissBehavior.Permanent,
                "timer_pause" => AlertDismissBehavior.TimerWithHoverPause,
                _ => AlertDismissBehavior.Manual
            };

    public static AudioPlayerVisualizer ParseAudioPlayerVisualizer(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "none" => AudioPlayerVisualizer.None,
                "waveform" => AudioPlayerVisualizer.Waveform,
                "frequency_bars" or "bars" => AudioPlayerVisualizer.FrequencyBars,
                "spectrum" or "spectrum_analyzer" => AudioPlayerVisualizer.SpectrumAnalyzer,
                "oscilloscope" => AudioPlayerVisualizer.Oscilloscope,
                _ => AudioPlayerVisualizer.None
            };

    public static CardElevationStyle ParseCardElevationStyle(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "flat" => CardElevationStyle.Flat,
                "raised" => CardElevationStyle.Raised,
                "bordered" => CardElevationStyle.Bordered,
                "floating" => CardElevationStyle.Floating,
                "glassmorphism" or "glass" => CardElevationStyle.Glassmorphism,
                _ => CardElevationStyle.Flat
            };

    public static ClipboardCopyStatus ParseClipboardCopyStatus(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "idle" => ClipboardCopyStatus.Idle,
                "copying" => ClipboardCopyStatus.Copying,
                "success" => ClipboardCopyStatus.Success,
                "failed" => ClipboardCopyStatus.Failed,
                _ => ClipboardCopyStatus.Idle
            };

    public static ComponentDisplayState ParseComponentDisplayState(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "hidden" => ComponentDisplayState.Hidden,
                "visible" => ComponentDisplayState.Visible,
                "loading" => ComponentDisplayState.Loading,
                "disabled" => ComponentDisplayState.Disabled,
                "error" => ComponentDisplayState.Error,
                "empty" => ComponentDisplayState.Empty,
                _ => ComponentDisplayState.Visible
            };

    public static DropdownTriggerMode ParseDropdownTriggerMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "click" => DropdownTriggerMode.Click,
                "hover" => DropdownTriggerMode.Hover,
                "context_menu" => DropdownTriggerMode.ContextMenu,
                "manual" => DropdownTriggerMode.Manual,
                _ => DropdownTriggerMode.Click
            };

    public static FormInputType ParseFormInputType(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "text" => FormInputType.Text,
                "password" => FormInputType.Password,
                "email" => FormInputType.Email,
                "number" => FormInputType.Number,
                "textarea" => FormInputType.TextArea,
                "select" => FormInputType.Select,
                "checkbox" => FormInputType.Checkbox,
                "radio" => FormInputType.Radio,
                "switch" => FormInputType.SwitchToggle,
                "color" => FormInputType.ColorPicker,
                "date" => FormInputType.DatePicker,
                "file" => FormInputType.File,
                _ => FormInputType.Text
            };

    public static FormValidationState ParseFormValidationState(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "untouched" => FormValidationState.Untouched,
                "validating" => FormValidationState.Validating,
                "valid" => FormValidationState.Valid,
                "invalid" => FormValidationState.Invalid,
                "warning" => FormValidationState.Warning,
                _ => FormValidationState.Untouched
            };

    public static ImagePreviewAspect ParseImagePreviewAspect(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "1:1" or "square" => ImagePreviewAspect.Square1x1,
                "4:3" => ImagePreviewAspect.Standard4x3,
                "16:9" => ImagePreviewAspect.Widescreen16x9,
                "21:9" => ImagePreviewAspect.Cinema21x9,
                "9:16" => ImagePreviewAspect.Portrait9x16,
                "original" => ImagePreviewAspect.Original,
                _ => ImagePreviewAspect.Original
            };

    public static KeyboardShortcutKey ParseKeyboardShortcutKey(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "enter" => KeyboardShortcutKey.Enter,
                "escape" or "esc" => KeyboardShortcutKey.Escape,
                "space" => KeyboardShortcutKey.Space,
                "tab" => KeyboardShortcutKey.Tab,
                "arrow_up" or "up" => KeyboardShortcutKey.ArrowUp,
                "arrow_down" or "down" => KeyboardShortcutKey.ArrowDown,
                "arrow_left" or "left" => KeyboardShortcutKey.ArrowLeft,
                "arrow_right" or "right" => KeyboardShortcutKey.ArrowRight,
                "delete" or "del" => KeyboardShortcutKey.KeyDelete,
                "backspace" => KeyboardShortcutKey.KeyBackspace,
                _ => KeyboardShortcutKey.Enter
            };

    public static LayoutContainerWidth ParseLayoutContainerWidth(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fluid" => LayoutContainerWidth.Fluid,
                "fixed_sm" or "small" => LayoutContainerWidth.FixedSmall,
                "fixed_md" or "medium" => LayoutContainerWidth.FixedMedium,
                "fixed_lg" or "large" => LayoutContainerWidth.FixedLarge,
                "fixed_xl" or "xlarge" => LayoutContainerWidth.FixedExtraLarge,
                "full_viewport" or "full" => LayoutContainerWidth.FullViewport,
                _ => LayoutContainerWidth.Fluid
            };

    public static PageNavigationRoute ParsePageNavigationRoute(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "home" or "" => PageNavigationRoute.Home,
                "adaptation" => PageNavigationRoute.Adaptation,
                "characters" => PageNavigationRoute.Characters,
                "scenes" => PageNavigationRoute.Scenes,
                "review" => PageNavigationRoute.Review,
                "configuration" => PageNavigationRoute.Configuration,
                "admin" => PageNavigationRoute.Admin,
                "cost" => PageNavigationRoute.Cost,
                "about" => PageNavigationRoute.About,
                _ => PageNavigationRoute.Unknown
            };

    public static PaginationMode ParsePaginationMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "paged" => PaginationMode.Paged,
                "infinite_scroll" or "infinite" => PaginationMode.InfiniteScroll,
                "load_more" => PaginationMode.LoadMoreButton,
                "virtual_scroll" or "virtual" => PaginationMode.VirtualScroll,
                _ => PaginationMode.Paged
            };

    public static SidebarCollapseMode ParseSidebarCollapseMode(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "expanded" => SidebarCollapseMode.Expanded,
                "collapsed" => SidebarCollapseMode.Collapsed,
                "hidden" => SidebarCollapseMode.Hidden,
                "mini_overlay" or "mini" => SidebarCollapseMode.MiniOverlay,
                "auto" => SidebarCollapseMode.Auto,
                _ => SidebarCollapseMode.Expanded
            };

    public static TabChangeSource ParseTabChangeSource(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "user_click" or "click" => TabChangeSource.UserClick,
                "programmatic" => TabChangeSource.Programmatic,
                "keyboard" => TabChangeSource.KeyboardNavigation,
                "url_hash" or "hash" => TabChangeSource.UrlHash,
                _ => TabChangeSource.UserClick
            };

    public static TableSortColumnDirection ParseTableSortColumnDirection(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "asc" or "ascending" => TableSortColumnDirection.Ascending,
                "desc" or "descending" => TableSortColumnDirection.Descending,
                _ => TableSortColumnDirection.None
            };

    public static ToastPosition ParseToastPosition(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "top_right" => ToastPosition.TopRight,
                "top_left" => ToastPosition.TopLeft,
                "bottom_right" => ToastPosition.BottomRight,
                "bottom_left" => ToastPosition.BottomLeft,
                "top_center" => ToastPosition.TopCenter,
                "bottom_center" => ToastPosition.BottomCenter,
                _ => ToastPosition.TopRight
            };

    public static TooltipPlacement ParseTooltipPlacement(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "top" => TooltipPlacement.Top,
                "bottom" => TooltipPlacement.Bottom,
                "left" => TooltipPlacement.Left,
                "right" => TooltipPlacement.Right,
                "top_start" => TooltipPlacement.TopStart,
                "top_end" => TooltipPlacement.TopEnd,
                "bottom_start" => TooltipPlacement.BottomStart,
                "bottom_end" => TooltipPlacement.BottomEnd,
                _ => TooltipPlacement.Top
            };

    public static VideoPlayerState ParseVideoPlayerState(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "idle" => VideoPlayerState.Idle,
                "buffering" => VideoPlayerState.Buffering,
                "playing" => VideoPlayerState.Playing,
                "paused" => VideoPlayerState.Paused,
                "ended" => VideoPlayerState.Ended,
                "error" => VideoPlayerState.Error,
                _ => VideoPlayerState.Idle
            };

    public static string ToApiString(this PageNavigationRoute route) => route switch
        {
            PageNavigationRoute.Home => "home",
            PageNavigationRoute.Adaptation => "adaptation",
            PageNavigationRoute.Characters => "characters",
            PageNavigationRoute.Scenes => "scenes",
            PageNavigationRoute.Review => "review",
            PageNavigationRoute.Configuration => "configuration",
            PageNavigationRoute.Admin => "admin",
            PageNavigationRoute.Cost => "cost",
            PageNavigationRoute.About => "about",
            _ => "unknown"
        };

    public static string ToApiString(this LayoutContainerWidth width) => width switch
        {
            LayoutContainerWidth.Fluid => "fluid",
            LayoutContainerWidth.FixedSmall => "fixed_sm",
            LayoutContainerWidth.FixedMedium => "fixed_md",
            LayoutContainerWidth.FixedLarge => "fixed_lg",
            LayoutContainerWidth.FixedExtraLarge => "fixed_xl",
            LayoutContainerWidth.FullViewport => "full_viewport",
            _ => "fluid"
        };

    public static string ToApiString(this SidebarCollapseMode mode) => mode switch
        {
            SidebarCollapseMode.Expanded => "expanded",
            SidebarCollapseMode.Collapsed => "collapsed",
            SidebarCollapseMode.Hidden => "hidden",
            SidebarCollapseMode.MiniOverlay => "mini_overlay",
            SidebarCollapseMode.Auto => "auto",
            _ => "expanded"
        };

    public static string ToApiString(this ComponentDisplayState state) => state switch
        {
            ComponentDisplayState.Hidden => "hidden",
            ComponentDisplayState.Visible => "visible",
            ComponentDisplayState.Loading => "loading",
            ComponentDisplayState.Disabled => "disabled",
            ComponentDisplayState.Error => "error",
            ComponentDisplayState.Empty => "empty",
            _ => "visible"
        };

    public static string ToApiString(this TableSortColumnDirection direction) => direction switch
        {
            TableSortColumnDirection.None => "none",
            TableSortColumnDirection.Ascending => "asc",
            TableSortColumnDirection.Descending => "desc",
            _ => "none"
        };

    public static string ToApiString(this PaginationMode mode) => mode switch
        {
            PaginationMode.Paged => "paged",
            PaginationMode.InfiniteScroll => "infinite_scroll",
            PaginationMode.LoadMoreButton => "load_more",
            PaginationMode.VirtualScroll => "virtual_scroll",
            _ => "paged"
        };

    public static string ToApiString(this FormInputType type) => type switch
        {
            FormInputType.Text => "text",
            FormInputType.Password => "password",
            FormInputType.Email => "email",
            FormInputType.Number => "number",
            FormInputType.TextArea => "textarea",
            FormInputType.Select => "select",
            FormInputType.Checkbox => "checkbox",
            FormInputType.Radio => "radio",
            FormInputType.SwitchToggle => "switch",
            FormInputType.ColorPicker => "color",
            FormInputType.DatePicker => "date",
            FormInputType.File => "file",
            _ => "text"
        };

    public static string ToApiString(this FormValidationState state) => state switch
        {
            FormValidationState.Untouched => "untouched",
            FormValidationState.Validating => "validating",
            FormValidationState.Valid => "valid",
            FormValidationState.Invalid => "invalid",
            FormValidationState.Warning => "warning",
            _ => "untouched"
        };

    public static string ToApiString(this ToastPosition position) => position switch
        {
            ToastPosition.TopRight => "top_right",
            ToastPosition.TopLeft => "top_left",
            ToastPosition.BottomRight => "bottom_right",
            ToastPosition.BottomLeft => "bottom_left",
            ToastPosition.TopCenter => "top_center",
            ToastPosition.BottomCenter => "bottom_center",
            _ => "top_right"
        };

    public static string ToApiString(this AlertDismissBehavior behavior) => behavior switch
        {
            AlertDismissBehavior.Manual => "manual",
            AlertDismissBehavior.AutoDismiss => "auto_dismiss",
            AlertDismissBehavior.Permanent => "permanent",
            AlertDismissBehavior.TimerWithHoverPause => "timer_pause",
            _ => "manual"
        };

    public static string ToApiString(this TooltipPlacement placement) => placement switch
        {
            TooltipPlacement.Top => "top",
            TooltipPlacement.Bottom => "bottom",
            TooltipPlacement.Left => "left",
            TooltipPlacement.Right => "right",
            TooltipPlacement.TopStart => "top_start",
            TooltipPlacement.TopEnd => "top_end",
            TooltipPlacement.BottomStart => "bottom_start",
            TooltipPlacement.BottomEnd => "bottom_end",
            _ => "top"
        };

    public static string ToApiString(this CardElevationStyle style) => style switch
        {
            CardElevationStyle.Flat => "flat",
            CardElevationStyle.Raised => "raised",
            CardElevationStyle.Bordered => "bordered",
            CardElevationStyle.Floating => "floating",
            CardElevationStyle.Glassmorphism => "glassmorphism",
            _ => "flat"
        };

    public static string ToApiString(this TabChangeSource source) => source switch
        {
            TabChangeSource.UserClick => "user_click",
            TabChangeSource.Programmatic => "programmatic",
            TabChangeSource.KeyboardNavigation => "keyboard",
            TabChangeSource.UrlHash => "url_hash",
            _ => "user_click"
        };

    public static string ToApiString(this DropdownTriggerMode mode) => mode switch
        {
            DropdownTriggerMode.Click => "click",
            DropdownTriggerMode.Hover => "hover",
            DropdownTriggerMode.ContextMenu => "context_menu",
            DropdownTriggerMode.Manual => "manual",
            _ => "click"
        };

    public static string ToApiString(this AccordionExpandMode mode) => mode switch
        {
            AccordionExpandMode.Single => "single",
            AccordionExpandMode.Multiple => "multiple",
            AccordionExpandMode.AllExpanded => "all_expanded",
            AccordionExpandMode.AllCollapsed => "all_collapsed",
            _ => "single"
        };

    public static string ToApiString(this ImagePreviewAspect aspect) => aspect switch
        {
            ImagePreviewAspect.Square1x1 => "1:1",
            ImagePreviewAspect.Standard4x3 => "4:3",
            ImagePreviewAspect.Widescreen16x9 => "16:9",
            ImagePreviewAspect.Cinema21x9 => "21:9",
            ImagePreviewAspect.Portrait9x16 => "9:16",
            ImagePreviewAspect.Original => "original",
            _ => "original"
        };

    public static string ToApiString(this VideoPlayerState state) => state switch
        {
            VideoPlayerState.Idle => "idle",
            VideoPlayerState.Buffering => "buffering",
            VideoPlayerState.Playing => "playing",
            VideoPlayerState.Paused => "paused",
            VideoPlayerState.Ended => "ended",
            VideoPlayerState.Error => "error",
            _ => "idle"
        };

    public static string ToApiString(this AudioPlayerVisualizer visualizer) => visualizer switch
        {
            AudioPlayerVisualizer.None => "none",
            AudioPlayerVisualizer.Waveform => "waveform",
            AudioPlayerVisualizer.FrequencyBars => "frequency_bars",
            AudioPlayerVisualizer.SpectrumAnalyzer => "spectrum",
            AudioPlayerVisualizer.Oscilloscope => "oscilloscope",
            _ => "none"
        };

    public static string ToApiString(this ClipboardCopyStatus status) => status switch
        {
            ClipboardCopyStatus.Idle => "idle",
            ClipboardCopyStatus.Copying => "copying",
            ClipboardCopyStatus.Success => "success",
            ClipboardCopyStatus.Failed => "failed",
            _ => "idle"
        };

    public static string ToApiString(this KeyboardShortcutKey key) => key switch
        {
            KeyboardShortcutKey.Enter => "enter",
            KeyboardShortcutKey.Escape => "escape",
            KeyboardShortcutKey.Space => "space",
            KeyboardShortcutKey.Tab => "tab",
            KeyboardShortcutKey.ArrowUp => "arrow_up",
            KeyboardShortcutKey.ArrowDown => "arrow_down",
            KeyboardShortcutKey.ArrowLeft => "arrow_left",
            KeyboardShortcutKey.ArrowRight => "arrow_right",
            KeyboardShortcutKey.KeyDelete => "delete",
            KeyboardShortcutKey.KeyBackspace => "backspace",
            _ => "enter"
        };

}
