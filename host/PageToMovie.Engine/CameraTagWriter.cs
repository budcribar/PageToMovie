using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Single writer for the clip <c>&lt;Camera&gt;</c> tag.
/// <see cref="CameraDirectorClassifier"/> is the primary author; this helper applies that
/// row, or the deterministic fallback (lift from action → reuse previous → medium hold),
/// and keeps lens / DoF / shot-size out of <c>&lt;Action&gt;</c>.
/// Optics owns f-stop; Camera prose must not carry aperture or depth-of-field language.
/// </summary>
public static class CameraTagWriter
{
    public const string MediumHoldFraming = "Medium shot, 35mm lens, hold";

    /// <summary>
    /// Film-grammar camera orders (shot size, lens, move, OTS / back-to-camera).
    /// Body blocking ("faces the window") is not a match.
    /// </summary>
    private static readonly Regex CameraOrderRegex = new(
        """
        (?:
          (?:extreme\s+)?(?:wide|long|full|medium(?:\s+wide|\s+close(?:-?up)?)?|close[- ]up)\s+(?:shot|two[- ]shot|three[- ]shot)
          |
          (?:medium|wide|tight|establishing)\s+two[- ]shot
          |
          (?:two|three)[- ]shot
          |
          establishing(?:\s+wide)?\s+shot
          |
          over[- ]the[- ]shoulder(?:\s+shot)?
          |
          camera\s+behind
          |
          (?:from\s+)?behind\s+(?:the\s+)?camera
          |
          back\s+to\s+(?:the\s+)?camera
          |
          (?:facing|looks?\s+into)\s+(?:the\s+)?(?:lens|camera)
          |
          \d{2,3}\s*mm(?:\s+(?:prime\s+|portrait\s+|macro\s+|anamorphic\s+)?lens)?
          |
          f\s*/\s*\d+(?:\.\d+)?
          |
          (?:shallow\s+)?depth\s+of\s+field
          |
          creamy(?:\s+soft)?\s+bokeh
          |
          (?:deep|shallow)\s+focus
          |
          (?:slow\s+)?(?:push[- ]in|pull[- ](?:out|back))(?:\s+as\s+\S+(?:\s+\S+){0,4}\s+speaks)?
          |
          (?:slow\s+)?(?:dolly(?:\s+(?:in|out|push(?:-in)?)?)?|tracking(?:\s+shot)?)
          |
          (?:low|high)[- ]angle(?:\s+shot)?
          |
          (?:locked\s+(?:tripod|off)|tripod\s+hold|static\s+(?:locked\s+)?(?:camera|hold))
          |
          (?:macro|portrait|anamorphic)\s+lens
          |
          \b(?:MCU|ECU|EWS|OTS)\b
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex OtsRegex = new(
        @"\b(?:OTS|over[- ]the[- ]shoulder)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex PushInRegex = new(
        @"\b(?:slow\s+)?push[- ]in\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    /// <summary>
    /// Choose the Camera tag body. Classifier row wins. Otherwise: lift named camera
    /// from action/blocking, else reuse/vary the previous same-speaker Camera, else
    /// one medium hold when the clip has speech.
    /// </summary>
    public static string? Resolve(
        CameraDirective? classifierRow,
        string? actionAndBlocking,
        string? previousCameraTag,
        bool sameSpeakerRun,
        bool hasSpeech,
        int onScreenCastCount)
    {
        if (classifierRow is not null)
        {
            var fromClassifier = FramingFromDirective(classifierRow);
            if (!string.IsNullOrWhiteSpace(fromClassifier))
                return fromClassifier;
        }

        if (TryLiftFromAction(actionAndBlocking, out var lifted))
            return lifted;

        if (sameSpeakerRun && !string.IsNullOrWhiteSpace(previousCameraTag))
            return ReusePrevious(previousCameraTag, onScreenCastCount);

        if (hasSpeech)
            return MediumHold();

        return null;
    }

    public static string MediumHold(string? speakerDisplay = null)
    {
        _ = speakerDisplay;
        return MediumHoldFraming;
    }

    /// <summary>
    /// Copy the previous Camera, strip DoF, refuse invented OTS without a second body,
    /// and turn a stacked push-in into a hold.
    /// </summary>
    public static string ReusePrevious(string previousCamera, int onScreenCastCount, int step = 1)
    {
        var t = SanitizeCameraProse(previousCamera);
        if (onScreenCastCount < 2 && OtsRegex.IsMatch(t))
            return MediumHold();
        if (step > 0 && PushInRegex.IsMatch(t))
            t = PushInRegex.Replace(t, "hold");
        t = CollapseWs(t);
        return string.IsNullOrWhiteSpace(t) ? MediumHold() : t;
    }

    /// <summary>Medium hold, or reuse/vary <paramref name="previousCamera"/> — never ECU / macro / DoF / invented OTS.</summary>
    public static string FallbackFraming(
        string? previousCamera,
        int onScreenCastCount,
        string? speakerDisplay = null,
        int step = 0)
    {
        if (!string.IsNullOrWhiteSpace(previousCamera))
            return ReusePrevious(previousCamera, onScreenCastCount, step);
        return MediumHold(speakerDisplay);
    }

    public static bool HasCameraLanguage(string? action) =>
        !string.IsNullOrWhiteSpace(action) && CameraOrderRegex.IsMatch(action);

    public static bool TryLiftFromAction(string? action, out string camera)
    {
        camera = "";
        if (string.IsNullOrWhiteSpace(action))
            return false;
        var matches = CameraOrderRegex.Matches(action);
        if (matches.Count == 0)
            return false;
        var parts = matches
            .Select(m => m.Value.Trim(' ', ',', ';', '.'))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        camera = SanitizeCameraProse(string.Join(", ", parts));
        return !string.IsNullOrWhiteSpace(camera);
    }

    /// <summary>Action is bodies / eyeline / blocking — drop lens, DoF, and shot-size camera orders.</summary>
    public static string StripFromAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "";
        var t = CameraOrderRegex.Replace(action, " ");
        t = CommonRegex.WhitespaceCollapse.Replace(t, " ");
        t = CommonRegex.Replace(t, @"\s*([,;])(?:\s*[,;])+", "$1");
        t = CommonRegex.Replace(t, @"\s+\.", ".");
        t = CommonRegex.DotCollapse.Replace(t, ".");
        return t.Trim(' ', ',', ';', '.', '-', ':');
    }

    /// <summary>Drop f-stop / DoF / bokeh so Camera does not compete with Optics.</summary>
    public static string SanitizeCameraProse(string? framing)
    {
        if (string.IsNullOrWhiteSpace(framing))
            return "";
        var t = framing.Trim();
        t = CommonRegex.Replace(t, @"\bf\s*/\s*\d+(?:\.\d+)?\b", "", RegexOptions.IgnoreCase);
        t = CommonRegex.Replace(
            t,
            @"(?:,|;|\s+)?(?:with\s+)?(?:a\s+)?(?:shallow\s+)?depth\s+of\s+field\b(?:\s+\w+){0,4}",
            "",
            RegexOptions.IgnoreCase);
        t = CommonRegex.Replace(t, @"(?:,|;|\s+)?(?:creamy\s+)?(?:soft\s+)?bokeh\b", "", RegexOptions.IgnoreCase);
        t = CommonRegex.Replace(t, @"(?:,|;|\s+)?(?:deep|shallow)\s+focus\b", "", RegexOptions.IgnoreCase);
        return CollapseWs(t);
    }

    public static string? ReadCameraTag(string? visualPrompt)
    {
        foreach (var section in ClipPromptSections.Parse(visualPrompt))
        {
            if (section.Field == ClipPromptField.Camera && !string.IsNullOrWhiteSpace(section.Value))
                return section.Value.Trim();
        }
        return null;
    }

    public static string? ReadActionTag(string? visualPrompt)
    {
        foreach (var section in ClipPromptSections.Parse(visualPrompt))
        {
            if (section.Field == ClipPromptField.Action && !string.IsNullOrWhiteSpace(section.Value))
                return section.Value.Trim();
        }
        return null;
    }

    public static string? ReadOpticsTag(string? visualPrompt)
    {
        foreach (var section in ClipPromptSections.Parse(visualPrompt))
        {
            if (section.Field == ClipPromptField.Optics && !string.IsNullOrWhiteSpace(section.Value))
                return section.Value.Trim();
        }
        return null;
    }

    private static string? FramingFromDirective(CameraDirective row)
    {
        var framing = SanitizeCameraProse(row.FramingPrompt);
        if (!string.IsNullOrWhiteSpace(framing))
            return framing;
        var fallback = SanitizeCameraProse($"{row.LensSpec}, {row.CameraMovement}".Trim(' ', ','));
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static string CollapseWs(string text)
    {
        var t = CommonRegex.WhitespaceCollapse.Replace(text, " ");
        return t.Trim(' ', ',', ';', '-', '.');
    }
}
