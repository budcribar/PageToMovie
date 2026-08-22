using System.Text.RegularExpressions;

namespace PageToMovie.Fountain;

/// <summary>
/// Shared Fountain lexical helpers — the pure, line-level text rules the parser and the Stage‑1
/// adaptation scans both need. Consolidated here so Engine and Adaptation consume one canonical
/// implementation instead of each re-scanning Fountain text with hand-rolled copies.
/// Everything here is pure <see cref="System.Text"/>: no state, no I/O, no other product deps.
/// Spec: https://fountain.io/syntax/
/// </summary>
public static class FountainLexer
{
    // INT / EXT / EST / INT./EXT / INT/EXT / I/E / I./E followed by . or space
    // (I./E is used in the nyousefi Fountain reference fixtures)
    private static readonly Regex SceneHeadingStartRegex = new(@"^(INT\./EXT|INT/EXT|I\./E|I/E|INT\.?|EXT\.?|EST\.?)(\s|\.|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex TransitionEnd = new(@"TO:$", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    /// <summary>
    /// Common standalone transitions that do not end in TO: (FADE IN / FADE OUT / …).
    /// </summary>
    private static readonly Regex StandaloneFadeTransition = new(@"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO\s+BLACK|BLACK\s+OUT)[\.:]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex VoExtensionRegex = new(@"\(?\s*V\s*\.?\s*O\s*\.?\s*\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    /// <summary>True when a line starts with a Fountain scene-heading prefix (INT./EXT./EST./…).</summary>
    public static bool IsSceneHeadingStart(string? line) =>
        !string.IsNullOrEmpty(line) && SceneHeadingStartRegex.IsMatch(line);

    /// <summary>
    /// Strip Fountain/Markdown-style emphasis for plain-text import
    /// (*italic*, **bold**, ***both***, _underline_), honoring backslash escapes.
    /// Matches Fountain: spaces around markers matter (no emphasis when open is followed
    /// by whitespace or close is preceded by whitespace); emphasis does not span lines
    /// (caller processes one line at a time for most elements).
    /// </summary>
    public static string StripEmphasis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Protect escapes (Markdown convention)
        text = text.Replace("\\*", "\u0001").Replace("\\_", "\u0002");
        // Content must start and end with non-whitespace (Markdown/Fountain spacing rules).
        // Single non-space char is allowed: *a*, **b**, etc.
        // ***bold italic*** then **bold** then *italic* then _underline_
        text = FountainRegex.Replace(text, @"\*\*\*(\S(?:[^*]*\S)?)\*\*\*", "$1");
        text = FountainRegex.Replace(text, @"\*\*(\S(?:[^*]*\S)?)\*\*", "$1");
        text = FountainRegex.Replace(text, @"\*(\S(?:[^*]*\S)?)\*", "$1");
        text = FountainRegex.Replace(text, @"_(\S(?:[^_]*\S)?)_", "$1");
        return text.Replace("\u0001", "*").Replace("\u0002", "_");
    }

    /// <summary>Alias for <see cref="StripEmphasis"/> — Fountain has no separate unescape pass.</summary>
    public static string UnescapeFountain(string text) => StripEmphasis(text);

    /// <summary>
    /// Map curly quotes/apostrophes/dashes to ASCII so CONT'D and MARLEY'S parse reliably.
    /// </summary>
    public static string NormalizeTypographicPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return text
            .Replace('‘', '\'') // ‘
            .Replace('’', '\'') // ’
            .Replace('“', '"')  // “
            .Replace('”', '"')  // ”
            .Replace('–', '-')  // –
            .Replace('—', '-')  // —
            .Replace(' ', ' '); // nbsp
    }

    /// <summary>True when a line has at least one letter and every letter is upper-case.</summary>
    public static bool IsAllCapsLine(string s)
    {
        var hasLetter = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                if (!char.IsUpper(ch)) return false;
            }
        }
        return hasLetter;
    }

    /// <summary>
    /// True for a line that should be a Transition element (not Action / not a scene).
    /// Public so importers can ignore transition-only noise without inventing scenes.
    /// </summary>
    public static bool IsStandaloneTransitionLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // Strip emphasis so **FADE IN:** still matches
        var stripped = StripEmphasis(line).TrimStart();
        var core = stripped.TrimEnd();
        if (core.Length == 0) return false;
        if (!IsAllCapsLine(core)) return false;

        // FADE IN / FADE OUT / FADE TO BLACK (optional trailing period)
        if (StandaloneFadeTransition.IsMatch(core))
            return true;

        // Classic Fountain TO: — must end with TO: (trailing spaces after colon → Action)
        return TransitionEnd.IsMatch(stripped.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// True when a trimmed line is an all-caps Fountain character cue (name, optional
    /// parenthetical extension). Scene-heading-shaped lines (INT/EXT) are never cues.
    /// </summary>
    public static bool IsCharacterLine(string trimmed)
    {
        trimmed = trimmed.Trim();
        if (trimmed.StartsWith('@')) return true;
        // Spec: when in doubt, Action. Never treat INT/EXT scene-heading-shaped lines as Character
        // (e.g. two headings stacked without a blank line between them).
        if (SceneHeadingStartRegex.IsMatch(trimmed)) return false;
        var core = trimmed.TrimEnd('^', ' ', '\t');
        var namePart = core.Split('(')[0].Trim();
        if (namePart.Length < 1) return false;
        if (!namePart.Any(char.IsLetter)) return false; // "23" invalid; "R2D2" ok
        // Allow multi-word ALL CAPS including apostrophes / ampersands
        // (MARLEY'S GHOST, SCROOGE & MARLEY'S, GHOST OF CHRISTMAS PAST)
        return namePart.All(c =>
            !char.IsLetter(c) || char.IsUpper(c));
    }

    /// <summary>Split a character cue line into its name and its parenthetical extension (e.g. (V.O.)).</summary>
    public static (string Name, string? Ext) SplitCharacter(string line)
    {
        line = line.Trim().TrimEnd('^').Trim();
        var open = line.IndexOf('(');
        if (open > 0 && line.EndsWith(')'))
            return (line[..open].Trim(), line[open..].Trim());
        // Extension with spaces: MOM (O. S.) already handled if ends with )
        if (open > 0)
        {
            var close = line.LastIndexOf(')');
            if (close > open)
                return (line[..open].Trim(), line[open..(close + 1)].Trim());
        }
        return (line, null);
    }

    /// <summary>True when the line before index <paramref name="i"/> is blank (or start of file).</summary>
    public static bool PrevBlank(IReadOnlyList<string> lines, int i)
    {
        if (i <= 0) return true;
        return string.IsNullOrWhiteSpace(lines[i - 1]);
    }

    /// <summary>True when the line after index <paramref name="i"/> is blank (or end of file).</summary>
    public static bool NextBlank(IReadOnlyList<string> lines, int i)
    {
        if (i + 1 >= lines.Count) return true;
        return string.IsNullOrWhiteSpace(lines[i + 1]);
    }

    /// <summary>
    /// Fountain two-space rule: a whitespace-only line that still contains two+ spaces is a line
    /// break that CONTINUES a dialogue block, whereas a truly-empty line ends it.
    /// </summary>
    public static bool IsTwoSpaceContinue(string raw) =>
        raw is not null && string.IsNullOrWhiteSpace(raw) && raw.Contains("  ", StringComparison.Ordinal);

    /// <summary>
    /// Pure text check: does a character-cue extension say voice-over (V.O.)? Beat-level
    /// off-screen / delivery policy stays in Engine — this only inspects the cue text.
    /// </summary>
    public static bool IsVoiceOverExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;
        if (ext.Contains("V.O", StringComparison.OrdinalIgnoreCase)) return true;
        return VoExtensionRegex.IsMatch(ext);
    }

    /// <summary>
    /// Remove standalone Fountain page-break markers (a line of three or more <c>=</c>,
    /// optionally with a page number, e.g. <c>===</c> or <c>===13===</c>). Collapses the
    /// blank runs left behind and guarantees a single trailing newline.
    /// </summary>
    public static string StripFountainPageBreaks(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";

        fountain = FountainRegex.Replace(
            fountain,
            @"(?m)^[ \t]*={3,}[ \t]*(?:\d+[ \t]*=+[ \t]*)?$\r?\n?",
            "");

        fountain = FountainRegex.Replace(fountain, @"\n{3,}", "\n\n");
        var trimmed = fountain.TrimEnd();
        return trimmed.Length == 0 ? "" : trimmed + "\n";
    }
}
