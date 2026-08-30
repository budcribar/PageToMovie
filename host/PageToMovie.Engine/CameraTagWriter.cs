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

    /// <summary>
    /// Action is bodies / eyeline / blocking, so camera orders come out of it — but only where
    /// they stand as their own clause. A shot size welded into a sentence ("steps into a close-up
    /// shot of the letter") is load-bearing grammar: cutting it leaves "steps into a of the
    /// letter", and a beat the model cannot read is worse than a framing word it reads twice.
    /// </summary>
    public static string StripFromAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "";
        var kept = SplitClauses(action)
            .Where(clause => !IsOnlyCameraOrders(clause.Text))
            .Select(clause => clause.Text.Trim() + clause.Separator);
        return TidyProse(string.Concat(kept));
    }

    /// <summary>True when the clause says nothing but camera orders and the words joining them.</summary>
    private static bool IsOnlyCameraOrders(string clause)
    {
        if (!CameraOrderRegex.IsMatch(clause))
            return false;
        var residue = CameraOrderRegex.Replace(clause, " ");
        return residue
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .All(w => JoiningWords.Contains(w.Trim(WordTrim), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drop f-stop / DoF / bokeh so Camera does not compete with Optics. Whole clauses go, so a
    /// trailing "with the lantern in frame" leaves with its depth-of-field clause instead of
    /// stranding "frame" behind a comma.
    /// </summary>
    public static string SanitizeCameraProse(string? framing)
    {
        if (string.IsNullOrWhiteSpace(framing))
            return "";
        // f-stops carry their own period ("f/1.8"), so they go before anything splits on one.
        var text = CommonRegex.Replace(
            framing, @"\bf\s*/\s*\d+(?:\.\d+)?\b", "", RegexOptions.IgnoreCase);
        var kept = text
            .Split(CameraClauseSeparators, StringSplitOptions.TrimEntries)
            .Where(clause => !OpticsClauseRegex.IsMatch(clause))
            .Select(clause => clause.Trim(' ', ',', ';', '-'))
            .Where(clause => clause.Length > 0);
        return CollapseWs(string.Join(", ", kept));
    }

    private static readonly char[] CameraClauseSeparators = [',', ';'];

    /// <summary>Aperture / depth language: Optics writes it, Camera must not.</summary>
    private static readonly Regex OpticsClauseRegex = new(
        @"\b(?:depth\s+of\s+field|bokeh|(?:deep|shallow)\s+focus)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly char[] WordSeparators = [' ', '	'];
    private static readonly char[] WordTrim = ['.', ',', ';', ':', '-', '(', ')'];

    /// <summary>Articles and prepositions that carry no blocking on their own.</summary>
    private static readonly string[] JoiningWords =
        ["a", "an", "the", "and", "or", "with", "in", "on", "at", "of", "to", "into", "from",
         "is", "are", "was", "were", "then", "camera", "shot"];

    private readonly record struct Clause(string Text, string Separator);

    /// <summary>Split on sentence and clause ends, keeping each separator with its clause.</summary>
    private static List<Clause> SplitClauses(string text)
    {
        var clauses = new List<Clause>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or ',' or ';' or '!' or '?'))
                continue;
            var stop = i;
            while (stop + 1 < text.Length && text[stop + 1] is '.' or ',' or ';' or '!' or '?')
                stop++;
            clauses.Add(new Clause(text[start..i], text[i..(stop + 1)] + " "));
            start = stop + 1;
            i = stop;
        }
        if (start < text.Length)
            clauses.Add(new Clause(text[start..], ""));
        return clauses;
    }

    /// <summary>Close the gaps a dropped clause leaves: doubled punctuation and loose spacing.</summary>
    private static string TidyProse(string text)
    {
        var t = CommonRegex.WhitespaceCollapse.Replace(text, " ");
        t = CommonRegex.Replace(t, @"\s*([,;])(?:\s*[,;])+", "$1");
        t = CommonRegex.Replace(t, @"\s+([,;.])", "$1");
        t = CommonRegex.DotCollapse.Replace(t, ".");
        return t.Trim(' ', ',', ';', '.', '-', ':');
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
