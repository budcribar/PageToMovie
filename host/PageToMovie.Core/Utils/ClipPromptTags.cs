namespace PageToMovie.Core.Utils;

/// <summary>
/// Finds, reads and removes the <c>&lt;Tag&gt;…&lt;/Tag&gt;</c> blocks Stage 2 writes into a clip's
/// visual prompt. We own this format, so it is scanned rather than pattern-matched: a scan cannot
/// pick the wrong closing tag, cannot backtrack, and says exactly where each block starts and ends.
/// <para>Every caller that used to carry its own <c>&lt;Tag&gt;.*?&lt;/Tag&gt;</c> regex comes here
/// instead — one reading of the format, not eleven.</para>
/// </summary>
public static class ClipPromptTags
{
    /// <summary>Where one block sits: the whole block, and the value inside it.</summary>
    public readonly record struct TagSpan(int Start, int End, int InnerStart, int InnerEnd)
    {
        public int Length => End - Start;
        public int InnerLength => InnerEnd - InnerStart;
    }

    /// <summary>Every block for <paramref name="tag"/>, in order. An unclosed tag ends the scan.</summary>
    public static List<TagSpan> Find(string? text, string tag)
    {
        var spans = new List<TagSpan>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tag))
            return spans;

        var open = $"<{tag}>";
        var close = $"</{tag}>";
        var from = 0;
        while (from < text.Length)
        {
            var start = text.IndexOf(open, from, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                break;
            var innerStart = start + open.Length;
            var innerEnd = text.IndexOf(close, innerStart, StringComparison.OrdinalIgnoreCase);
            if (innerEnd < 0)
                break;
            var end = innerEnd + close.Length;
            spans.Add(new TagSpan(start, end, innerStart, innerEnd));
            from = end;
        }

        return spans;
    }

    /// <summary>Value inside the first block, or null when the tag is absent.</summary>
    public static string? ReadFirstInner(string? text, string tag)
    {
        var spans = Find(text, tag);
        return spans.Count == 0 ? null : text![spans[0].InnerStart..spans[0].InnerEnd];
    }

    /// <summary>The first block including its tags, or null when the tag is absent.</summary>
    public static string? ReadFirstBlock(string? text, string tag)
    {
        var spans = Find(text, tag);
        return spans.Count == 0 ? null : text![spans[0].Start..spans[0].End];
    }

    /// <summary>Remove every block for <paramref name="tag"/>, and the whitespace each left behind.</summary>
    public static string Remove(string? text, string tag)
    {
        var spans = Find(text, tag);
        return spans.Count == 0 ? text ?? "" : Cut(text!, spans);
    }

    /// <summary>
    /// Keep the first block of each distinct value and drop the later copies — the same rule the
    /// prompt-budget dedupe applied, which is by value, not "keep only the first block".
    /// </summary>
    public static string DropDuplicateBlocks(string? text, string tag)
    {
        var spans = Find(text, tag);
        if (spans.Count < 2)
            return text ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drop = spans.Where(span => !seen.Add(Normalize(text![span.Start..span.End]))).ToList();
        return drop.Count == 0 ? text! : Cut(text!, drop);
    }

    /// <summary>Rewrite each block's value with <paramref name="rewrite"/>, tags kept in place.</summary>
    public static string RewriteBlocks(string? text, string tag, Func<string, string> rewrite)
    {
        var spans = Find(text, tag);
        if (spans.Count == 0)
            return text ?? "";
        var sb = new System.Text.StringBuilder();
        var at = 0;
        foreach (var span in spans)
        {
            sb.Append(text!, at, span.InnerStart - at);
            sb.Append(rewrite(text![span.InnerStart..span.InnerEnd]));
            sb.Append(text, span.InnerEnd, span.End - span.InnerEnd);
            at = span.End;
        }

        sb.Append(text!, at, text!.Length - at);
        return sb.ToString();
    }

    /// <summary>Cut the given spans out, taking the whitespace that trailed each one.</summary>
    private static string Cut(string text, List<TagSpan> spans)
    {
        var sb = new System.Text.StringBuilder();
        var at = 0;
        foreach (var span in spans)
        {
            sb.Append(text, at, span.Start - at);
            at = span.End;
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
        }

        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
