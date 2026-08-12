using System;
using System.Text;
using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Core.Utils;

/// <summary>
/// Utility for detecting and stripping Project Gutenberg legal headers and footers
/// from public domain book texts during import and screenplay adaptation.
/// </summary>
public static class GutenbergCleaner
{
    private static readonly Regex StartHeaderRegex = new(@"\*\*\*\s*START OF (?:THE|THIS) PROJECT GUTENBERG[^\*\r\n]*\*\*\*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex EndFooterRegex = new(@"(?:\*\*\*\s*END OF (?:THE|THIS) PROJECT GUTENBERG[^\*\r\n]*\*\*\*|End of (?:the|this|The|This) Project Gutenberg)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex GutenbergHeaderMarkerRegex = new(@"^The Project Gutenberg (?:eBook|EBook|eText|EText)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Checks whether the text contains a Project Gutenberg header or license block.
    /// </summary>
    public static bool HasGutenbergHeader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return StartHeaderRegex.IsMatch(text) || GutenbergHeaderMarkerRegex.IsMatch(text);
    }

    /// <summary>
    /// Strips Project Gutenberg headers and footers from text if present.
    /// Returns the cleaned book content trimmed of legal preamble and end license blocks.
    /// </summary>
    public static string StripHeaderAndFooter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleaned = text;

        // 1. Strip Header
        var startMatch = StartHeaderRegex.Match(cleaned);
        if (startMatch.Success)
        {
            var headerEndIndex = startMatch.Index + startMatch.Length;
            cleaned = cleaned.Substring(headerEndIndex);
        }
        else
        {
            // Fallback header detection for older Gutenberg formats without *** START OF
            var headerMarker = GutenbergHeaderMarkerRegex.Match(cleaned);
            if (headerMarker.Success && headerMarker.Index < 2000)
            {
                // Find transition past preamble metadata (e.g. after line containing Title / Author / Produced by)
                var lines = cleaned.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var contentStartIndex = 0;
                var inPreamble = true;

                for (var i = 0; i < Math.Min(lines.Length, 120); i++)
                {
                    var line = lines[i].Trim();
                    if (inPreamble)
                    {
                        if (line.StartsWith("***") || line.StartsWith("Produced by", StringComparison.OrdinalIgnoreCase))
                        {
                            contentStartIndex = i + 1;
                            inPreamble = false;
                        }
                    }
                }

                if (!inPreamble && contentStartIndex < lines.Length)
                {
                    cleaned = string.Join("\n", lines.AsSpan(contentStartIndex).ToArray());
                }
            }
        }

        // 2. Strip Footer
        var endMatch = EndFooterRegex.Match(cleaned);
        if (endMatch.Success)
        {
            cleaned = cleaned.Substring(0, endMatch.Index);
        }

        return cleaned.Trim();
    }
}
