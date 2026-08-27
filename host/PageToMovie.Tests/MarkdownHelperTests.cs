using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class MarkdownHelperTests
{
    [Fact]
    public void Render_RendersMarkdownToHtmlMarkupString()
    {
        // Arrange
        var markdown = "### Executive Overview\n- **Score**: 9/10\n- Great pacing!";

        // Act
        var markup = MarkdownHelper.Render(markdown);
        var html = markup.Value;

        // Assert
        Assert.Contains("<h3>Executive Overview</h3>", html);
        Assert.Contains("<strong>Score</strong>: 9/10", html);
        Assert.Contains("<li>Great pacing!</li>", html);
    }

    [Fact]
    public void Render_SanitizesRawLlmHtmlParagraphTags_PreventsLiteralParagraphText()
    {
        // Arrange: LLM raw payload containing literal <p> and </p> wrappers
        var rawLlmOutput = "<p>Strong spatial progression from single medium shot at table with candle.</p>";

        // Act
        var markup = MarkdownHelper.Render(rawLlmOutput);
        var html = markup.Value;

        // Assert: Should NOT escape into &lt;p&gt; text inside rendered HTML
        Assert.DoesNotContain("&lt;p&gt;", html);
        Assert.DoesNotContain("&lt;/p&gt;", html);
        Assert.Contains("Strong spatial progression from single medium shot", html);
    }

    [Fact]
    public void Render_RendersPipeTableAsHtmlTable()
    {
        // The shape the review models actually emit for category scores.
        var markdown = """
            | Category | Score | Status |
            | :--- | :---: | :--- |
            | Continuity & Visual Cohesion | 5/10 | Requires Remediation |
            | Character Lock & Model Fidelity | 7/10 | Approved |
            """;

        var html = MarkdownHelper.Render(markdown).Value;

        Assert.Contains("<table", html);
        Assert.Contains("<th", html);
        Assert.Contains("Continuity &amp; Visual Cohesion", html);
        // The pipes must not survive as literal text.
        Assert.DoesNotContain("| Score |", html);
        Assert.DoesNotContain(":---", html);
    }

    [Fact]
    public void Render_RendersTableEvenWhenTheModelOmitsTheBlankLineBeforeIt()
    {
        // No blank line after the sentence: Markdig would otherwise swallow the whole table into
        // that paragraph and render one run-on line of pipes.
        var markdown = """
            **Overall Evaluation:** 6/10 — Verdict: Needs Polish
            | Category | Score |
            | --- | --- |
            | Lighting | 6/10 |
            """;

        var html = MarkdownHelper.Render(markdown).Value;

        Assert.Contains("<table", html);
        Assert.Contains("<td", html);
        Assert.Contains("Needs Polish", html);
        Assert.DoesNotContain("| Category |", html);
    }

    [Fact]
    public void Render_LeavesProseContainingPipesAlone()
    {
        // A stray pipe is not a table; nothing here should turn into markup.
        var markdown = "Use the A | B split screen for the final beat.";

        var html = MarkdownHelper.Render(markdown).Value;

        Assert.DoesNotContain("<table", html);
        Assert.Contains("A | B split screen", html);
    }

    [Fact]
    public void RenderOrDash_StripsModelParagraphTags_AndFallsBackToADash()
    {
        // The sequence-group notes come back wrapped in the model's own <p> tags. Rendering them
        // through a conditional that mixed a string with a MarkupString escaped the whole thing,
        // so the tags showed on screen as text.
        var html = MarkdownHelper.RenderOrDash(
            "<p>Spatial direction flows logically, though background art transitions abruptly.</p>").Value;

        Assert.DoesNotContain("&lt;p&gt;", html);
        Assert.DoesNotContain("&lt;/p&gt;", html);
        Assert.Contains("Spatial direction flows logically", html);

        Assert.Equal("—", MarkdownHelper.RenderOrDash(null).Value);
        Assert.Equal("—", MarkdownHelper.RenderOrDash("   ").Value);
    }

    [Fact]
    public void StripHtml_StripsAllHtmlTagsAndDecodesEntities()
    {
        // Arrange
        var htmlInput = "<p>Camera movement &amp; framing locked <strong>well</strong>.</p>";

        // Act
        var plainText = MarkdownHelper.StripHtml(htmlInput);

        // Assert
        Assert.Equal("Camera movement & framing locked well.", plainText);
    }
}
