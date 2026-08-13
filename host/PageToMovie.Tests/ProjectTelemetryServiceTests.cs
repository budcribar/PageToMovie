using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectTelemetryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectTelemetryService _tel;

    public ProjectTelemetryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-tel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "P"));
        Directory.CreateDirectory(Path.Combine(_root, "prompts"));
        File.WriteAllText(Path.Combine(_root, "projects", "P", "project.json"), """{"id":"P"}""");
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        var store = new ProjectStore(opts);
        _tel = new ProjectTelemetryService(store, NullLogger<ProjectTelemetryService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* */ }
    }

    [Fact]
    public async Task LogApiCall_writes_full_prompt_jsonl()
    {
        using (ProjectTelemetryService.UseProject("P"))
        {
            await _tel.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video",
                Endpoint = "videos/generations",
                Model = "grok-imagine-video",
                Prompt = "FULL PROMPT BODY HERE with Character_Hero",
                PromptChars = 40,
                RefsAttached = true,
                Ok = true,
            });
        }

        var path = _tel.ApiCallsPath("P");
        Assert.True(File.Exists(path));
        var line = File.ReadAllText(path).Trim();
        Assert.Contains("FULL PROMPT BODY HERE", line);
        Assert.Contains("\"kind\":\"video\"", line.Replace(" ", ""));
    }

    [Fact]
    public async Task LogMediaOp_condensed_drops_frame_spam()
    {
        var raw = string.Join('\n',
            "frame=  1 fps=0.0",
            "frame=  2 fps=30",
            "out_time=00:00:01.00",
            "speed=1.2x",
            "Error opening input file missing.mp4",
            "Conversion failed!");
        var rec = ProjectTelemetryService.CondenseMediaOp(
            op: "remux",
            args: "-i a.mp4 -i b.mp4 out.mp4",
            inputs: new[] { "a.mp4", "b.mp4" },
            output: "out.mp4",
            exitCode: 1,
            timedOut: false,
            wallMs: 1234,
            rawLog: raw,
            scene: 1,
            includedCount: 2,
            excludedCount: 1);

        Assert.False(rec.Ok);
        Assert.NotNull(rec.StderrInteresting);
        Assert.Contains(rec.StderrInteresting!, s => s.Contains("Error opening", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rec.StderrInteresting!, s => s.StartsWith("frame=", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rec.Progress);

        using (ProjectTelemetryService.UseProject("P"))
            await _tel.LogMediaOpAsync(rec);

        var path = _tel.MediaOpsPath("P");
        Assert.True(File.Exists(path));
        Assert.EndsWith("media_ops.jsonl", path, StringComparison.OrdinalIgnoreCase);
        var line = File.ReadAllText(path);
        Assert.Contains("\"op\":\"remux\"", line.Replace(" ", ""));
        Assert.DoesNotContain("frame=  2", line);
    }

    [Fact]
    public void IsInterestingLogLine_filters()
    {
        Assert.True(ProjectTelemetryService.IsInterestingLogLine("Error opening file"));
        Assert.False(ProjectTelemetryService.IsInterestingLogLine("frame=42"));
    }
}
