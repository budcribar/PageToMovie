using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Promoting a take writes a pointer — it never needs the bytes. The existence check is only there
/// so a take nobody can play cannot be made current. It used to accept two kinds of evidence, the
/// server's own copy and a provider file id, and a take that had neither could not be selected at
/// all: the request answered 400 and the UI silently kept the take it had. That is what happened to
/// Mary19 S01C02 take 7 — the only take of that clip whose narration says the opening word — after
/// the server pruned its copy. The browser still had it.
/// </summary>
public sealed class ClipVersionPromoteTests : IDisposable
{
    private readonly string _root;
    private readonly string _videoDir;
    private readonly ProjectStore _store;
    private const string ProjectId = "Demo";

    public ClipVersionPromoteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_promote_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(_root, "projects", ProjectId);
        _videoDir = Path.Combine(proj, "assets", "video");
        Directory.CreateDirectory(_videoDir);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json","model_name":"grok-imagine-video"}""");
        _store = new ProjectStore(Options.Create(
            new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* */ }
    }

    private string Path_(string name) => Path.Combine(_videoDir, name);

    /// <summary>A take the server holds the bytes for.</summary>
    private void WriteServerTake(int take)
    {
        File.WriteAllBytes(Path_($"scene_01_clip_02_take_{take:D2}.mp4"), new byte[4096]);
        File.WriteAllText(Path_($"scene_01_clip_02_take_{take:D2}.clip.json"),
            $$"""{"schema_version":"clip_sidecar.v1","scene":1,"clip":2,"take":{{take}},"duration_seconds":5}""");
    }

    /// <summary>
    /// A take whose bytes the server no longer has — only the marker saying the browser synced it,
    /// and a sidecar with no provider copy to fall back on.
    /// </summary>
    private void WriteClientOnlyTake(int take)
    {
        File.WriteAllText(Path_($"scene_01_clip_02_take_{take:D2}.clip.json"),
            $$"""{"schema_version":"clip_sidecar.v1","scene":1,"clip":2,"take":{{take}},"duration_seconds":5}""");
        File.WriteAllText(Path_($"scene_01_clip_02_take_{take:D2}.mp4.client.json"),
            """{"sha256":"abc","size":4096}""");
    }

    private int CurrentTake() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path_("scene_01_clip_02.current.json")))
            .RootElement.GetProperty("take").GetInt32();

    [Fact]
    public async Task A_take_the_server_still_holds_can_be_promoted()
    {
        WriteServerTake(1);
        WriteServerTake(2);

        var ok = await _store.PromoteClipVersionAsync(ProjectId, 1, 2, "scene_01_clip_02_take_01.mp4");

        Assert.True(ok);
        Assert.Equal(1, CurrentTake());
    }

    /// <summary>The regression: pruned from the server, still on the client, still selectable.</summary>
    [Fact]
    public async Task A_take_only_the_browser_holds_can_still_be_promoted()
    {
        WriteClientOnlyTake(7);
        WriteServerTake(11);

        var ok = await _store.PromoteClipVersionAsync(ProjectId, 1, 2, "scene_01_clip_02_take_07.mp4");

        Assert.True(ok, "a take the browser can play must be selectable even after the server prunes its copy");
        Assert.Equal(7, CurrentTake());
    }

    /// <summary>
    /// The guard still does its job: no bytes anywhere, no provider copy, no client marker — that
    /// take cannot be made current, because nothing could play it.
    /// </summary>
    [Fact]
    public async Task A_take_nobody_holds_is_still_refused()
    {
        File.WriteAllText(Path_("scene_01_clip_02_take_07.clip.json"),
            """{"schema_version":"clip_sidecar.v1","scene":1,"clip":2,"take":7,"duration_seconds":5}""");
        WriteServerTake(11);

        var ok = await _store.PromoteClipVersionAsync(ProjectId, 1, 2, "scene_01_clip_02_take_07.mp4");

        Assert.False(ok);
    }

    /// <summary>A provider copy remains sufficient on its own — that path is unchanged.</summary>
    [Fact]
    public async Task A_take_the_provider_still_holds_can_be_promoted()
    {
        File.WriteAllText(Path_("scene_01_clip_02_take_07.clip.json"),
            """{"schema_version":"clip_sidecar.v1","scene":1,"clip":2,"take":7,"duration_seconds":5,"source_file_id":"file_abc"}""");
        WriteServerTake(11);

        var ok = await _store.PromoteClipVersionAsync(ProjectId, 1, 2, "scene_01_clip_02_take_07.mp4");

        Assert.True(ok);
        Assert.Equal(7, CurrentTake());
    }
}
