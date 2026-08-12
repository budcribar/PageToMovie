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
        .Build();

    private static readonly Regex LeadingTagRe = new(@"^<(p|div)>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TrailingTagRe = new(@"\s*</(p|div)>$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OpenTagRe = new(@"<(p|div)>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CloseTagRe = new(@"\s*</(p|div)>", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex BrTagRe = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

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

        var html = Markdown.ToHtml(text, Pipeline);
        return new MarkupString(html);
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
