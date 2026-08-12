using System.Text.Json.Serialization;

namespace PageToMovie.Web;

/// <summary>
/// Tooltip placement direction options for UI elements.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TooltipPlacementKind
{
    Top,
    Bottom,
    Left,
    Right,
    TopStart,
    TopEnd,
    BottomStart,
    BottomEnd,
    Auto
}

/// <summary>
/// Elevation and shadow style variations for visual card components.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardElevationStyleKind
{
    Flat,
    Raised,
    Bordered,
    Floating,
    Glassmorphism,
    Outlined,
    Inset
}

/// <summary>
/// Trigger source origin for tab navigation changes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TabChangeSourceKind
{
    UserClick,
    Programmatic,
    KeyboardNavigation,
    UrlHash,
    SwipeGesture
}

/// <summary>
/// Interaction modes for expanding dropdown menus.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DropdownTriggerModeKind
{
    Click,
    Hover,
    ContextMenu,
    Manual,
    DoubleClick
}

/// <summary>
/// Expansion behavior modes for accordion container items.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccordionExpandModeKind
{
    Single,
    Multiple,
    AccordionExclusive,
    Toggleable
}

/// <summary>
/// Target aspect ratio constraints for image preview displays.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImagePreviewAspectKind
{
    Original,
    Square,
    Widescreen16x9,
    Standard4x3,
    Portrait9x16,
    CustomCrop
}

/// <summary>
/// Operational state transitions of video player controls.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoPlayerStateKind
{
    Idle,
    Buffering,
    Playing,
    Paused,
    Seeking,
    Ended,
    Error
}

/// <summary>
/// Visualizer presentation modes for active audio playback.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioPlayerVisualizerKind
{
    None,
    Waveform,
    FrequencyBars,
    SpectrumAnalyzer,
    Oscilloscope,
    CircularRing
}

/// <summary>
/// Status state of clipboard copy actions in UI components.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardCopyStatusKind
{
    Idle,
    Copying,
    Success,
    Failed,
    PermissionDenied
}

/// <summary>
/// High-level keyboard shortcut action triggers across application views.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardShortcutKind
{
    Save,
    Cancel,
    Undo,
    Redo,
    PlayPause,
    ToggleMute,
    Fullscreen,
    Search,
    Delete,
    NavigateNext
}

/// <summary>
/// Extension methods for Blazor UI extended enum string conversions and parsing.
/// </summary>
public static class BlazorUiExtendedEnumExtensions
{
    public static AccordionExpandModeKind ParseAccordionExpandModeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "single" => AccordionExpandModeKind.Single,
                "multiple" => AccordionExpandModeKind.Multiple,
                "exclusive" or "accordion_exclusive" => AccordionExpandModeKind.AccordionExclusive,
                "toggleable" => AccordionExpandModeKind.Toggleable,
                _ => AccordionExpandModeKind.Single
            };

    public static AudioPlayerVisualizerKind ParseAudioPlayerVisualizerKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "none" => AudioPlayerVisualizerKind.None,
                "waveform" => AudioPlayerVisualizerKind.Waveform,
                "frequency_bars" or "bars" => AudioPlayerVisualizerKind.FrequencyBars,
                "spectrum" or "spectrum_analyzer" => AudioPlayerVisualizerKind.SpectrumAnalyzer,
                "oscilloscope" => AudioPlayerVisualizerKind.Oscilloscope,
                "circular_ring" or "ring" => AudioPlayerVisualizerKind.CircularRing,
                _ => AudioPlayerVisualizerKind.None
            };

    public static CardElevationStyleKind ParseCardElevationStyleKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "flat" => CardElevationStyleKind.Flat,
                "raised" => CardElevationStyleKind.Raised,
                "bordered" => CardElevationStyleKind.Bordered,
                "floating" => CardElevationStyleKind.Floating,
                "glassmorphism" or "glass" => CardElevationStyleKind.Glassmorphism,
                "outlined" => CardElevationStyleKind.Outlined,
                "inset" => CardElevationStyleKind.Inset,
                _ => CardElevationStyleKind.Flat
            };

    public static ClipboardCopyStatusKind ParseClipboardCopyStatusKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "idle" => ClipboardCopyStatusKind.Idle,
                "copying" => ClipboardCopyStatusKind.Copying,
                "success" => ClipboardCopyStatusKind.Success,
                "failed" => ClipboardCopyStatusKind.Failed,
                "permission_denied" or "denied" => ClipboardCopyStatusKind.PermissionDenied,
                _ => ClipboardCopyStatusKind.Idle
            };

    public static DropdownTriggerModeKind ParseDropdownTriggerModeKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "click" => DropdownTriggerModeKind.Click,
                "hover" => DropdownTriggerModeKind.Hover,
                "context_menu" => DropdownTriggerModeKind.ContextMenu,
                "manual" => DropdownTriggerModeKind.Manual,
                "double_click" or "dblclick" => DropdownTriggerModeKind.DoubleClick,
                _ => DropdownTriggerModeKind.Click
            };

    public static ImagePreviewAspectKind ParseImagePreviewAspectKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "original" => ImagePreviewAspectKind.Original,
                "square" or "1:1" => ImagePreviewAspectKind.Square,
                "16:9" or "16x9" or "widescreen" => ImagePreviewAspectKind.Widescreen16x9,
                "4:3" or "4x3" or "standard" => ImagePreviewAspectKind.Standard4x3,
                "9:16" or "9x16" or "portrait" => ImagePreviewAspectKind.Portrait9x16,
                "custom" or "custom_crop" => ImagePreviewAspectKind.CustomCrop,
                _ => ImagePreviewAspectKind.Original
            };

    public static KeyboardShortcutKind ParseKeyboardShortcutKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "save" => KeyboardShortcutKind.Save,
                "cancel" => KeyboardShortcutKind.Cancel,
                "undo" => KeyboardShortcutKind.Undo,
                "redo" => KeyboardShortcutKind.Redo,
                "play_pause" or "playpause" => KeyboardShortcutKind.PlayPause,
                "toggle_mute" or "mute" => KeyboardShortcutKind.ToggleMute,
                "fullscreen" => KeyboardShortcutKind.Fullscreen,
                "search" => KeyboardShortcutKind.Search,
                "delete" => KeyboardShortcutKind.Delete,
                "navigate_next" or "next" => KeyboardShortcutKind.NavigateNext,
                _ => KeyboardShortcutKind.Save
            };

    public static TabChangeSourceKind ParseTabChangeSourceKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "user_click" or "click" => TabChangeSourceKind.UserClick,
                "programmatic" => TabChangeSourceKind.Programmatic,
                "keyboard" or "keyboard_navigation" => TabChangeSourceKind.KeyboardNavigation,
                "url_hash" or "hash" => TabChangeSourceKind.UrlHash,
                "swipe" or "swipe_gesture" => TabChangeSourceKind.SwipeGesture,
                _ => TabChangeSourceKind.UserClick
            };

    public static TooltipPlacementKind ParseTooltipPlacementKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "top" => TooltipPlacementKind.Top,
                "bottom" => TooltipPlacementKind.Bottom,
                "left" => TooltipPlacementKind.Left,
                "right" => TooltipPlacementKind.Right,
                "top_start" or "topstart" => TooltipPlacementKind.TopStart,
                "top_end" or "topend" => TooltipPlacementKind.TopEnd,
                "bottom_start" or "bottomstart" => TooltipPlacementKind.BottomStart,
                "bottom_end" or "bottomend" => TooltipPlacementKind.BottomEnd,
                "auto" => TooltipPlacementKind.Auto,
                _ => TooltipPlacementKind.Auto
            };

    public static VideoPlayerStateKind ParseVideoPlayerStateKind(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "idle" => VideoPlayerStateKind.Idle,
                "buffering" => VideoPlayerStateKind.Buffering,
                "playing" => VideoPlayerStateKind.Playing,
                "paused" => VideoPlayerStateKind.Paused,
                "seeking" => VideoPlayerStateKind.Seeking,
                "ended" => VideoPlayerStateKind.Ended,
                "error" => VideoPlayerStateKind.Error,
                _ => VideoPlayerStateKind.Idle
            };

    public static AccordionExpandModeKind ToAccordionExpandModeKind(this string? value) => ParseAccordionExpandModeKind(value);

    public static string ToApiString(this TooltipPlacementKind kind) => kind switch
        {
            TooltipPlacementKind.Top => "top",
            TooltipPlacementKind.Bottom => "bottom",
            TooltipPlacementKind.Left => "left",
            TooltipPlacementKind.Right => "right",
            TooltipPlacementKind.TopStart => "top_start",
            TooltipPlacementKind.TopEnd => "top_end",
            TooltipPlacementKind.BottomStart => "bottom_start",
            TooltipPlacementKind.BottomEnd => "bottom_end",
            TooltipPlacementKind.Auto => "auto",
            _ => "auto"
        };

    public static string ToApiString(this CardElevationStyleKind style) => style switch
        {
            CardElevationStyleKind.Flat => "flat",
            CardElevationStyleKind.Raised => "raised",
            CardElevationStyleKind.Bordered => "bordered",
            CardElevationStyleKind.Floating => "floating",
            CardElevationStyleKind.Glassmorphism => "glassmorphism",
            CardElevationStyleKind.Outlined => "outlined",
            CardElevationStyleKind.Inset => "inset",
            _ => "flat"
        };

    public static string ToApiString(this TabChangeSourceKind source) => source switch
        {
            TabChangeSourceKind.UserClick => "user_click",
            TabChangeSourceKind.Programmatic => "programmatic",
            TabChangeSourceKind.KeyboardNavigation => "keyboard",
            TabChangeSourceKind.UrlHash => "url_hash",
            TabChangeSourceKind.SwipeGesture => "swipe",
            _ => "user_click"
        };

    public static string ToApiString(this DropdownTriggerModeKind mode) => mode switch
        {
            DropdownTriggerModeKind.Click => "click",
            DropdownTriggerModeKind.Hover => "hover",
            DropdownTriggerModeKind.ContextMenu => "context_menu",
            DropdownTriggerModeKind.Manual => "manual",
            DropdownTriggerModeKind.DoubleClick => "double_click",
            _ => "click"
        };

    public static string ToApiString(this AccordionExpandModeKind mode) => mode switch
        {
            AccordionExpandModeKind.Single => "single",
            AccordionExpandModeKind.Multiple => "multiple",
            AccordionExpandModeKind.AccordionExclusive => "exclusive",
            AccordionExpandModeKind.Toggleable => "toggleable",
            _ => "single"
        };

    public static string ToApiString(this ImagePreviewAspectKind aspect) => aspect switch
        {
            ImagePreviewAspectKind.Original => "original",
            ImagePreviewAspectKind.Square => "square",
            ImagePreviewAspectKind.Widescreen16x9 => "16:9",
            ImagePreviewAspectKind.Standard4x3 => "4:3",
            ImagePreviewAspectKind.Portrait9x16 => "9:16",
            ImagePreviewAspectKind.CustomCrop => "custom",
            _ => "original"
        };

    public static string ToApiString(this VideoPlayerStateKind state) => state switch
        {
            VideoPlayerStateKind.Idle => "idle",
            VideoPlayerStateKind.Buffering => "buffering",
            VideoPlayerStateKind.Playing => "playing",
            VideoPlayerStateKind.Paused => "paused",
            VideoPlayerStateKind.Seeking => "seeking",
            VideoPlayerStateKind.Ended => "ended",
            VideoPlayerStateKind.Error => "error",
            _ => "idle"
        };

    public static string ToApiString(this AudioPlayerVisualizerKind visualizer) => visualizer switch
        {
            AudioPlayerVisualizerKind.None => "none",
            AudioPlayerVisualizerKind.Waveform => "waveform",
            AudioPlayerVisualizerKind.FrequencyBars => "frequency_bars",
            AudioPlayerVisualizerKind.SpectrumAnalyzer => "spectrum",
            AudioPlayerVisualizerKind.Oscilloscope => "oscilloscope",
            AudioPlayerVisualizerKind.CircularRing => "circular_ring",
            _ => "none"
        };

    public static string ToApiString(this ClipboardCopyStatusKind status) => status switch
        {
            ClipboardCopyStatusKind.Idle => "idle",
            ClipboardCopyStatusKind.Copying => "copying",
            ClipboardCopyStatusKind.Success => "success",
            ClipboardCopyStatusKind.Failed => "failed",
            ClipboardCopyStatusKind.PermissionDenied => "permission_denied",
            _ => "idle"
        };

    public static string ToApiString(this KeyboardShortcutKind shortcut) => shortcut switch
        {
            KeyboardShortcutKind.Save => "save",
            KeyboardShortcutKind.Cancel => "cancel",
            KeyboardShortcutKind.Undo => "undo",
            KeyboardShortcutKind.Redo => "redo",
            KeyboardShortcutKind.PlayPause => "play_pause",
            KeyboardShortcutKind.ToggleMute => "toggle_mute",
            KeyboardShortcutKind.Fullscreen => "fullscreen",
            KeyboardShortcutKind.Search => "search",
            KeyboardShortcutKind.Delete => "delete",
            KeyboardShortcutKind.NavigateNext => "navigate_next",
            _ => "save"
        };

    public static AudioPlayerVisualizerKind ToAudioPlayerVisualizerKind(this string? value) => ParseAudioPlayerVisualizerKind(value);

    public static CardElevationStyleKind ToCardElevationStyleKind(this string? value) => ParseCardElevationStyleKind(value);

    public static ClipboardCopyStatusKind ToClipboardCopyStatusKind(this string? value) => ParseClipboardCopyStatusKind(value);

    public static DropdownTriggerModeKind ToDropdownTriggerModeKind(this string? value) => ParseDropdownTriggerModeKind(value);

    public static ImagePreviewAspectKind ToImagePreviewAspectKind(this string? value) => ParseImagePreviewAspectKind(value);

    public static KeyboardShortcutKind ToKeyboardShortcutKind(this string? value) => ParseKeyboardShortcutKind(value);

    public static TabChangeSourceKind ToTabChangeSourceKind(this string? value) => ParseTabChangeSourceKind(value);

    public static TooltipPlacementKind ToTooltipPlacementKind(this string? value) => ParseTooltipPlacementKind(value);

    public static VideoPlayerStateKind ToVideoPlayerStateKind(this string? value) => ParseVideoPlayerStateKind(value);

}
