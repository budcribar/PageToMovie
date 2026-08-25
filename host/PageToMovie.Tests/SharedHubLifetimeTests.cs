using System.Text.RegularExpressions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// JobHubClient is an app-wide singleton owned by DI. A page that disposes it latches _disposed,
/// after which every StartAsync/EnsureStartedAsync returns immediately and the socket never comes
/// back — and ClientMediaFolderService.EnsureHubHookAsync latches _hubHooked on first call, so it
/// never re-subscribes either. From then on no job's generated media reaches the local folder,
/// while the API host drops its own copy once ClientMediaUrl is published, so the clips are lost.
///
/// This has now happened twice: once on /admin, and again on Home — where it was worse, because
/// Home is the landing page, so merely navigating away from it killed live updates for the rest of
/// the session. A comment on Admin.razor.cs explaining the hazard did not stop the second one.
/// </summary>
public class SharedHubLifetimeTests
{
    [Fact]
    public void No_page_disposes_the_shared_hub_client()
    {
        var pages = Directory.GetFiles(WebComponentsDir(), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(WebComponentsDir(), "*.razor", SearchOption.AllDirectories));

        var offenders = new List<string>();
        foreach (var file in pages)
        {
            // Strip comments first: the notes warning against this on Admin and Home spell the
            // call out verbatim, and matching those would leave the guard permanently red.
            var text = StripComments(File.ReadAllText(file));
            // Any receiver named like the hub client — Hub, _hub, JobHub — being disposed.
            if (Regex.IsMatch(text, @"\b_?(job)?hub\w*\s*\.\s*DisposeAsync\s*\(", RegexOptions.IgnoreCase))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            "These pages dispose the shared JobHubClient, which kills live job updates app-wide " +
            "for the rest of the session. Unsubscribe from its events instead: " +
            string.Join(", ", offenders));
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
                lines[i] = lines[i][..idx];
        }
        return string.Join("\n", lines);
    }

    private static string WebComponentsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "PageToMovie.Web")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "PageToMovie.Web", "Components");
    }
}
