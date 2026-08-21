using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Film Generate / Regen must open the Generating popup before predecessor prep
/// (plate upload, predecessor MP4 upload, ffmpeg trim + extend-source POST).
/// </summary>
public class ScenesGenerateJobModalTests
{
    [Fact]
    public void OpenJobModal_sets_show_flag_and_local_preparing_snapshot()
    {
        var page = new Scenes();
        var gen = page.Gen;

        gen.OpenJobModal(Scenes.ScenesGeneration.UploadingPredecessorMessage, scene: 2, clip: 3);

        Assert.True(gen._showJobModal);
        Assert.NotNull(gen._job);
        Assert.True(Scenes.ScenesGeneration.IsLocalPreparingJob(gen._job));
        Assert.Equal(Scenes.ScenesGeneration.UploadingPredecessorMessage, gen._job!.Message);
        Assert.Equal(
            Scenes.ScenesGeneration.UploadingPredecessorMessage,
            Scenes.ScenesGeneration.LiveGenStatusLabel(gen._job));
        Assert.DoesNotContain("Waiting", Scenes.ScenesGeneration.LiveGenStatusLabel(gen._job), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenJobModal_replaces_stale_waiting_snapshot()
    {
        var page = new Scenes();
        var gen = page.Gen;
        gen._job = new JobSnapshot
        {
            JobId = "old-server-job",
            Status = "queued",
            Kind = "batch",
            Message = "Queued",
        };

        gen.OpenJobModal(Scenes.ScenesGeneration.PreparingExtendMessage);

        Assert.True(gen._showJobModal);
        Assert.True(Scenes.ScenesGeneration.IsLocalPreparingJob(gen._job));
        Assert.Equal(
            Scenes.ScenesGeneration.PreparingExtendMessage,
            Scenes.ScenesGeneration.LiveGenStatusLabel(gen._job!));
    }

    [Fact]
    public void LiveGenStatusLabel_prefers_local_preparing_message()
    {
        var snap = Scenes.ScenesGeneration.CreateLocalPreparingJob(
            Scenes.ScenesGeneration.PreparingExtendMessage);
        Assert.Equal(
            Scenes.ScenesGeneration.PreparingExtendMessage,
            Scenes.ScenesGeneration.LiveGenStatusLabel(snap));
    }

    [Fact]
    public void SetPreparingMessage_updates_open_local_snapshot()
    {
        var page = new Scenes();
        var gen = page.Gen;
        gen.OpenJobModal(Scenes.ScenesGeneration.PreparingDefaultMessage);

        gen.SetPreparingMessage(Scenes.ScenesGeneration.UploadingPredecessorMessage);

        Assert.True(gen._showJobModal);
        Assert.Equal(
            Scenes.ScenesGeneration.UploadingPredecessorMessage,
            Scenes.ScenesGeneration.LiveGenStatusLabel(gen._job!));
        Assert.Contains(Scenes.ScenesGeneration.UploadingPredecessorMessage, gen._job!.Log);
    }

    [Theory]
    [InlineData("ScenesGeneration.cs", "ForceRegenSceneAsync")]
    [InlineData("ScenesGeneration.cs", "GenOneSceneAsync")]
    [InlineData("ScenesGeneration.cs", "RunSelectedBatchAsync")]
    [InlineData("ScenesGeneration.cs", "StartBatchForceSelectedAsync")]
    [InlineData("ScenesClipRegen.cs", "RegenSelectedClipsAsync")]
    [InlineData("ScenesClipRegen.cs", "RegenClipAsync")]
    public void Opens_job_modal_before_predecessor_prep(string fileName, string methodName)
    {
        var body = ExtractMethodBody(fileName, methodName);
        var modal = FirstIndexOfAny(body, "OpenJobModalAndPaintAsync", "OpenJobModal(");
        var prep = FirstIndexOfAny(
            body,
            "EnsurePredecessorsUploadedAsync",
            "PrepareExtendSourceAsync");

        Assert.True(modal >= 0, $"{methodName} must open the Generating popup.");
        Assert.True(prep >= 0, $"{methodName} must still run predecessor prep.");
        Assert.True(
            modal < prep,
            $"{methodName} must call OpenJobModal before EnsurePredecessors / PrepareExtendSource.");
    }

    [Theory]
    [InlineData("ScenesGeneration.cs", "ForceRegenSceneAsync")]
    [InlineData("ScenesGeneration.cs", "GenOneSceneAsync")]
    [InlineData("ScenesGeneration.cs", "RunSelectedBatchAsync")]
    [InlineData("ScenesGeneration.cs", "StartBatchForceSelectedAsync")]
    [InlineData("ScenesGeneration.cs", "RegenStaleInSceneAsync")]
    [InlineData("ScenesClipRegen.cs", "RegenSelectedClipsAsync")]
    [InlineData("ScenesClipRegen.cs", "RegenClipAsync")]
    public void Opens_job_modal_before_starting_server_job(string fileName, string methodName)
    {
        var body = ExtractMethodBody(fileName, methodName);
        var modal = FirstIndexOfAny(body, "OpenJobModalAndPaintAsync", "OpenJobModal(");
        var start = FirstIndexOfAny(
            body,
            "StartSceneGenAsync",
            "StartBatchGenAsync",
            "StartClipBatchGenAsync");

        Assert.True(modal >= 0, $"{methodName} must open the Generating popup.");
        Assert.True(start >= 0, $"{methodName} must still start the server job.");
        Assert.True(modal < start, $"{methodName} must open the modal before starting the server job.");
    }

    private static int FirstIndexOfAny(string src, params string[] needles)
    {
        var best = -1;
        foreach (var n in needles)
        {
            var i = src.IndexOf(n, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best))
                best = i;
        }
        return best;
    }

    private static string ExtractMethodBody(string fileName, string methodName)
    {
        var src = ReadPage(fileName);
        var sig = src.IndexOf($"Task {methodName}(", StringComparison.Ordinal);
        Assert.True(sig >= 0, $"Method {methodName} not found in {fileName}.");
        var brace = src.IndexOf('{', sig);
        Assert.True(brace >= 0, $"Opening brace for {methodName} not found.");
        var depth = 0;
        for (var i = brace; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return src[brace..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in {methodName}.");
    }

    private static string ReadPage(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
