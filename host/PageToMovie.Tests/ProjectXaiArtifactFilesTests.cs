using PageToMovie.Engine;

namespace PageToMovie.Tests;

public class ProjectXaiArtifactFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptm-xai-art-" + Guid.NewGuid().ToString("N"));

    public ProjectXaiArtifactFilesTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Reuses_file_id_when_sha_matches_and_not_expired()
    {
        var text = "Title: Test\n\nEXT. SEA - DAY\nWaves.";
        var sha = ProjectXaiArtifactFiles.Sha256Hex(text);
        ProjectXaiArtifactFiles.Upsert(_dir, new ProjectXaiArtifactFiles.Entry
        {
            Kind = ProjectXaiArtifactFiles.KindScreenplayMax,
            Sha256 = sha,
            FileId = "file-abc",
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400,
            Bytes = text.Length,
            Filename = "screenplay.max.fountain",
        });

        Assert.True(ProjectXaiArtifactFiles.TryGetReusable(
            _dir, ProjectXaiArtifactFiles.KindScreenplayMax, sha, out var hit));
        Assert.Equal("file-abc", hit!.FileId);
    }

    [Fact]
    public void Does_not_reuse_when_sha_changes()
    {
        ProjectXaiArtifactFiles.Upsert(_dir, new ProjectXaiArtifactFiles.Entry
        {
            Kind = ProjectXaiArtifactFiles.KindScreenplayMax,
            Sha256 = "aaa",
            FileId = "file-old",
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400,
        });

        Assert.False(ProjectXaiArtifactFiles.TryGetReusable(
            _dir, ProjectXaiArtifactFiles.KindScreenplayMax, "bbb", out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
