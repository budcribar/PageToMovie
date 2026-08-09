using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_VerificationReport
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public int ClipNumber { get; set; }
    [Parameter] public ClipDialogueVerificationResult? Report { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private static MarkupString RenderDiffHtml(string? expected, string? heard)
    {
        var expStr = expected ?? "";
        var heardStr = heard ?? "";
        if (string.IsNullOrWhiteSpace(expStr) && string.IsNullOrWhiteSpace(heardStr))
            return new MarkupString("—");

        var expWords = System.Text.RegularExpressions.Regex.Split(expStr.Trim(), @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        var heardWords = System.Text.RegularExpressions.Regex.Split(heardStr.Trim(), @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

        static string Clean(string w) => System.Text.RegularExpressions.Regex.Replace(w.ToLowerInvariant(), @"[^\w]", "");

        var expClean = expWords.Select(Clean).ToList();
        var heardClean = heardWords.Select(Clean).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"small\">");
        sb.Append("<div><strong>Expected:</strong> ");
        for (int i = 0; i < expWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(expWords[i]);
            var c = expClean[i];
            if (!string.IsNullOrEmpty(c) && !heardClean.Contains(c))
                sb.Append($"<span class=\"badge bg-danger-subtle text-danger text-decoration-line-through me-1\" title=\"Missing from spoken clip audio\">{word}</span> ");
            else
                sb.Append($"{word} ");
        }
        sb.Append("</div>");
        sb.Append("<div><strong>Heard:</strong> ");
        for (int i = 0; i < heardWords.Count; i++)
        {
            var word = System.Net.WebUtility.HtmlEncode(heardWords[i]);
            var c = heardClean[i];
            if (!string.IsNullOrEmpty(c) && !expClean.Contains(c))
                sb.Append($"<span class=\"badge bg-warning-subtle text-warning border border-warning-subtle me-1\" title=\"Extra/changed word heard in clip\">{word}</span> ");
            else
                sb.Append($"{word} ");
        }
        sb.Append("</div></div>");
        return new MarkupString(sb.ToString());
    }
}
