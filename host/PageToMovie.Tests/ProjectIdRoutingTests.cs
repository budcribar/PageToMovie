using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectIdRoutingTests
{
    [Theory]
    [InlineData("Mary3", "Mary3")]
    [InlineData("budcribar/Mary3", "budcribar/Mary3")]
    [InlineData("budcribar%2FMary3", "budcribar/Mary3")]
    [InlineData("alice/Buster", "alice/Buster")]
    public void ToUrlPath_keeps_slash_as_a_path_separator(string input, string expected) =>
        Assert.Equal(expected, ProjectIdRouting.ToUrlPath(input));

    [Fact]
    public void ProjectApi_prefixes_owner_name_without_encoding_the_slash()
    {
        Assert.Equal("/api/projects/budcribar/Mary3", ProjectIdRouting.ProjectApi("budcribar/Mary3"));
        Assert.DoesNotContain("%2F", ProjectIdRouting.ProjectApi("budcribar/Mary3"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/projects/budcribar/Mary3/activate", "/api/projects/budcribar%2FMary3/activate")]
    [InlineData("/api/projects/budcribar/Mary3/presence/heartbeat", "/api/projects/budcribar%2FMary3/presence/heartbeat")]
    [InlineData("/api/projects/budcribar/Mary3/presence", "/api/projects/budcribar%2FMary3/presence")]
    [InlineData("/api/projects/budcribar/Mary3/scenes/1/clips", "/api/projects/budcribar%2FMary3/scenes/1/clips")]
    [InlineData("/api/projects/budcribar/Annette/video-preset-voices", "/api/projects/budcribar%2FAnnette/video-preset-voices")]
    [InlineData("/api/projects/budcribar/Mary3", "/api/projects/budcribar%2FMary3")]
    [InlineData("/api/admin/projects/budcribar/Mary3/export", "/api/admin/projects/budcribar%2FMary3/export")]
    public void TryRewriteRequestPath_collapses_owner_name_for_routing(string path, string expected)
    {
        Assert.True(ProjectIdRouting.TryRewriteRequestPath(path, out var rewritten));
        Assert.Equal(expected, rewritten);
    }

    [Theory]
    [InlineData("/api/projects/Mary3/activate")]
    [InlineData("/api/projects/Mary3/presence/heartbeat")]
    [InlineData("/api/projects/budcribar%2FMary3/activate")]
    [InlineData("/api/projects/budcribar%2FMary3/presence")]
    [InlineData("/api/projects")]
    [InlineData("/api/projects/import")]
    [InlineData("/api/projects/forkable")]
    [InlineData("/api/admin/projects/model-selections")]
    [InlineData("/health")]
    public void TryRewriteRequestPath_leaves_single_segment_and_collection_urls(string path) =>
        Assert.False(ProjectIdRouting.TryRewriteRequestPath(path, out _));

    [Theory]
    [InlineData("/api/projects/budcribar/Mary3/activate", "budcribar/Mary3")]
    [InlineData("/api/projects/budcribar%2FMary3/activate", "budcribar/Mary3")]
    [InlineData("/api/projects/budcribar/Mary3/presence/heartbeat", "budcribar/Mary3")]
    [InlineData("/api/projects/budcribar/Annette/video-preset-voices", "budcribar/Annette")]
    [InlineData("/api/projects/Mary3/activate", "Mary3")]
    [InlineData("/api/projects/Mary3/presence", "Mary3")]
    public void TryExtractProjectId_accepts_both_slash_forms(string path, string expected)
    {
        Assert.True(ProjectIdRouting.TryExtractProjectId(path, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/projects/")]
    [InlineData("/api/projects/import")]
    [InlineData("/api/projects/forkable")]
    public void TryExtractProjectId_skips_collection_urls(string path) =>
        Assert.False(ProjectIdRouting.TryExtractProjectId(path, out _));

    [Theory]
    [InlineData(403, true)]
    [InlineData(401, true)]
    [InlineData(200, false)]
    [InlineData(500, false)]
    [InlineData(null, false)]
    public void ShouldStopPresencePolling_only_on_access_denied(int? status, bool stop) =>
        Assert.Equal(stop, ProjectIdRouting.ShouldStopPresencePolling(status));

    /// <summary>
    /// Cheap source scan: every Map* <c>/api/projects/{id}/X</c> first segment must be in
    /// <c>ResourceSegments</c>. Misses (like video-preset-voices) 404 for owner/Name ids.
    /// Unused allowlist entries are fine — do not require the reverse.
    /// </summary>
    [Fact]
    public void Mapped_project_resource_segments_are_allowlisted()
    {
        var routingSource = ReadRepoFile("host", "PageToMovie.Core", "Utils", "ProjectIdRouting.cs");
        var allowlist = ExtractResourceSegments(routingSource);
        Assert.Contains("video-preset-voices", allowlist);

        var mapped = ScanMappedProjectResources();
        var missing = mapped.Where(s => !allowlist.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "Add each new /api/projects/{id}/X first segment to ProjectIdRouting.ResourceSegments. Missing: "
            + string.Join(", ", missing));
    }

    private static readonly Regex ResourceSegmentLiteral = new(
        @"^\s+""([a-z0-9-]+)"",?\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex MappedProjectResource = new(
        @"/api/projects/\{(?:id|projectId)\}/([a-z0-9-]+)",
        RegexOptions.CultureInvariant);

    private static HashSet<string> ExtractResourceSegments(string routingSource)
    {
        var start = routingSource.IndexOf("ResourceSegments", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResourceSegments not found in ProjectIdRouting.cs");
        var blockStart = routingSource.IndexOf('{', start);
        var blockEnd = routingSource.IndexOf('}', blockStart);
        var block = routingSource[blockStart..blockEnd];
        return ResourceSegmentLiteral.Matches(block)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ScanMappedProjectResources()
    {
        var apiDir = FindRepoPath("host", "PageToMovie.Api");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in MappedProjectResource.Matches(File.ReadAllText(file)))
                found.Add(match.Groups[1].Value);
        }

        return found;
    }

    private static string ReadRepoFile(params string[] relativeParts) =>
        File.ReadAllText(FindRepoPath(relativeParts));

    private static string FindRepoPath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join('/', relativeParts));
    }
}
