using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Components;

/// <summary>
/// Word-level expected-vs-heard markup for clip dialogue verification (Scenes inspector + report).
/// </summary>
internal static class DialogueDiffHtml
{
    public static MarkupString Render(string? expected, string? heard)
    {
        var expStr = expected ?? "";
        var heardStr = heard ?? "";
        if (string.IsNullOrWhiteSpace(expStr) && string.IsNullOrWhiteSpace(heardStr))
            return new MarkupString("—");

        var expWords = CommonRegex.Split(expStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        var heardWords = CommonRegex.Split(heardStr.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

        static string Clean(string w) => CommonRegex.Replace(w.ToLowerInvariant(), @"[^\w]", "");

        var expClean = expWords.Select(Clean).ToList();
        var heardClean = heardWords.Select(Clean).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"small\">");

        sb.Append("<div><strong>Expected:</strong> ");
        AppendWordLine(sb, expWords, expClean, heardClean,
            missing: true);
        sb.Append("</div>");

        sb.Append("<div><strong>Heard:</strong> ");
        AppendWordLine(sb, heardWords, heardClean, expClean,
            missing: false);
        sb.Append("</div></div>");

        return new MarkupString(sb.ToString());
    }

    private static void AppendWordLine(
        System.Text.StringBuilder sb,
        List<string> words,
        List<string> clean,
        List<string> otherClean,
        bool missing)
    {
        for (int i = 0; i < words.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(words[i]);
            var c = clean[i];
            if (!string.IsNullOrEmpty(c) && !otherClean.Contains(c))
            {
                if (missing)
                    sb.Append($"<span class=\"badge bg-danger-subtle text-danger text-decoration-line-through me-1\" title=\"Missing from spoken clip audio\">{word}</span> ");
                else
                    sb.Append($"<span class=\"badge bg-warning-subtle text-warning border border-warning-subtle me-1\" title=\"Extra/changed word heard in clip\">{word}</span> ");
            }
            else
            {
                sb.Append($"{word} ");
            }
        }
    }
}
