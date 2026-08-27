namespace PageToMovie.Core.Utils;

/// <summary>Semantic field a clip prompt segment carries. <see cref="Action"/> is the catch-all.</summary>
public enum ClipPromptField
{
    Action,
    StyleLock,
    Setting,
    Cast,
    Sound,
    Speech,
    MustNot,
    Wardrobe,
    Lighting,
    Camera,
    Performance,
    Optics,
    Grade,
}

/// <summary>
/// One editable piece of a clip prompt. <see cref="Prefix"/> and <see cref="Suffix"/> are the
/// literal markers that introduced it (<c>"STYLE LOCK: "</c>, <c>"&lt;Optics&gt;"</c> …) and are
/// re-emitted verbatim, so only <see cref="Value"/> is ever the user's to change.
/// </summary>
public sealed record ClipPromptSection(
    ClipPromptField Field,
    string Label,
    string Prefix,
    string Value,
    string Suffix)
{
    /// <summary>Free prose with no marker of its own — shown, but with nothing to label it.</summary>
    public bool IsFreeText => Prefix.Length == 0 && Suffix.Length == 0;

    public ClipPromptSection WithValue(string? value) => this with { Value = value ?? "" };
}

/// <summary>
/// Splits a clip's <c>visual_prompt</c> into labelled fields for editing, and puts it back together
/// again. The prompt is only half tagged: <c>&lt;Lighting&gt;</c>, <c>&lt;Camera&gt;</c>,
/// <c>&lt;Performance&gt;</c> and <c>&lt;Optics&gt;</c> are real tags, while STYLE LOCK, the scene
/// slug, the cast list, the sound cue and the colour grade are plain prose. Both are recognised
/// here so the editor can show one box per field either way.
///
/// <para>
/// The section list covers the prompt with no gaps, so <see cref="Compose"/> of an unedited
/// <see cref="Parse"/> returns the original string character for character. That is what lets the
/// editor ship without changing a single byte of what any model is sent — this class is the one
/// place that knows the format, so moving to fully tagged prompts later is a change to the writer
/// alone, and old plans keep parsing.
/// </para>
/// </summary>
public static class ClipPromptSections
{
    /// <summary>Every field Stage 2 tags, in the order it emits them.</summary>
    private static readonly (string Tag, ClipPromptField Field)[] TaggedFields =
    {
        (PromptFieldTags.StyleLock, ClipPromptField.StyleLock),
        (PromptFieldTags.Setting, ClipPromptField.Setting),
        (PromptFieldTags.Cast, ClipPromptField.Cast),
        (PromptFieldTags.Action, ClipPromptField.Action),
        (PromptFieldTags.Sound, ClipPromptField.Sound),
        // Legacy plans only — Stage 2 stopped emitting it once <Audio> became the single copy
        // of the spoken line. Still parsed so an old prompt round-trips instead of arriving as
        // raw tag text in an Action box; the editor does not offer it as an edit box.
        (PromptFieldTags.Speech, ClipPromptField.Speech),
        (PromptFieldTags.MustNot, ClipPromptField.MustNot),
        (PromptFieldTags.Wardrobe, ClipPromptField.Wardrobe),
        (PromptFieldTags.Lighting, ClipPromptField.Lighting),
        (PromptFieldTags.Camera, ClipPromptField.Camera),
        (PromptFieldTags.Performance, ClipPromptField.Performance),
        (PromptFieldTags.Optics, ClipPromptField.Optics),
        (PromptFieldTags.Grade, ClipPromptField.Grade),
    };

    public static string LabelFor(ClipPromptField field) => field switch
    {
        ClipPromptField.StyleLock => "Style lock",
        ClipPromptField.Setting => "Setting",
        // Stage 2 fills this from "others" — the visible cast MINUS the primary subject — so it
        // reads empty-ish on a clip whose action is about the primary. Not "who is visible".
        ClipPromptField.Cast => "Also on screen",
        ClipPromptField.Sound => "Sound",
        ClipPromptField.Speech => "Speech",
        ClipPromptField.MustNot => "Must not",
        ClipPromptField.Wardrobe => "Wardrobe",
        ClipPromptField.Lighting => "Lighting",
        ClipPromptField.Camera => "Camera",
        ClipPromptField.Performance => "Performance",
        ClipPromptField.Optics => "Optics",
        ClipPromptField.Grade => "Colour grade",
        _ => "Action",
    };

    private sealed record Span(int Start, int Length, ClipPromptSection Section)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// Ordered, gap-free sections. An empty or whitespace-only prompt yields a single empty
    /// <see cref="ClipPromptField.Action"/> section so the editor always has somewhere to type.
    /// </summary>
    public static IReadOnlyList<ClipPromptSection> Parse(string? visualPrompt)
    {
        var text = visualPrompt ?? "";
        if (text.Length == 0)
            return new[] { Free("") };

        var spans = new List<Span>();
        foreach (var (tag, field) in TaggedFields)
            AddSpans(spans, text, $@"<{tag}>(.*?)</{tag}>", field, LabelFor(field), $"<{tag}>", $"</{tag}>");

        // Tags only. There is deliberately no prose fallback: Stage 2 tags every field, so
        // matching the old flattened form would be guesswork that quietly does nothing on any
        // current plan. A plan built before tagging still opens and still round-trips — its whole
        // prompt simply arrives as one Action box instead of a dozen labelled ones.
        return Stitch(text, spans);
    }

    /// <summary>Reassemble edited sections. Unedited input round-trips exactly.</summary>
    public static string Compose(IEnumerable<ClipPromptSection> sections)
    {
        if (sections is null)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (var s in sections)
        {
            // A field the user emptied takes its marker with it — a bare "STYLE LOCK:" with
            // nothing after it is worse than no style lock at all.
            if (!s.IsFreeText && string.IsNullOrWhiteSpace(s.Value))
                continue;
            sb.Append(s.Prefix).Append(s.Value).Append(s.Suffix);
        }
        return sb.ToString();
    }

    private static ClipPromptSection Free(string value) =>
        new(ClipPromptField.Action, LabelFor(ClipPromptField.Action), "", value, "");

    private static void AddSpans(
        List<Span> spans, string text, string pattern, ClipPromptField field,
        string? label = null, string? fixedPrefix = null, string? fixedSuffix = null)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 CommonRegex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            if (!m.Success || m.Length == 0)
                continue;
            if (spans.Any(s => m.Index < s.End && s.Start < m.Index + m.Length))
                continue;   // already claimed by an earlier, more specific field
            var prefix = fixedPrefix ?? GroupOrEmpty(m, "prefix");
            var suffix = fixedSuffix ?? GroupOrEmpty(m, "suffix");
            var value = CaptureValue(m);
            spans.Add(new Span(m.Index, m.Length,
                new ClipPromptSection(field, label ?? LabelFor(field), prefix, value, suffix)));
        }
    }

    private static string GroupOrEmpty(System.Text.RegularExpressions.Match m, string name) =>
        m.Groups[name].Success ? m.Groups[name].Value : "";

    private static string CaptureValue(System.Text.RegularExpressions.Match m)
    {
        if (m.Groups["value"].Success)
            return m.Groups["value"].Value;
        if (m.Groups.Count > 1)
            return m.Groups[1].Value;
        return m.Value;
    }

    /// <summary>Interleave the claimed spans with the free text between them, leaving no gaps.</summary>
    private static List<ClipPromptSection> Stitch(string text, List<Span> spans)
    {
        var result = new List<ClipPromptSection>();
        var cursor = 0;
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (span.Start > cursor)
                AddFree(result, text[cursor..span.Start]);
            result.Add(span.Section);
            cursor = span.End;
        }
        if (cursor < text.Length)
            AddFree(result, text[cursor..]);
        if (result.Count == 0)
            result.Add(Free(text));
        return result;
    }

    /// <summary>
    /// Whitespace-only runs between fields are joined onto the previous section's suffix rather
    /// than becoming their own empty edit box.
    /// </summary>
    private static void AddFree(List<ClipPromptSection> result, string chunk)
    {
        if (chunk.Length == 0)
            return;
        if (chunk.Trim().Length == 0 && result.Count > 0)
        {
            var last = result[^1];
            result[^1] = last with { Suffix = last.Suffix + chunk };
            return;
        }
        result.Add(Free(chunk));
    }
}
