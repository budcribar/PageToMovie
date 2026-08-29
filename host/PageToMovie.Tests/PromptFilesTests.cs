using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class PromptFilesTests
{
    [Fact]
    public void TryReadEmbedded_has_core_cast_and_book_prompts()
    {
        var cast = PromptFiles.TryReadEmbedded("prompts/fountain_to_cast.txt");
        var lit = PromptFiles.TryReadEmbedded("prompts/cast_visual_literalize.txt");
        var book = PromptFiles.TryReadEmbedded("prompts/book_to_fountain.txt");
        var gen = PromptFiles.TryReadEmbedded("prompts/clip_gen_rules.txt");
        var ar = PromptFiles.TryReadEmbedded("prompts/clip_auto_review.txt");

        Assert.False(string.IsNullOrWhiteSpace(cast), "fountain_to_cast should be embedded in Engine");
        Assert.Contains("Character_", cast!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(lit));
        Assert.False(string.IsNullOrWhiteSpace(book));
        Assert.False(string.IsNullOrWhiteSpace(gen), "clip_gen_rules should be embedded (retired stub)");
        Assert.DoesNotContain("picture-book CG", gen!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(ar), "clip_auto_review should be embedded");
    }

    [Fact]
    public async Task ReadAsync_works_with_data_workspace_via_embed()
    {
        // Railway layout: workspace is /data with no prompts folder — must not matter.
        var text = await PromptFiles.ReadAsync("prompts/fountain_to_cast.txt", workspaceRoot: "/data");
        Assert.Contains("cast", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_override_dir_wins_over_embed()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-prompts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "fountain_to_cast.txt"), "OVERRIDE CAST PROMPT");
            var prev = PromptFiles.PromptsDirOverride;
            PromptFiles.PromptsDirOverride = tmp;
            try
            {
                var text = await PromptFiles.ReadAsync("prompts/fountain_to_cast.txt");
                Assert.Equal("OVERRIDE CAST PROMPT", text);
            }
            finally
            {
                PromptFiles.PromptsDirOverride = prev;
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }
}
