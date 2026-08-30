using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Single writer for film-level <c>PERFORMANCE LOCK</c> and Action eyeline/address.
/// Default address lives on <c>vision_meta.performance_lock</c>. Beat acting is
/// <c>&lt;Performance&gt;</c> only. Action is bodies / blocking — not who they look at.
/// </summary>
public static class PerformanceTagWriter
{
    public const string LockPrefix = "PERFORMANCE LOCK: ";

    /// <summary>
    /// Gaze / address commands. Body blocking ("faces the window") is not a match.
    /// Camera still owns shot size / lens / back-to-camera (PR 312).
    /// </summary>
    private static readonly Regex AddressCommandRegex = new(
        """
        (?:
          face(?:s|ing)?\s+(?:to\s+|the\s+)?house
          |
          look(?:s|ing)?\s+down\s+the\s+lens
          |
          look(?:s|ing)?\s+(?:into|at|toward|towards)\s+(?:the\s+)?(?:lens|camera|viewer|audience)
          |
          gaze[sd]?\s+(?:into|at|toward|towards)\s+(?:the\s+)?(?:lens|camera|viewer|audience|house)
          |
          address(?:es|ing)?\s+(?:the\s+)?(?:viewer|audience|camera|lens)
          |
          eyeline\s+(?:to|toward|towards)\s+\S+
          |
          confessional\s+address
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex PerformanceLockLineRegex = new(
        @"(?im)^\s*PERFORMANCE LOCK(?:\s*\(hard\))?\s*:\s*.+\r?\n?",
        RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex HousePerformanceBulletRegex = new(
        @"(?im)^\s*-\s*Performance:\s*.+\r?\n?",
        RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex ProjectPerformanceRuleRegex = new(
        @"(?im)^\s*-\s*\[performance\]\s*.+\r?\n?",
        RegexOptions.Compiled,
        CommonRegex.Timeout);

    public static string NormalizeLock(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        return ProjectRulesService.NormalizePerformanceRuleText(t);
    }

    public static bool HasAddressLanguage(string? action) =>
        !string.IsNullOrWhiteSpace(action) && AddressCommandRegex.IsMatch(action);

    /// <summary>
    /// Action is bodies / blocking — drop gaze and address so Performance owns who they look at.
    /// </summary>
    public static string StripEyelineFromAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "";
        if (action.Contains($"<{PromptFieldTags.Action}>", StringComparison.OrdinalIgnoreCase))
        {
            return CommonRegex.Replace(
                action,
                $@"(?is)<{PromptFieldTags.Action}>(.*?)</{PromptFieldTags.Action}>",
                m => PromptTags.Wrap(PromptFieldTags.Action, StripEyelineProse(m.Groups[1].Value)));
        }

        return StripEyelineProse(action);
    }

    /// <summary>
    /// Gaze and address leave when they stand as their own clause ("Confessional address."), and
    /// stay when they are inside a sentence that also does something: cutting the phrase out of
    /// "Nick looks into the camera and lifts the lantern" left "Nick and lifts the lantern", and
    /// the beat is worth more than the repetition. Performance still owns the tag.
    /// </summary>
    public static string StripEyelineProse(string? action) =>
        ProseClauses.DropClausesOnlyMatching(action, AddressCommandRegex, AddressJoiningWords);

    /// <summary>Words that only ever join an address command, so they leave with it.</summary>
    private static readonly string[] AddressJoiningWords =
        ["camera", "lens", "viewer", "audience", "house", "eyeline", "confessional", "address"];

    /// <summary>
    /// After house / project rules are appended, keep exactly one PERFORMANCE LOCK
    /// from vision_meta. Do not invent a lock when the project has none.
    /// </summary>
    public static string EnsureSinglePerformanceLock(string? prompt, string? performanceLock)
    {
        var text = prompt ?? "";
        text = PerformanceLockLineRegex.Replace(text, "");
        text = HousePerformanceBulletRegex.Replace(text, "");
        text = ProjectPerformanceRuleRegex.Replace(text, "");
        text = text.Trim();
        var line = NormalizeLock(performanceLock);
        if (string.IsNullOrWhiteSpace(line))
            return text;

        var style = CommonRegex.Match(
            text,
            @"^STYLE LOCK(?:\s*\(hard\))?:\s*[^\n]+(?:\n+)?",
            RegexOptions.IgnoreCase);
        if (style.Success)
            return text[..style.Length] + line + "\n\n" + text[style.Length..].TrimStart();
        return line + "\n\n" + text;
    }

    public static int CountPerformanceLocks(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return 0;
        return PerformanceLockLineRegex.Matches(prompt).Count;
    }

    public static string? ReadPerformanceTag(string? visualPrompt)
    {
        foreach (var section in ClipPromptSections.Parse(visualPrompt))
        {
            if (section.Field == ClipPromptField.Performance && !string.IsNullOrWhiteSpace(section.Value))
                return section.Value.Trim();
        }
        return null;
    }

    public static string? ReadActionTag(string? visualPrompt) =>
        CameraTagWriter.ReadActionTag(visualPrompt);
}
