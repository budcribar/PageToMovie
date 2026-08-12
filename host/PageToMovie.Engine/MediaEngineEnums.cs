namespace PageToMovie.Engine;

public enum AspectRatio
{
    Ratio16x9,
    Ratio9x16,
    Ratio1x1,
    Ratio4x3,
    Ratio21x9
}

public enum CameraLens
{
    Lens24mm,
    Lens35mm,
    Lens50mm,
    Lens85mm
}

public enum CameraMovementKind
{
    DollyPush,
    TripodHold,
    PanLeft,
    PanRight,
    TiltUp
}

public enum VideoResolution
{
    Res720p,
    Res1080p,
    Res4k
}

public enum PacingMood
{
    Slow,
    Moderate,
    Fast,
    Frenetic
}

public enum MusicTempo
{
    Slow,
    Medium,
    Fast,
    Uptempo
}

public enum MusicMood
{
    Orchestral,
    Cinematic,
    Ambient,
    Electronic
}

public enum StorageTier
{
    Hot,
    Cold,
    Archive
}

public enum ExportQualityPreset
{
    Draft,
    Standard,
    ProRes,
    Ultra
}

public enum SubtitleFormat
{
    Srt,
    Vtt,
    Ass
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public enum ScriptDocumentFormat
{
    Fountain,
    Fdx,
    Pdf,
    Txt,
    Docx
}

public static class MediaEngineEnumExtensions
{
    public static AspectRatio ParseAspectRatio(string? s, AspectRatio defaultValue = AspectRatio.Ratio1x1)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return s.Trim() switch
            {
                "16:9" => AspectRatio.Ratio16x9,
                "9:16" => AspectRatio.Ratio9x16,
                "1:1" => AspectRatio.Ratio1x1,
                "4:3" => AspectRatio.Ratio4x3,
                "21:9" => AspectRatio.Ratio21x9,
                _ => Enum.TryParse<AspectRatio>(s, true, out var r) ? r : defaultValue
            };
        }

    public static CameraLens ParseCameraLens(string? s, CameraLens defaultValue = CameraLens.Lens35mm)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            if (s.Contains("24mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens24mm;
            if (s.Contains("35mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens35mm;
            if (s.Contains("50mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens50mm;
            if (s.Contains("85mm", StringComparison.OrdinalIgnoreCase)) return CameraLens.Lens85mm;
            return Enum.TryParse<CameraLens>(s, true, out var l) ? l : defaultValue;
        }

    public static CameraMovementKind ParseCameraMovementKind(string? s, CameraMovementKind defaultValue = CameraMovementKind.TripodHold)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant();
            if (lower.Contains("dolly") || lower.Contains("push")) return CameraMovementKind.DollyPush;
            if (lower.Contains("pan left") || lower.Contains("pan_left")) return CameraMovementKind.PanLeft;
            if (lower.Contains("pan right") || lower.Contains("pan_right")) return CameraMovementKind.PanRight;
            if (lower.Contains("tilt") || lower.Contains("tilt_up")) return CameraMovementKind.TiltUp;
            if (lower.Contains("tripod") || lower.Contains("hold") || lower.Contains("static")) return CameraMovementKind.TripodHold;
            return Enum.TryParse<CameraMovementKind>(s, true, out var m) ? m : defaultValue;
        }

    public static ExportQualityPreset ParseExportQualityPreset(string? s, ExportQualityPreset defaultValue = ExportQualityPreset.Standard)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<ExportQualityPreset>(s, true, out var eq) ? eq : defaultValue;
        }

    public static MusicMood ParseMusicMood(string? s, MusicMood defaultValue = MusicMood.Cinematic)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<MusicMood>(s, true, out var m) ? m : defaultValue;
        }

    public static MusicTempo ParseMusicTempo(string? s, MusicTempo defaultValue = MusicTempo.Medium)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<MusicTempo>(s, true, out var t) ? t : defaultValue;
        }

    public static NotificationSeverity ParseNotificationSeverity(string? s, NotificationSeverity defaultValue = NotificationSeverity.Info)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<NotificationSeverity>(s, true, out var ns) ? ns : defaultValue;
        }

    public static PacingMood ParsePacingMood(string? s, PacingMood defaultValue = PacingMood.Moderate)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<PacingMood>(s, true, out var m) ? m : defaultValue;
        }

    public static ScriptDocumentFormat ParseScriptDocumentFormat(string? s, ScriptDocumentFormat defaultValue = ScriptDocumentFormat.Fountain)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var ext = s.TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "fountain" or "spmd" => ScriptDocumentFormat.Fountain,
                "fdx" => ScriptDocumentFormat.Fdx,
                "pdf" => ScriptDocumentFormat.Pdf,
                "txt" => ScriptDocumentFormat.Txt,
                "docx" or "doc" => ScriptDocumentFormat.Docx,
                _ => Enum.TryParse<ScriptDocumentFormat>(ext, true, out var sdf) ? sdf : defaultValue
            };
        }

    public static StorageTier ParseStorageTier(string? s, StorageTier defaultValue = StorageTier.Hot)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<StorageTier>(s, true, out var st) ? st : defaultValue;
        }

    public static SubtitleFormat ParseSubtitleFormat(string? s, SubtitleFormat defaultValue = SubtitleFormat.Srt)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            return Enum.TryParse<SubtitleFormat>(s, true, out var sf) ? sf : defaultValue;
        }

    public static VideoResolution ParseVideoResolution(string? s, VideoResolution defaultValue = VideoResolution.Res1080p)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            var lower = s.ToLowerInvariant().Trim();
            return lower switch
            {
                "720p" => VideoResolution.Res720p,
                "1080p" => VideoResolution.Res1080p,
                "4k" or "2160p" => VideoResolution.Res4k,
                _ => Enum.TryParse<VideoResolution>(s, true, out var r) ? r : defaultValue
            };
        }

    public static string ToApiString(this AspectRatio ratio) => ratio switch
        {
            AspectRatio.Ratio16x9 => "16:9",
            AspectRatio.Ratio9x16 => "9:16",
            AspectRatio.Ratio1x1 => "1:1",
            AspectRatio.Ratio4x3 => "4:3",
            AspectRatio.Ratio21x9 => "21:9",
            _ => "1:1"
        };

    public static string ToApiString(this CameraLens lens) => lens switch
        {
            CameraLens.Lens24mm => "24mm",
            CameraLens.Lens35mm => "35mm",
            CameraLens.Lens50mm => "50mm",
            CameraLens.Lens85mm => "85mm",
            _ => "35mm"
        };

    public static string ToApiString(this CameraMovementKind move) => move switch
        {
            CameraMovementKind.DollyPush => "dolly_push",
            CameraMovementKind.TripodHold => "tripod_hold",
            CameraMovementKind.PanLeft => "pan_left",
            CameraMovementKind.PanRight => "pan_right",
            CameraMovementKind.TiltUp => "tilt_up",
            _ => "tripod_hold"
        };

    public static string ToApiString(this VideoResolution res) => res switch
        {
            VideoResolution.Res720p => "720p",
            VideoResolution.Res1080p => "1080p",
            VideoResolution.Res4k => "4k",
            _ => "1080p"
        };

    public static string ToApiString(this PacingMood mood) => mood.ToString().ToLowerInvariant();

    public static string ToApiString(this MusicTempo tempo) => tempo.ToString().ToLowerInvariant();

    public static string ToApiString(this MusicMood mood) => mood.ToString().ToLowerInvariant();

    public static string ToApiString(this StorageTier tier) => tier.ToString().ToLowerInvariant();

    public static string ToApiString(this ExportQualityPreset preset) => preset.ToString().ToLowerInvariant();

    public static string ToApiString(this SubtitleFormat fmt) => fmt.ToString().ToLowerInvariant();

    public static string ToApiString(this NotificationSeverity severity) => severity.ToString().ToLowerInvariant();

    public static string ToApiString(this ScriptDocumentFormat fmt) => fmt.ToString().ToLowerInvariant();

}
