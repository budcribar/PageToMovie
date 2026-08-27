using Markdig;
using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Utils;
using System.Text.RegularExpressions;

namespace PageToMovie.Web.Services;

public static class MarkdownHelper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseListExtras()
        // Models reach for a pipe table whenever they are scoring categories. Without this the
        // rows collapse into one run-on paragraph of "|" characters.
        .UsePipeTables()
        .Build();

    private static readonly Regex LeadingTagRe = new(@"^<(p|div)>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TrailingTagRe = new(@"\s*</(p|div)>$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OpenTagRe = new(@"<(p|div)>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CloseTagRe = new(@"\s*</(p|div)>", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex BrTagRe = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>A pipe-table delimiter row: <c>|---|:---:|</c>, with or without the outer pipes.</summary>
    private static readonly Regex TableDelimiterRe = new(
        @"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$",
        RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Render Markdown or AI text payload to MarkupString.
    /// Automatically strips raw HTML paragraph tags (<p>, </p>, <br>) emitted by LLMs so they render as clean HTML.
    /// </summary>
    public static MarkupString Render(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new MarkupString("");

        var text = input.Trim();

        // If the AI model returned raw HTML tags (e.g. <p>...</p> or <br/>), clean raw paragraph tags
        // so Markdig doesn't double-escape them into literal &lt;p&gt; text.
        if (text.Contains('<'))
        {
            text = LeadingTagRe.Replace(text, "");
            text = TrailingTagRe.Replace(text, "");
            text = OpenTagRe.Replace(text, "\n\n");
            text = CloseTagRe.Replace(text, "");
            text = BrTagRe.Replace(text, "\n");
        }

        text = SeparateTablesFromParagraphs(text);

        var html = Markdown.ToHtml(text, Pipeline);
        return new MarkupString(html);
    }

    /// <summary>
    /// A table only parses when it starts its own block. Models routinely write the header row
    /// straight under a sentence, which makes the whole table part of that paragraph, so insert the
    /// blank line they left out. Detection keys on the delimiter row, which is what actually makes
    /// a pipe table a table.
    /// </summary>
    private static string SeparateTablesFromParagraphs(string text)
    {
        if (!text.Contains('|'))
            return text;

        var lines = text.Split('\n');
        var output = new List<string>(lines.Length + 4);
        for (var i = 0; i < lines.Length; i++)
        {
            var startsTable =
                i >= 1
                && i + 1 < lines.Length
                && lines[i].Contains('|')
                && TableDelimiterRe.IsMatch(lines[i + 1])
                && !string.IsNullOrWhiteSpace(lines[i - 1])
                && !TableDelimiterRe.IsMatch(lines[i - 1]);
            if (startsTable)
                output.Add("");
            output.Add(lines[i]);
        }
        return string.Join("\n", output);
    }

    /// <summary>
    /// Strip all HTML tags for plain-text contexts (e.g. collapsed headers, tooltips, title tags).
    /// </summary>
    public static string StripHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var text = CommonRegex.HtmlTags.Replace(input, "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
