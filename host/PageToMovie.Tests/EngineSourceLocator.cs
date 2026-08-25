namespace PageToMovie.Tests;

/// <summary>
/// Reads product source text for the handful of architecture guards that assert on call shape
/// rather than behaviour (see <see cref="JobTerminalPublishTests"/>). Mirrors the lookup used by
/// <see cref="AdaptationModuleBoundaryTests"/>: walk up from the test binary until the file is found.
/// </summary>
internal static class EngineSourceLocator
{
    public static string ReadEngineSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "host", "PageToMovie.Engine", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            // When tests run from host/PageToMovie.Tests/bin/...
            candidate = Path.Combine(dir.FullName, "PageToMovie.Engine", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"{fileName} not found from test base directory.", fileName);
    }
}
