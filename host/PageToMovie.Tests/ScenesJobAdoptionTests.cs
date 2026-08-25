using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Every Scenes-page job start has to arm the lost-job watchdog. Sites used to assign the snapshot
/// and leave polling to the first hub JobUpdated — so the watchdog depended on the very transport
/// it exists to backstop, and a job that never reported left the Generating modal spinning on
/// "Queued batch gen…" while the server showed no active jobs at all.
/// </summary>
public class ScenesJobAdoptionTests
{
    [Theory]
    [InlineData("ScenesGeneration.cs")]
    [InlineData("ScenesClipRegen.cs")]
    public void Job_starts_go_through_AdoptStartedJob(string fileName)
    {
        var src = ReadPage(fileName);

        // A start site is recognisable by taking the snapshot straight from a start/poll call.
        Assert.DoesNotContain("_job = jobs?.Job;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_job = (await S.Engine.GetJobAsync())?.Job;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_job = await S.Engine.Start", src, StringComparison.Ordinal);
        Assert.Contains("AdoptStartedJob(", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptStartedJob_arms_polling()
    {
        var src = ReadPage("ScenesGeneration.cs");
        var body = src[src.IndexOf("void AdoptStartedJob(", StringComparison.Ordinal)..];
        var end = body.IndexOf("internal void StartJobPolling", StringComparison.Ordinal);
        Assert.True(end > 0, "AdoptStartedJob should sit directly above StartJobPolling");
        Assert.Contains("StartJobPolling();", body[..end], StringComparison.Ordinal);
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
