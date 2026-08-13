using System;
using System.IO;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ClipPromptHistoryTests
{
    [Fact]
    public async Task ListClipPromptHistory_returns_empty_when_no_history_dir()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_prompt_hist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await FilmJobService.ListClipPromptHistoryAsync(root, scene: 1, clip: 1);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ListClipPromptHistory_parses_and_sorts_newest_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_prompt_hist_" + Guid.NewGuid().ToString("N"));
        var historyDir = Path.Combine(root, "assets", "video", "history");
        Directory.CreateDirectory(historyDir);
        try
        {
            var older = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
            var newer = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();

            File.WriteAllText(
                Path.Combine(historyDir, $"scene_01_clip_02_{older}.meta.json"),
                """{"prompt":"old prompt text"}""");
            File.WriteAllText(
                Path.Combine(historyDir, $"scene_01_clip_02_{newer}.meta.json"),
                """{"prompt":"newer prompt text"}""");
            // Different clip — must not leak into scene 1 clip 2's history.
            File.WriteAllText(
                Path.Combine(historyDir, $"scene_01_clip_03_{newer}.meta.json"),
                """{"prompt":"different clip"}""");

            var result = await FilmJobService.ListClipPromptHistoryAsync(root, scene: 1, clip: 2);

            Assert.Equal(2, result.Count);
            Assert.Equal("newer prompt text", result[0].Prompt);
            Assert.Equal("old prompt text", result[1].Prompt);
            Assert.True(result[0].TimestampUtc > result[1].TimestampUtc);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ListClipPromptHistory_skips_unreadable_entries_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_prompt_hist_" + Guid.NewGuid().ToString("N"));
        var historyDir = Path.Combine(root, "assets", "video", "history");
        Directory.CreateDirectory(historyDir);
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(Path.Combine(historyDir, $"scene_01_clip_01_{ts}.meta.json"), "not valid json{{{");

            var result = await FilmJobService.ListClipPromptHistoryAsync(root, scene: 1, clip: 1);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
