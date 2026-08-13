using System.Text;
using System.Text.RegularExpressions;

namespace PageToMovie.Fountain;

/// <summary>
/// Fountain 1.1 plain-text screenplay parser.
/// Spec: https://fountain.io/syntax/
/// Line-level lexical rules (emphasis, cue detection, scene-heading / transition / two-space
/// classification, V.O. text checks) live in <see cref="FountainLexer"/> and are shared with the
/// Stage‑1 adaptation scans; this type owns block assembly and the element model.
/// </summary>
public static class FountainParser
{
    public enum ElementType
    {
        SceneHeading,
        Action,
        Character,
        Parenthetical,
        Dialogue,
        Transition,
        Lyric,
        Section,
        Synopsis,
        PageBreak,
        Note,
        Centered,
    }

    public sealed class Element
    {
        public ElementType Type { get; init; }
        public string Text { get; init; } = "";
        /// <summary>
        /// Character extension e.g. (O.S.); Section depth as "#"; dual dialogue "dual";
        /// Scene number if present.
        /// </summary>
        public string? Meta { get; init; }
    }

    public sealed class ParseResult
    {
        public Dictionary<string, string> TitlePage { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Element> Elements { get; } = new();
    }

    /// <summary>Mutable cursor for one body-line classification pass.</summary>
    private sealed class BodyParseState
    {
        public required string[] Lines;
        public required ParseResult Result;
        public int I;
        public bool PendingDual;
        public string Raw = "";
        public string Trimmed = "";
    }

    /// <summary>Accumulates title-page Key: value pairs, including multiline values.</summary>
    private sealed class TitlePageParseState
    {
        public string? CurrentKey;
        public readonly StringBuilder ValueBuf = new();
        public required ParseResult Result;

        public void Flush()
        {
            if (CurrentKey is null) return;
            var v = ValueBuf.ToString().Trim();
            if (v.Length > 0)
            {
                v = FountainLexer.UnescapeFountain(v);
                if (Result.TitlePage.TryGetValue(CurrentKey, out var existing) && existing.Length > 0)
                    Result.TitlePage[CurrentKey] = existing + "\n" + v;
                else
                    Result.TitlePage[CurrentKey] = v;
            }
            CurrentKey = null;
            ValueBuf.Clear();
        }
    }

    // Parser-internal disambiguation for title-page Key: lines ("CUT TO:" is a transition, not a key).
    private static readonly Regex TransitionEnd = new(@"TO:$", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex SceneNumberSuffix = new(@"\s+#([^#]+)#\s*$", RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex TitleKeyLine = new(@"^([A-Za-z][A-Za-z0-9 ]*):\s*(.*)$", RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex BoneyardRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex NoteRegex = new(@"\[\[(.*?)\]\]", RegexOptions.Singleline | RegexOptions.Compiled, FountainRegex.Timeout);

    private static readonly Regex PageTagRegex = new(@"^\[\[\s*pages?\s+\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled, FountainRegex.Timeout);

    public static ParseResult Parse(string text)
    {
        text ??= "";
        // Normalize typographic punctuation so CONT'D / MARLEY'S match ASCII rules
        text = FountainLexer.NormalizeTypographicPunctuation(text);
        // Boneyard /* ... */ may span lines — remove entirely
        text = BoneyardRegex.Replace(text, "\n");

        // Extract and remove notes [[...]] (may span lines; empty line inside needs two spaces per spec)
        var notes = new List<string>();
        text = ExtractNotes(text, notes);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Tabs → 4 spaces (Fountain: tabs converted to four spaces in Action)
        for (var t = 0; t < lines.Length; t++)
            lines[t] = lines[t].Replace("\t", "    ");

        var result = new ParseResult();
        foreach (var n in notes)
        {
            if (n.Length > 0)
                result.Elements.Add(new Element { Type = ElementType.Note, Text = n });
        }

        var i = ParseTitlePage(lines, 0, result);
        // Some Fountain files put dual-dialogue caret on its own line before the second speaker.
        var pendingDual = false;

        while (i < lines.Length)
            ConsumeBodyLine(lines, result, ref i, ref pendingDual);

        return result;
    }

    /// <summary>Strip Fountain/Markdown emphasis. Forwards to <see cref="FountainLexer.StripEmphasis"/>.</summary>
    public static string StripEmphasis(string text) => FountainLexer.StripEmphasis(text);

    /// <summary>Fountain unescape (alias for emphasis stripping). Forwards to <see cref="FountainLexer"/>.</summary>
    public static string UnescapeFountain(string text) => FountainLexer.UnescapeFountain(text);

    /// <summary>
    /// True for a line that should be a Transition element (not Action / not a scene).
    /// Forwards to <see cref="FountainLexer.IsStandaloneTransitionLine"/>.
    /// </summary>
    public static bool IsStandaloneTransitionLine(string? line) =>
        FountainLexer.IsStandaloneTransitionLine(line);

    /// <summary>
    /// Map curly quotes/apostrophes/dashes to ASCII. Forwards to
    /// <see cref="FountainLexer.NormalizeTypographicPunctuation"/>.
    /// </summary>
    public static string NormalizeTypographicPunctuation(string text) =>
        FountainLexer.NormalizeTypographicPunctuation(text);

    private static string ExtractNotes(string text, List<string> notes)
    {
        return NoteRegex.Replace(text, m => NoteCapture(notes, m));
    }

    private static string NoteCapture(List<string> notes, Match m)
    {
        notes.Add(m.Groups[1].Value.Trim());
        return "";
    }

    private static int CountLeadingHashes(string trimmed)
    {
        var depth = 0;
        while (depth < trimmed.Length && trimmed[depth] == '#')
            depth++;
        return depth;
    }

    private static void ConsumeBodyLine(string[] lines, ParseResult result, ref int i, ref bool pendingDual)
    {
        var state = new BodyParseState
        {
            Lines = lines,
            Result = result,
            I = i,
            PendingDual = pendingDual,
            Raw = lines[i],
        };
        // Keep right-trim only for classification; Action may keep leading spaces
        state.Trimmed = state.Raw.TrimEnd().Trim();

        _ = TryConsumeBlankOrDualCaret(state)
            || TryConsumePageBreakSectionSynopsisLyric(state)
            || TryConsumeForcedActionSceneChar(state)
            || TryConsumeCenteredOrForcedTransition(state)
            || TryConsumeAutomaticSceneOrTransition(state)
            || TryConsumeCharacterDialogue(state)
            || ConsumeDefaultAction(state);

        i = state.I;
        pendingDual = state.PendingDual;
    }

    private static bool TryConsumeBlankOrDualCaret(BodyParseState state)
    {
        if (state.Trimmed.Length == 0)
        {
            state.I++;
            return true;
        }

        // Dual dialogue marker alone on a line (non-spec but common) → next Character is dual
        if (state.Trimmed == "^")
        {
            state.PendingDual = true;
            state.I++;
            return true;
        }

        return false;
    }

    private static bool TryConsumePageBreakSectionSynopsisLyric(BodyParseState state)
    {
        var trimmed = state.Trimmed;
        var result = state.Result;

        // Page break: ===
        if (FountainRegex.IsMatch(trimmed, @"^={3,}\s*$"))
        {
            result.Elements.Add(new Element { Type = ElementType.PageBreak, Text = trimmed });
            state.I++;
            return true;
        }

        // Section: # ...
        if (trimmed.StartsWith('#'))
        {
            var depth = CountLeadingHashes(trimmed);
            result.Elements.Add(new Element
            {
                Type = ElementType.Section,
                Text = trimmed.TrimStart('#').Trim(),
                Meta = depth.ToString(),
            });
            state.I++;
            return true;
        }

        // Synopsis: = ...  (not === page break)
        if (trimmed.StartsWith('=') && !trimmed.StartsWith("==="))
        {
            result.Elements.Add(new Element
            {
                Type = ElementType.Synopsis,
                Text = trimmed.TrimStart('=').Trim(),
            });
            state.I++;
            return true;
        }

        // Lyrics: ~...
        if (trimmed.StartsWith('~'))
        {
            result.Elements.Add(new Element
            {
                Type = ElementType.Lyric,
                Text = FountainLexer.UnescapeFountain(trimmed.TrimStart('~').TrimStart()),
            });
            state.I++;
            return true;
        }

        return false;
    }

    private static bool TryConsumeForcedActionSceneChar(BodyParseState state)
    {
        var trimmed = state.Trimmed;
        var result = state.Result;

        // Forced action: !...
        if (trimmed.StartsWith('!'))
        {
            result.Elements.Add(new Element
            {
                Type = ElementType.Action,
                Text = PreserveActionIndent(state.Raw, FountainLexer.UnescapeFountain(trimmed[1..].TrimStart())),
            });
            state.I++;
            return true;
        }

        // Forced scene heading: .ALNUM... (single period, not ellipsis)
        if (trimmed.StartsWith('.') &&
            trimmed.Length > 1 &&
            char.IsLetterOrDigit(trimmed[1]))
        {
            var (heading, sceneNo) = SplitSceneNumber(trimmed[1..].Trim());
            result.Elements.Add(new Element
            {
                Type = ElementType.SceneHeading,
                Text = heading,
                Meta = sceneNo,
            });
            state.I++;
            return true;
        }

        // Forced character: @Name
        if (trimmed.StartsWith('@'))
        {
            var dual = state.PendingDual || trimmed.TrimEnd().EndsWith('^');
            state.PendingDual = false;
            var (name, ext) = FountainLexer.SplitCharacter(trimmed[1..].Trim().TrimEnd('^').Trim());
            result.Elements.Add(new Element
            {
                Type = ElementType.Character,
                Text = name,
                Meta = BuildCharMeta(ext, dual),
            });
            state.I++;
            state.I = ConsumeDialogueBlock(state.Lines, state.I, result);
            return true;
        }

        return false;
    }

    private static bool TryConsumeCenteredOrForcedTransition(BodyParseState state)
    {
        var trimmed = state.Trimmed;
        var result = state.Result;

        // Centered: > text <  (Action, leading spaces not preserved)
        if (IsCentered(trimmed))
        {
            var inner = trimmed.Trim().TrimStart('>').TrimEnd('<').Trim();
            result.Elements.Add(new Element
            {
                Type = ElementType.Centered,
                Text = FountainLexer.UnescapeFountain(inner),
            });
            state.I++;
            return true;
        }

        // Forced transition: >...  (not centered)
        if (trimmed.StartsWith('>') && !IsCentered(trimmed))
        {
            result.Elements.Add(new Element
            {
                Type = ElementType.Transition,
                Text = FountainLexer.UnescapeFountain(trimmed.TrimStart('>').Trim()),
            });
            state.I++;
            return true;
        }

        return false;
    }

    private static bool TryConsumeAutomaticSceneOrTransition(BodyParseState state)
    {
        var lines = state.Lines;
        var i = state.I;
        var trimmed = state.Trimmed;
        var result = state.Result;
        var prevBlank = FountainLexer.PrevBlank(lines, i);
        var nextBlank = FountainLexer.NextBlank(lines, i);
        var classify = trimmed; // already trimmed; indent ignored for non-action

        // Automatic scene heading: blank before + INT/EXT/...
        // Fountain prefers a blank after; we also accept page-tag / synopsis lines
        // immediately under the heading (= page N, = synopsis) for book tooling.
        if (prevBlank && FountainLexer.IsSceneHeadingStart(classify) &&
            (nextBlank || NextIsPageTagOrSynopsis(lines, i)))
        {
            var (heading, sceneNo) = SplitSceneNumber(classify);
            result.Elements.Add(new Element
            {
                Type = ElementType.SceneHeading,
                Text = heading,
                Meta = sceneNo,
            });
            state.I++;
            return true;
        }

        // Transition: uppercase, blank before/after.
        // Classic Fountain: ends with TO: (spaces after colon → Action).
        // Also: FADE IN / FADE OUT / FADE TO BLACK (common and would otherwise
        // invent a phantom scene if treated as Action before the first heading).
        if (prevBlank && nextBlank)
        {
            var transCandidate = state.Raw.TrimStart(); // keep trailing spaces after colon for TO: rule
            if (FountainLexer.IsStandaloneTransitionLine(transCandidate))
            {
                result.Elements.Add(new Element
                {
                    Type = ElementType.Transition,
                    Text = FountainLexer.UnescapeFountain(transCandidate.Trim()),
                });
                state.I++;
                return true;
            }
        }

        return false;
    }

    private static bool TryConsumeCharacterDialogue(BodyParseState state)
    {
        var lines = state.Lines;
        var i = state.I;
        var classify = state.Trimmed;
        var prevBlank = FountainLexer.PrevBlank(lines, i);
        var nextBlank = FountainLexer.NextBlank(lines, i);

        // Character + dialogue: blank before, NOT blank after, all-caps name
        // Also accept when a prior standalone ^ dual-marker left no blank "before"
        // (marker line is not blank, so prevBlank is false) — use pendingDual.
        if ((prevBlank || state.PendingDual) && !nextBlank && FountainLexer.IsCharacterLine(classify))
        {
            var dual = state.PendingDual || classify.TrimEnd().EndsWith('^');
            state.PendingDual = false;
            var (name, ext) = FountainLexer.SplitCharacter(classify.TrimEnd('^', ' ', '\t'));
            state.Result.Elements.Add(new Element
            {
                Type = ElementType.Character,
                Text = name,
                Meta = BuildCharMeta(ext, dual),
            });
            state.I++;
            state.I = ConsumeDialogueBlock(lines, state.I, state.Result);
            return true;
        }

        return false;
    }

    private static bool ConsumeDefaultAction(BodyParseState state)
    {
        // Default: Action (preserve leading indentation)
        state.PendingDual = false; // orphan dual marker shouldn't stick forever
        state.Result.Elements.Add(new Element
        {
            Type = ElementType.Action,
            Text = PreserveActionIndent(state.Raw, FountainLexer.UnescapeFountain(state.Trimmed)),
        });
        state.I++;
        return true;
    }

    private static bool LooksLikeTitlePageKeyLine(string line)
    {
        var trimmed = line.Trim();
        if (!TitleKeyLine.IsMatch(trimmed)) return false;
        // Do not treat transitions (CUT TO:, FADE TO BLACK. is not Key:) or forced >
        // as title-page metadata. "CUT TO:" matches Key:value with empty value.
        if (trimmed.StartsWith('>')) return false;
        if (FountainLexer.IsAllCapsLine(trimmed) && TransitionEnd.IsMatch(trimmed.TrimEnd()))
            return false;
        // Title keys are typically Title Case / mixed case (Title, Draft date, Author).
        // All-caps keys with empty values are almost always body elements.
        var m = TitleKeyLine.Match(trimmed);
        var key = m.Groups[1].Value;
        var rest = m.Groups[2].Value.Trim();
        if (rest.Length == 0 && key.Any(char.IsLetter) && key.Where(char.IsLetter).All(char.IsUpper))
            return false;
        return true;
    }

    private static int ParseTitlePage(string[] lines, int start, ParseResult result)
    {
        var i = start;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Length) return i;

        // Title page only if first non-blank is a real title Key: (not CUT TO: etc.)
        if (!LooksLikeTitlePageKeyLine(lines[i]))
            return i;

        var state = new TitlePageParseState { Result = result };
        while (i < lines.Length)
        {
            if (!TryContinueTitlePage(lines, ref i, state))
                break;
        }

        state.Flush();
        return i;
    }

    private static bool TryContinueTitlePage(string[] lines, ref int i, TitlePageParseState state)
    {
        if (HandleTitleBlank(lines, ref i, state, out var ended))
            return !ended;
        if (HandleTitleKeyLine(lines, ref i, state))
            return true;
        if (HandleMultilineValue(lines, ref i, state))
            return true;
        state.Flush();
        return false;
    }

    private static bool HandleTitleBlank(string[] lines, ref int i, TitlePageParseState state, out bool ended)
    {
        ended = false;
        var trimmed = lines[i].TrimEnd();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (state.Result.TitlePage.Count > 0 || state.CurrentKey is not null)
        {
            state.Flush();
            i++;
            ended = true;
            return true;
        }

        i++;
        return true;
    }

    private static bool HandleTitleKeyLine(string[] lines, ref int i, TitlePageParseState state)
    {
        var trimmed = lines[i].TrimEnd();
        if (!LooksLikeTitlePageKeyLine(trimmed))
            return false;

        state.Flush();
        var m = TitleKeyLine.Match(trimmed.Trim());
        state.CurrentKey = m.Groups[1].Value.Trim();
        var rest = m.Groups[2].Value.Trim();
        if (rest.Length > 0)
            state.ValueBuf.Append(rest);
        i++;
        return true;
    }

    private static bool HandleMultilineValue(string[] lines, ref int i, TitlePageParseState state)
    {
        var raw = lines[i];
        // Multiline value: 3+ spaces or was tab (already expanded)
        if (state.CurrentKey is not null &&
            (raw.StartsWith("   ", StringComparison.Ordinal) || raw.StartsWith('\t')))
        {
            if (state.ValueBuf.Length > 0) state.ValueBuf.Append('\n');
            state.ValueBuf.Append(raw.Trim());
            i++;
            return true;
        }

        return false;
    }

    private static int ConsumeDialogueBlock(string[] lines, int i, ParseResult result)
    {
        while (i < lines.Length && TryConsumeNextDialogueLine(lines, ref i, result))
        { }
        return i;
    }

    private static bool TryConsumeNextDialogueLine(string[] lines, ref int i, ParseResult result)
    {
        var raw = lines[i];
        var trimmed = raw.TrimEnd().Trim();

        if (trimmed.Length == 0)
            return TryAdvancePastDialogueBlank(lines, raw, ref i, result);

        if (TryConsumeParentheticalLine(trimmed, result))
        {
            i++;
            return true;
        }

        if (LooksLikeNewBlock(lines, i))
            return false;

        result.Elements.Add(new Element
        {
            Type = ElementType.Dialogue,
            Text = FountainLexer.UnescapeFountain(trimmed),
        });
        i++;
        return true;
    }

    private static bool TryAdvancePastDialogueBlank(string[] lines, string raw, ref int i, ParseResult result)
    {
        if (!TryContinueDialogueAfterBlank(lines, raw, i, result))
            return false;
        i++;
        return true;
    }

    private static bool TryContinueDialogueAfterBlank(string[] lines, string raw, int i, ParseResult result)
    {
        // Empty line: two+ spaces on the "blank" line continues dialogue (Fountain line breaks)
        if (FountainLexer.IsTwoSpaceContinue(raw) &&
            i + 1 < lines.Length &&
            lines[i + 1].Trim().Length > 0 &&
            !LooksLikeNewBlock(lines, i + 1))
        {
            // preserve intentional blank inside dialogue as newline
            result.Elements.Add(new Element { Type = ElementType.Dialogue, Text = "" });
            return true;
        }
        return false;
    }

    private static bool TryConsumeParentheticalLine(string trimmed, ParseResult result)
    {
        if (!trimmed.StartsWith('(') || !trimmed.Contains(')'))
            return false;

        var close = trimmed.IndexOf(')');
        var inside = trimmed[1..close].Trim();
        result.Elements.Add(new Element
        {
            Type = ElementType.Parenthetical,
            Text = FountainLexer.UnescapeFountain(inside),
        });
        var rest = trimmed[(close + 1)..].Trim();
        if (rest.Length > 0)
            result.Elements.Add(new Element { Type = ElementType.Dialogue, Text = FountainLexer.UnescapeFountain(rest) });
        return true;
    }

    private static bool LooksLikeNewBlock(string[] lines, int i)
    {
        var trimmed = lines[i].TrimEnd().Trim();
        if (trimmed.Length == 0) return true;
        if (LooksLikeForcedNewBlock(trimmed)) return true;
        return LooksLikeAutomaticNewBlock(lines, i, trimmed);
    }

    private static bool LooksLikeForcedNewBlock(string trimmed)
    {
        if (trimmed.StartsWith('#')) return true;
        if (trimmed.StartsWith('=') && !trimmed.StartsWith("===")) return true;
        if (FountainRegex.IsMatch(trimmed, @"^={3,}\s*$")) return true;
        if (trimmed.StartsWith('.') && trimmed.Length > 1 && char.IsLetterOrDigit(trimmed[1])) return true;
        if (trimmed.StartsWith('@')) return true;
        if (trimmed.StartsWith('!')) return true;
        if (trimmed.StartsWith('~')) return true;
        if (IsCentered(trimmed)) return true;
        if (trimmed.StartsWith('>') && !IsCentered(trimmed)) return true;
        return false;
    }

    private static bool LooksLikeAutomaticNewBlock(string[] lines, int i, string trimmed)
    {
        var prevBlank = FountainLexer.PrevBlank(lines, i);
        var nextBlank = FountainLexer.NextBlank(lines, i);

        if (prevBlank && FountainLexer.IsSceneHeadingStart(trimmed) &&
            (nextBlank || NextIsPageTagOrSynopsis(lines, i)))
            return true;
        if (prevBlank && nextBlank && FountainLexer.IsStandaloneTransitionLine(lines[i].TrimStart()))
            return true;
        if (prevBlank && !nextBlank && FountainLexer.IsCharacterLine(trimmed)) return true;
        return false;
    }

    private static bool IsCentered(string trimmed)
    {
        trimmed = trimmed.Trim();
        return trimmed.StartsWith('>') && trimmed.EndsWith('<') && trimmed.Length >= 2;
    }

    /// <summary>
    /// True when the line after a potential scene heading is a Fountain synopsis
    /// or our book page tag (so headings stay recognized without a blank line).
    /// </summary>
    private static bool NextIsPageTagOrSynopsis(string[] lines, int i)
    {
        if (i + 1 >= lines.Length) return false;
        var next = lines[i + 1].Trim();
        if (next.Length == 0) return false;
        // Synopsis: = text (not === section)
        if (next.StartsWith('=') && !next.StartsWith("===", StringComparison.Ordinal))
            return true;
        // Page notes if not yet stripped: [[page N]]
        if (PageTagRegex.IsMatch(next))
            return true;
        return false;
    }

    private static string? BuildCharMeta(string? ext, bool dual)
    {
        if (string.IsNullOrWhiteSpace(ext) && !dual) return null;
        if (dual && string.IsNullOrWhiteSpace(ext)) return "dual";
        if (dual) return ext + "|dual";
        return ext;
    }

    private static (string Heading, string? SceneNumber) SplitSceneNumber(string heading)
    {
        var m = SceneNumberSuffix.Match(heading);
        if (!m.Success) return (heading.Trim(), null);
        return (heading[..m.Index].Trim(), m.Groups[1].Value);
    }

    private static string PreserveActionIndent(string rawLine, string content)
    {
        // Count leading spaces on raw line (tabs already expanded)
        var lead = 0;
        while (lead < rawLine.Length && rawLine[lead] == ' ') lead++;
        if (lead == 0) return content;
        return new string(' ', lead) + content;
    }

    /// <summary>
    /// Count character cues whose extension is voice-over (Meta contains V.O. / VO).
    /// On-camera NARRATOR cues (no V.O. extension) are NOT counted as voice-over.
    /// </summary>
    public static (int VoCues, int TotalCues) CountVoiceoverCues(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return (0, 0);

        var cues = Parse(fountain).Elements
            .Where(e => e.Type == ElementType.Character)
            .ToList();
        var vo = cues.Count(e => IsVoiceoverCue(e));
        return (vo, cues.Count);
    }

    /// <summary>True when a character element is tagged voice-over (not bare NARRATOR on camera).</summary>
    public static bool IsVoiceoverCue(Element e)
    {
        if (e.Type != ElementType.Character) return false;
        // Meta holds extension e.g. "(V.O.)", "(V.O.) (CONT'D)", "(V.O.)|dual"
        if (FountainLexer.IsVoiceOverExtension(e.Meta)) return true;
        // Rare: extension left in Text if parser edge case
        var text = e.Text ?? "";
        if (text.Contains("V.O.", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
