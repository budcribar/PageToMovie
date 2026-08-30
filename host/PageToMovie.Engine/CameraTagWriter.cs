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

    /// <summary>The one framing that is always safe: no invented lens, move, or second body.</summary>
    public static string MediumHold() => MediumHoldFraming;

    /// <summary>
    /// Copy the previous Camera, strip DoF, refuse invented OTS without a second body,
    /// and turn a stacked push-in into a hold.
    /// </summary>
    public static string ReusePrevious(string previousCamera, int onScreenCastCount, int step = 1)
    {
        // The previous tag was written by this class, so it is already Optics-free.
        var t = CollapseWs(previousCamera);
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
        int step = 0)
    {
        if (!string.IsNullOrWhiteSpace(previousCamera))
            return ReusePrevious(previousCamera, onScreenCastCount, step);
        return MediumHold();
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
        camera = CollapseWs(string.Join(", ", parts));
        return !string.IsNullOrWhiteSpace(camera);
    }

    /// <summary>
    /// Action is bodies / eyeline / blocking, so camera orders come out of it — but only where
    /// they stand as their own clause. A shot size welded into a sentence ("steps into a close-up
    /// shot of the letter") is load-bearing grammar: cutting it leaves "steps into a of the
    /// letter", and a beat the model cannot read is worse than a framing word it reads twice.
    /// </summary>
    public static string StripFromAction(string? action) =>
        ProseClauses.DropClausesOnlyMatching(action, CameraOrderRegex, CameraJoiningWords);

    /// <summary>Words that only ever join camera orders together, so they leave with them.</summary>
    private static readonly string[] CameraJoiningWords = ["camera", "shot"];

    /// <summary>
    /// True when framing prose reaches into what Optics owns — aperture, depth of field, bokeh,
    /// focus. Only asked, never cut: deciding how far such a phrase reaches is guesswork, and the
    /// directive carries scale / lens / move as fields we can compose from instead.
    /// </summary>
    public static bool NamesApertureOrDepth(string? framing)
    {
        if (string.IsNullOrWhiteSpace(framing))
            return false;
        if (OpticsPhrases.Any(phrase => framing.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return true;
        return FStopRegex.IsMatch(framing);
    }

    private static readonly string[] OpticsPhrases =
        ["depth of field", "bokeh", "deep focus", "shallow focus", "aperture", "stopped down"];

    /// <summary>"f/1.8" — a literal shape, matched only to answer yes or no.</summary>
    private static readonly Regex FStopRegex = new(
        @"\bf\s*/\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

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

    /// <summary>
    /// The classifier's prose framing when it stays in its lane, otherwise the same directive
    /// rebuilt from its own fields. Prose that names an f-stop is not trimmed back into shape —
    /// scale, lens and move are already structured, so there is nothing to salvage by cutting.
    /// </summary>
    private static string? FramingFromDirective(CameraDirective row)
    {
        var framing = (row.FramingPrompt ?? "").Trim();
        if (framing.Length > 0 && !NamesApertureOrDepth(framing))
            return CollapseWs(framing);
        return ComposeFromFields(row);
    }

    /// <summary>Scale, lens, move — the directive's structured fields, in prompt order.</summary>
    public static string? ComposeFromFields(CameraDirective row)
    {
        var parts = new[]
        {
            row.ShotScale.ToFramingPhrase(),
            FieldOrEnum(row.LensSpec, LensPhrase(row.Lens)),
            FieldOrEnum(row.CameraMovement, MovementPhrase(row.MovementKind)),
        }.Where(part => !string.IsNullOrWhiteSpace(part));
        var composed = CollapseWs(string.Join(", ", parts));
        return string.IsNullOrWhiteSpace(composed) ? null : composed;
    }

    /// <summary>
    /// The model's own words for a field, or the enum the same reply parsed to when those words
    /// wandered into Optics. "85mm f/1.4 portrait lens" becomes "85mm lens" — from the parsed
    /// lens, not by cutting the aperture out of the sentence.
    /// </summary>
    private static string FieldOrEnum(string? value, string fromEnum) =>
        NamesApertureOrDepth(value) ? fromEnum : (value ?? "").Trim();

    private static string LensPhrase(CameraLens lens) => lens switch
    {
        CameraLens.Lens24mm => "24mm lens",
        CameraLens.Lens50mm => "50mm lens",
        CameraLens.Lens85mm => "85mm lens",
        _ => "35mm lens",
    };

    private static string MovementPhrase(CameraMovementKind kind) => kind switch
    {
        CameraMovementKind.DollyPush => "slow dolly push-in",
        CameraMovementKind.PanLeft => "slow pan left",
        CameraMovementKind.PanRight => "slow pan right",
        CameraMovementKind.TiltUp => "slow tilt up",
        _ => "locked tripod hold",
    };

    private static string CollapseWs(string text)
    {
        var t = CommonRegex.WhitespaceCollapse.Replace(text, " ");
        return t.Trim(' ', ',', ';', '-', '.');
    }
}
