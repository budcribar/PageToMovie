using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A take card showed "Dec 31, 5:00 PM" for a clip generated that afternoon. The date came from the
/// mp4's last-write time, which records when those bytes last landed on this disk — rewritten by a
/// restore, a folder sync or a plain copy, and zeroed outright by a zip restore to the DOS epoch.
/// The display format omits the year, so 1979 read as a plausible evening in December. The sidecar
/// had the real time in created_at_utc the whole time and nothing was reading it.
/// </summary>
public class ClipTakeCreatedDateTests
{
    private sealed class Project : IDisposable
    {
        public string Root { get; }
        public string VideoDir { get; }
        public ProjectStore Store { get; }

        public Project()
        {
            Root = Path.Combine(Path.GetTempPath(), "fs_take_date_" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(Root, "projects", "Demo");
            VideoDir = Path.Combine(dir, "assets", "video");
            Directory.CreateDirectory(VideoDir);
            File.WriteAllText(Path.Combine(dir, "project.json"), """{"id":"Demo"}""");
            Store = new ProjectStore(Options.Create(
                new PageToMovieOptions { WorkspaceRoot = Root, EnableReadCaches = false }));
        }

        public void WriteTake(int scene, int clip, int take, string? createdAtUtc, DateTime mtimeUtc)
        {
            var stem = $"scene_{scene:D2}_clip_{clip:D2}_take_{take:D2}";
            var mp4 = Path.Combine(VideoDir, stem + ".mp4");
            File.WriteAllBytes(mp4, new byte[4096]);
            File.SetLastWriteTimeUtc(mp4, mtimeUtc);
            var created = createdAtUtc is null ? "" : $"""
                ,"created_at_utc":"{createdAtUtc}"
                """;
            File.WriteAllText(
                Path.Combine(VideoDir, stem + ".clip.json"),
                $$"""{"schema_version":"clip_sidecar.v1","scene":{{scene}},"clip":{{clip}},"take":{{take}},"model":"grok-imagine-video"{{created}}}""");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task The_sidecar_time_wins_over_a_last_write_time_that_was_rewritten()
    {
        using var p = new Project();
        // The DOS/zip epoch a restore leaves behind — this is the "Dec 31" the operator saw.
        p.WriteTake(14, 2, 1, "2026-09-02T21:25:54.7027594Z", new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var versions = await p.Store.GetClipVersionsAsync("Demo", 14, 2);
        var take = Assert.Single(versions);

        Assert.Equal(new DateTime(2026, 9, 2, 21, 25, 54, DateTimeKind.Utc), take.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_sidecar_with_no_recorded_time_still_falls_back_to_the_file()
    {
        using var p = new Project();
        var mtime = new DateTime(2026, 8, 20, 10, 30, 0, DateTimeKind.Utc);
        p.WriteTake(14, 2, 1, createdAtUtc: null, mtimeUtc: mtime);

        var versions = await p.Store.GetClipVersionsAsync("Demo", 14, 2);
        var take = Assert.Single(versions);

        // Better than nothing when the sidecar predates the field — just never preferred over it.
        Assert.Equal(mtime, take.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cards_order_by_when_the_take_was_made_not_by_when_the_bytes_landed()
    {
        using var p = new Project();
        // Oldest take, but its bytes were re-written most recently — a restore or a folder sync.
        p.WriteTake(14, 2, 1, "2026-09-02T21:00:00Z", new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));
        p.WriteTake(14, 2, 2, "2026-09-02T21:17:00Z", new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        p.WriteTake(14, 2, 3, "2026-09-02T21:18:00Z", new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var versions = await p.Store.GetClipVersionsAsync("Demo", 14, 2);

        // Newest first. Sorting on the file time put the freshly restored take at the top and the
        // two real ones behind it, dated 1979.
        Assert.Equal(new[] { 3, 2, 1 }, versions.Select(v => v.Take).ToArray());
    }

    [Fact]
    public async Task Selecting_a_take_whose_video_is_gone_says_where_it_is_not()
    {
        using var p = new Project();
        p.WriteTake(14, 2, 1, "2026-09-02T21:25:54Z", DateTime.UtcNow);
        p.WriteTake(14, 2, 2, "2026-09-02T21:30:00Z", DateTime.UtcNow);
        // Take 2 is listed by its sidecar but its bytes are nowhere: no file here, no client
        // marker, no provider url. The card still renders a working-looking button.
        File.Delete(Path.Combine(p.VideoDir, "scene_14_clip_02_take_02.mp4"));
        // Pin take 1 as active, or the highest-numbered take is picked by default and the one
        // under test is already current.
        File.WriteAllText(
            Path.Combine(p.VideoDir, "scene_14_clip_02.current.json"),
            """{"scene":14,"clip":2,"take":1}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.Store.PromoteClipVersionAsync("Demo", 14, 2, "scene_14_clip_02_take_02.mp4"));

        // "Failed to promote clip version." is indistinguishable from a dead button.
        Assert.Contains("Take 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("your device", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
