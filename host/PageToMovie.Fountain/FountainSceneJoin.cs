using System.Text;
using System.Text.RegularExpressions;

namespace PageToMovie.Fountain;

/// <summary>
/// Between-scene join on the Film list — the Fountain transition immediately before the next
/// scene heading. Same SSoT Cut maps to playable joins (<c>CutTransitionMap.FromFountain</c>).
/// Empty / omitted line = hard cut. Wipe is out (reads as Cut).
/// </summary>
/// <remarks>
/// Optional chapter/scene card lives in a Fountain note on the same join:
/// <c>[[CARD: Chapter 1]]</c> immediately after the transition (or alone before the heading
/// when the join is a hard cut). Cut already has finish-card metadata; this note is the
/// Fountain-adjacent home it can read. No second transition store.
/// </remarks>
public enum FountainSceneJoinKind
{
    Cut = 0,
    Dissolve = 1,
    Dip = 2,
    FadeWhite = 3,
    CutToBlack = 4,
}

public readonly record struct FountainIncomingJoin(
    int IncomingHeadingIndex,
    FountainSceneJoinKind Kind,
    string? FountainLine,
    string? CardText);

public static class FountainSceneJoin
{
    public const string CardNotePrefix = "CARD:";
    public const string FadeOutLine = "> FADE OUT.";
    public const string DissolveLine = "DISSOLVE TO:";
    public const string FadeWhiteLine = "> FADE TO WHITE.";
    public const string CutToBlackLine = "> CUT TO BLACK";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex SceneNumberSuffix = new(@"#\s*(\d+)\s*#\s*$", RegexOptions.Compiled, RegexTimeout);

    public static string WireName(FountainSceneJoinKind kind) => kind switch
    {
        FountainSceneJoinKind.Dissolve => "dissolve",
        FountainSceneJoinKind.Dip => "dip",
        FountainSceneJoinKind.FadeWhite => "fadewhite",
        FountainSceneJoinKind.CutToBlack => "cuttoblack",
        _ => "cut",
    };

    public static string DisplayName(FountainSceneJoinKind kind) => kind switch
    {
        FountainSceneJoinKind.Dissolve => "Dissolve",
        FountainSceneJoinKind.Dip => "Dip to black",
        FountainSceneJoinKind.FadeWhite => "Fade to white",
        FountainSceneJoinKind.CutToBlack => "Cut to black",
        _ => "Cut",
    };

    public static FountainSceneJoinKind ParseKind(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return FountainSceneJoinKind.Cut;
        return wire.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "") switch
        {
            "dissolve" or "dissolveto" => FountainSceneJoinKind.Dissolve,
            "dip" or "diptoblack" or "fadeout" or "fadetoblack" or "blackout" => FountainSceneJoinKind.Dip,
            "fadewhite" or "fadetowhite" => FountainSceneJoinKind.FadeWhite,
            "cuttoblack" => FountainSceneJoinKind.CutToBlack,
            _ => FountainSceneJoinKind.Cut,
        };
    }

    /// <summary>
    /// Fountain line → Film/Cut join. Smash / match / jump / wipe display as Cut.
    /// FADE OUT. / FADE TO BLACK / BLACKOUT = Dip to black (one write spelling: <see cref="FadeOutLine"/>).
    /// </summary>
    public static FountainSceneJoinKind FromFountain(string? line)
    {
        var t = NormalizeLine(line);
        if (t.Length == 0)
            return FountainSceneJoinKind.Cut;
        if (IsMatch(t, @"WIPE"))
            return FountainSceneJoinKind.Cut;
        if (IsMatch(t, @"CUT\s+TO\s+BLACK"))
            return FountainSceneJoinKind.CutToBlack;
        if (IsMatch(t, @"FADE\s+TO\s+WHITE"))
            return FountainSceneJoinKind.FadeWhite;
        if (IsMatch(t, @"FADE\s+IN"))
            return FountainSceneJoinKind.Cut;
        if (IsMatch(t, @"FADE\s+(OUT|TO\s+BLACK)") || IsMatch(t, @"BLACKOUT") || IsMatch(t, @"BLACK\s+OUT"))
            return FountainSceneJoinKind.Dip;
        if (IsMatch(t, @"DISSOLVE"))
            return FountainSceneJoinKind.Dissolve;
        if (IsMatch(t, @"\b(CUT|SMASH|MATCH|JUMP)\b"))
            return FountainSceneJoinKind.Cut;
        return FountainSceneJoinKind.Cut;
    }

    public static string ToFountainLine(FountainSceneJoinKind kind) => kind switch
    {
        FountainSceneJoinKind.Dissolve => DissolveLine,
        FountainSceneJoinKind.Dip => FadeOutLine,
        FountainSceneJoinKind.FadeWhite => FadeWhiteLine,
        FountainSceneJoinKind.CutToBlack => CutToBlackLine,
        _ => "",
    };

    public static string ToCardNote(string? card)
    {
        var text = (card ?? "").Trim();
        return text.Length == 0 ? "" : $"[[{CardNotePrefix} {text}]]";
    }

    public static bool TryReadCardNote(string? line, out string text)
    {
        text = "";
        var t = (line ?? "").Trim();
        if (!t.StartsWith("[[", StringComparison.Ordinal) || !t.EndsWith("]]", StringComparison.Ordinal) || t.Length < 5)
            return false;
        var inner = t[2..^2].Trim();
        if (!inner.StartsWith(CardNotePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        text = inner[CardNotePrefix.Length..].Trim();
        return true;
    }

    public static IReadOnlyList<FountainIncomingJoin> ReadIncoming(string? fountain)
    {
        var lines = SplitLines(fountain);
        var headings = FindHeadingIndexes(lines);
        var result = new List<FountainIncomingJoin>();
        for (var h = 1; h < headings.Count; h++)
        {
            var headingIndex = h + 1;
            var sceneKey = ExplicitSceneNumber(lines[headings[h]]) ?? headingIndex;
            var (line, card) = ReadJoinRegion(lines, headings[h - 1] + 1, headings[h]);
            result.Add(new FountainIncomingJoin(
                sceneKey,
                FromFountain(line),
                string.IsNullOrWhiteSpace(line) ? null : line,
                string.IsNullOrWhiteSpace(card) ? null : card));
        }

        return result;
    }

    /// <summary>
    /// Write or omit the join immediately before the incoming scene heading
    /// (<paramref name="incomingHeadingIndex"/> is 1-based heading order, same as Film scene number when they align).
    /// Cut deletes the transition line. Card note is kept or removed independently.
    /// Does not touch FADE IN before the first heading or FADE OUT / THE END after the last.
    /// </summary>
    public static string WriteIncoming(
        string? fountain,
        int incomingHeadingIndex,
        FountainSceneJoinKind kind,
        string? card)
    {
        var lines = SplitLines(fountain).ToList();
        var headings = FindHeadingIndexes(lines);
        var headingOrd = ResolveHeadingOrdinal(lines, headings, incomingHeadingIndex);
        if (headingOrd < 2 || headingOrd > headings.Count)
            throw new ArgumentOutOfRangeException(nameof(incomingHeadingIndex), incomingHeadingIndex,
                "Join is between consecutive scenes — incoming heading must be 2 or later.");

        var headingLine = headings[headingOrd - 1];
        var regionStart = JoinRegionStart(lines, headings[headingOrd - 2] + 1, headingLine);
        lines.RemoveRange(regionStart, headingLine - regionStart);

        var insert = BuildJoinBlock(kind, card);
        lines.InsertRange(regionStart, insert);
        return JoinLines(lines);
    }

    /// <summary>
    /// Force parseable markers on standalone fade / cut-to-black lines that do not end in TO:.
    /// Matches the book-to-fountain "Bare FADE OUT." repair. Leaves TO: lines and body action alone.
    /// </summary>
    public static string ForceTransitionMarkers(string? fountain)
    {
        var lines = SplitLines(fountain);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('>'))
                continue;
            if (!IsJoinTransitionLine(trimmed))
                continue;
            var kind = FromFountain(trimmed);
            if (kind is FountainSceneJoinKind.Dip or FountainSceneJoinKind.FadeWhite or FountainSceneJoinKind.CutToBlack)
            {
                var forced = ToFountainLine(kind);
                if (!string.Equals(trimmed, forced, StringComparison.Ordinal))
                {
                    lines[i] = forced;
                    changed = true;
                }
            }
        }

        return changed ? JoinLines(lines) : (fountain ?? "");
    }

    internal static bool IsJoinTransitionLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var t = StripForcedPrefix(line.Trim());
        if (t.Length == 0)
            return false;
        if (FountainLexer.IsStandaloneTransitionLine(t))
            return true;
        if (!FountainLexer.IsAllCapsLine(t))
            return false;
        var kind = FromFountain(t);
        return kind != FountainSceneJoinKind.Cut || IsMatch(NormalizeLine(t), @"\b(CUT|SMASH|MATCH|JUMP|WIPE)\b");
    }

    private static (string? Line, string? Card) ReadJoinRegion(IReadOnlyList<string> lines, int afterPrevHeading, int headingLine)
    {
        string? transition = null;
        string? card = null;
        for (var i = headingLine - 1; i >= afterPrevHeading; i--)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
                continue;
            if (TryReadCardNote(t, out var cardText))
            {
                card ??= cardText;
                continue;
            }

            if (IsJoinTransitionLine(t))
            {
                transition = StripForcedPrefix(t);
                break;
            }

            break;
        }

        return (transition, card);
    }

    private static int JoinRegionStart(IReadOnlyList<string> lines, int afterPrevHeading, int headingLine)
    {
        var i = headingLine - 1;
        while (i >= afterPrevHeading && string.IsNullOrWhiteSpace(lines[i]))
            i--;
        while (i >= afterPrevHeading)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || IsJoinTransitionLine(t) || TryReadCardNote(t, out _))
            {
                i--;
                continue;
            }

            break;
        }

        return i + 1;
    }

    private static List<string> BuildJoinBlock(FountainSceneJoinKind kind, string? card)
    {
        var block = new List<string> { "" };
        var line = ToFountainLine(kind);
        if (line.Length > 0)
        {
            block.Add(line);
            block.Add("");
        }

        var note = ToCardNote(card);
        if (note.Length > 0)
        {
            block.Add(note);
            block.Add("");
        }

        return block;
    }

    internal static List<int> FindHeadingIndexes(IReadOnlyList<string> lines)
    {
        var idxs = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsHeadingLine(lines, i))
                idxs.Add(i);
        }

        return idxs;
    }

    internal static bool IsHeadingLine(IReadOnlyList<string> lines, int i)
    {
        var trimmed = lines[i].Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.StartsWith('.') && trimmed.Length > 1 && char.IsLetterOrDigit(trimmed[1]))
            return true;

        var prevBlank = FountainLexer.PrevBlank(lines, i);
        var nextBlank = FountainLexer.NextBlank(lines, i);
        if (!prevBlank || !FountainLexer.IsSceneHeadingStart(trimmed))
            return false;
        return nextBlank || NextIsPageTagOrSynopsis(lines, i);
    }

    internal static int ResolveHeadingOrdinal(IReadOnlyList<string> lines, IReadOnlyList<int> headings, int sceneNumber)
    {
        for (var h = 0; h < headings.Count; h++)
        {
            if (ExplicitSceneNumber(lines[headings[h]]) == sceneNumber)
                return h + 1;
        }

        return sceneNumber;
    }

    internal static int? ExplicitSceneNumber(string headingLine)
    {
        var t = headingLine.Trim();
        if (t.StartsWith('.'))
            t = t[1..].Trim();
        var m = SceneNumberSuffix.Match(t);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;
    }

    private static bool NextIsPageTagOrSynopsis(IReadOnlyList<string> lines, int i)
    {
        if (i + 1 >= lines.Count)
            return false;
        var next = lines[i + 1].Trim();
        if (next.Length == 0)
            return false;
        if (next.StartsWith('=') && !next.StartsWith("===", StringComparison.Ordinal))
            return true;
        return next.StartsWith("[[", StringComparison.Ordinal) && next.Contains("page", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripForcedPrefix(string line)
    {
        var t = FountainLexer.StripEmphasis(line).Trim();
        if (t.StartsWith('>') && !t.TrimEnd().EndsWith('<'))
            t = t.TrimStart('>').Trim();
        return t;
    }

    private static string NormalizeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";
        var t = StripForcedPrefix(line).TrimEnd(':').TrimEnd('.').ToUpperInvariant();
        return Regex.Replace(t, @"\s+", " ", RegexOptions.None, RegexTimeout);
    }

    private static bool IsMatch(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    internal static string[] SplitLines(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain))
            return [];
        return fountain.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static string JoinLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i]);
        }

        if (sb.Length > 0 && sb[^1] != '\n')
            sb.Append('\n');
        return sb.ToString();
    }
}
