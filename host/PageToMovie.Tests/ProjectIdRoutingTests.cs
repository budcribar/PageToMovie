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
}
