using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Validation;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.Tests;

/// <summary>
/// Architecture guards for <c>PageToMovie.Adaptation</c> (plan A6.1–A6.2): pure Stage‑1 module must not
/// reference Engine (or other product I/O hosts) or contain ProjectStore / product path symbols.
/// </summary>
public sealed class AdaptationModuleBoundaryTests
{
    [Fact]
    public void Adaptation_assembly_does_not_reference_Engine()
    {
        var assembly = typeof(AdaptationVersion).Assembly;
        var refs = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.Length > 0)
            .ToArray();

        Assert.DoesNotContain(refs, n =>
            string.Equals(n, "PageToMovie.Engine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, n =>
            n.StartsWith("PageToMovie.Engine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Adaptation_csproj_does_not_reference_Engine()
    {
        var csprojPath = FindAdaptationCsproj();
        Assert.True(File.Exists(csprojPath), $"Missing csproj at {csprojPath}");

        var doc = XDocument.Load(csprojPath);
        var refs = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Where(s => s.Length > 0)
            .ToArray();

        Assert.DoesNotContain(refs, r =>
            r.Contains("PageToMovie.Engine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r =>
            r.Contains("PageToMovie.Api", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r =>
            r.Contains("PageToMovie.Web", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r =>
            r.Contains("PageToMovie.Fakes", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(refs, r =>
            r.Contains("PageToMovie.Core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Adaptation_sources_do_not_mention_ProjectStore_or_product_paths()
    {
        var root = FindAdaptationSourceRoot();
        Assert.True(Directory.Exists(root), $"Missing Adaptation sources at {root}");

        // Forbidden product I/O symbols (plan A6.2). Allow the words only in comments that say "no …".
        var forbidden = new Regex(@"\b(ProjectStore|IProjectStore|SQLite|YouTubeAuth|FilmJobService)\b", RegexOptions.Compiled, CommonRegex.Timeout);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                // Doc comments that forbid the symbol are OK (e.g. "No ProjectStore").
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith('*'))
                    continue;
                if (!forbidden.IsMatch(line))
                    continue;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Adaptation must not reference product I/O types:\n" + string.Join("\n", offenders.Take(20)));
    }

    [Fact]
    public void CastPackageCrossCheck_lives_in_Adaptation_assembly()
    {
        var asm = typeof(CastPackageCrossCheck).Assembly;
        Assert.Equal("PageToMovie.Adaptation", asm.GetName().Name);
    }

    [Fact]
    public void AdaptationVersion_Current_is_stable_short_hex()
    {
        var id = AdaptationVersion.Current;
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
        Assert.Equal(id, AdaptationVersion.ComputeId());
    }

    private static string FindAdaptationCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "host", "PageToMovie.Adaptation", "PageToMovie.Adaptation.csproj");
            if (File.Exists(candidate))
                return candidate;
            // When tests run from host/PageToMovie.Tests/bin/...
            candidate = Path.Combine(dir.FullName, "PageToMovie.Adaptation", "PageToMovie.Adaptation.csproj");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("PageToMovie.Adaptation.csproj not found from test base directory.");
    }

    private static string FindAdaptationSourceRoot()
    {
        var csproj = FindAdaptationCsproj();
        return Path.GetDirectoryName(csproj)
               ?? throw new DirectoryNotFoundException(csproj);
    }
}
