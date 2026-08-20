using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Renumber-on-reorder engine: blueprint order + numbers, every number-keyed file family,
/// sidecar/QA content fields, composite invalidation, the committed rename manifest, and the
/// screenplay-chunk permutation for scene reorder.
/// </summary>
public sealed class ProjectReorderTests
{
    // ---- clip reorder ----------------------------------------------------------------------

    [Fact]
    public async Task ReorderClips_renames_every_file_family_and_renumbers_blueprint()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2, 3 }));
        // Clip files for scene 1: sidecar takes, client markers, mp4s, history, trash, QA.
        fx.WriteVideoFile("scene_01_clip_01.mp4", "one");
        fx.WriteVideoFile("scene_01_clip_02.mp4", "two");
        fx.WriteVideoFile("scene_01_clip_03.mp4", "three");
        fx.WriteVideoFile("scene_01_clip_02_take_01.clip.json", """{"scene":1,"clip":2,"take":1}""");
        fx.WriteVideoFile("scene_01_clip_02_take_02.clip.json", """{"scene":1,"clip":2,"take":2}""");
        fx.WriteVideoFile("scene_01_clip_03.mp4.client.json", """{"ok":true}""");
        fx.WriteVideoFile("history/scene_01_clip_02_20260101120000.mp4", "old");
        fx.WriteVideoFile(".trash/scene_01_clip_03_take_01.clip.json", """{"scene":1,"clip":3}""");
        fx.WriteQaFile("scene_01_clip_02_dialogue_verification.json", """{"SceneNumber":1,"ClipNumber":2,"Status":"verified"}""");

        // New order: C01 stays, C03 and C02 swap.
        var result = fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 3, 2 });

        Assert.Equal(new[] { 1, 2, 3 }, fx.BlueprintClipNumbers(1));
        Assert.Equal("three", fx.ReadVideoFile("scene_01_clip_02.mp4"));
        Assert.Equal("two", fx.ReadVideoFile("scene_01_clip_03.mp4"));
        Assert.Equal("one", fx.ReadVideoFile("scene_01_clip_01.mp4"));
        // Takes followed their clip, and the sidecar CONTENT was renumbered too.
        var sidecar = JsonNode.Parse(fx.ReadVideoFile("scene_01_clip_03_take_01.clip.json"))!.AsObject();
        Assert.Equal(3, (int)sidecar["clip"]!.GetValue<int>());
        Assert.Equal(1, (int)sidecar["scene"]!.GetValue<int>());
        Assert.True(fx.VideoFileExists("scene_01_clip_03_take_02.clip.json"));
        Assert.True(fx.VideoFileExists("scene_01_clip_02.mp4.client.json"));
        Assert.True(fx.VideoFileExists("history/scene_01_clip_03_20260101120000.mp4"));
        Assert.True(fx.VideoFileExists(".trash/scene_01_clip_02_take_01.clip.json"));
        var qa = JsonNode.Parse(fx.ReadQaFile("scene_01_clip_03_dialogue_verification.json"))!.AsObject();
        Assert.Equal(3, (int)qa["ClipNumber"]!.GetValue<int>());
        // Old names are gone.
        Assert.False(fx.VideoFileExists("scene_01_clip_02_take_01.clip.json"));
        Assert.False(fx.QaFileExists("scene_01_clip_02_dialogue_verification.json"));

        Assert.Contains(result.MediaRenames, r =>
            r.From == "assets/video/scene_01_clip_02.mp4" && r.To == "assets/video/scene_01_clip_03.mp4");
        Assert.True(result.ManifestId > 0);
    }

    [Fact]
    public async Task ReorderClips_makes_gappy_numbers_contiguous()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 3, 7 })); // deletes left gaps
        fx.WriteVideoFile("scene_01_clip_07.mp4", "seven");

        fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 3, 7 }); // same order — just renumber

        Assert.Equal(new[] { 1, 2, 3 }, fx.BlueprintClipNumbers(1));
        Assert.Equal("seven", fx.ReadVideoFile("scene_01_clip_03.mp4"));
        Assert.False(fx.VideoFileExists("scene_01_clip_07.mp4"));
    }

    [Fact]
    public async Task ReorderClips_deletes_scene_composite_and_extend_markers()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2 }));
        fx.WriteVideoFile("scene_01.mp4", "composite");
        fx.WriteVideoFile("scene_01.mp4.sources.json", """{"sources":["scene_01_clip_01.mp4"]}""");
        fx.WriteVideoFile("_extend_src_s01c02.json", """{"file_id":"f1"}""");

        var result = fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 2, 1 });

        Assert.False(fx.VideoFileExists("scene_01.mp4"));
        Assert.False(fx.VideoFileExists("scene_01.mp4.sources.json"));
        Assert.False(fx.VideoFileExists("_extend_src_s01c02.json"));
        Assert.Contains("assets/video/scene_01.mp4.sources.json", result.MediaDeletes);
        Assert.Contains("assets/video/_extend_src_s01c02.json", result.MediaDeletes);
    }

    [Fact]
    public async Task ReorderClips_leaves_other_scenes_alone()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2 }), ("S2", new[] { 1, 2 }));
        fx.WriteVideoFile("scene_02_clip_01.mp4", "s2c1");
        fx.WriteVideoFile("scene_02_clip_02.mp4", "s2c2");

        fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 2, 1 });

        Assert.Equal("s2c1", fx.ReadVideoFile("scene_02_clip_01.mp4"));
        Assert.Equal("s2c2", fx.ReadVideoFile("scene_02_clip_02.mp4"));
        Assert.Equal(new[] { 1, 2 }, fx.BlueprintClipNumbers(2));
    }

    [Fact]
    public async Task ReorderClips_identity_order_with_contiguous_numbers_is_a_noop()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2 }));

        var result = fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 2 });

        Assert.Empty(result.MediaRenames);
        Assert.Equal(0, result.ManifestId);
        Assert.False(File.Exists(Path.Combine(fx.ProjectDir, ProjectStore.MediaRenamesManifestFileName)));
    }

    [Fact]
    public async Task ReorderClips_rejects_non_permutations()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2, 3 }));

        Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 2 }));
        Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 2, 2 }));
        Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 1, 2, 4 }));
        Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderClips(fx.ProjectId, 9, new[] { 1 }));
    }

    [Fact]
    public async Task ReorderClips_appends_manifest_entries_with_increasing_ids()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("S1", new[] { 1, 2 }));
        fx.WriteVideoFile("scene_01_clip_01.mp4", "a");
        fx.WriteVideoFile("scene_01_clip_02.mp4", "b");

        var first = fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 2, 1 });
        var second = fx.Store.ReorderClips(fx.ProjectId, 1, new[] { 2, 1 });

        Assert.Equal(1, first.ManifestId);
        Assert.Equal(2, second.ManifestId);
        var entries = fx.Store.ReadRenameManifest(fx.ProjectId);
        Assert.Equal(2, entries.Count);
        var afterFirst = fx.Store.ReadRenameManifest(fx.ProjectId, afterId: 1);
        Assert.Single(afterFirst);
        Assert.Equal("reorder_clips", afterFirst[0]["op"]!.GetValue<string>());
        // Two swaps returned the bytes to their original names.
        Assert.Equal("a", fx.ReadVideoFile("scene_01_clip_01.mp4"));
    }

    // ---- scene reorder ---------------------------------------------------------------------

    [Fact]
    public async Task ReorderScenes_renames_files_and_permutes_screenplay_chunks()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("EXT. LANE - DAY", new[] { 1 }), ("INT. SCHOOL - DAY", new[] { 1, 2 }));
        fx.WriteScreenplay("""
            Title: Mary

            EXT. LANE - DAY

            Mary walks.

            INT. SCHOOL - DAY

            NARRATOR
            It followed her to school one day.
            """);
        fx.WriteVideoFile("scene_01_clip_01.mp4", "lane");
        fx.WriteVideoFile("scene_02_clip_01.mp4", "school1");
        fx.WriteVideoFile("scene_02_clip_02_take_01.clip.json", """{"scene":2,"clip":2}""");
        fx.WriteMusicFile("scene_02.meta.json", """{"scene":2}""");
        fx.WriteQaFile("scene_02_clip_01_dialogue_verification.json", """{"SceneNumber":2,"ClipNumber":1}""");

        fx.Store.ReorderScenes(fx.ProjectId, new[] { 2, 1 });

        Assert.Equal(new[] { 1, 2 }, fx.BlueprintSceneNumbers());
        Assert.Equal("INT. SCHOOL - DAY", fx.BlueprintSceneSetting(1));
        Assert.Equal("school1", fx.ReadVideoFile("scene_01_clip_01.mp4"));
        Assert.Equal("lane", fx.ReadVideoFile("scene_02_clip_01.mp4"));
        var sidecar = JsonNode.Parse(fx.ReadVideoFile("scene_01_clip_02_take_01.clip.json"))!.AsObject();
        Assert.Equal(1, sidecar["scene"]!.GetValue<int>());
        Assert.True(fx.MusicFileExists("scene_01.meta.json"));
        var qa = JsonNode.Parse(fx.ReadQaFile("scene_01_clip_01_dialogue_verification.json"))!.AsObject();
        Assert.Equal(1, qa["SceneNumber"]!.GetValue<int>());

        var draft = fx.ReadScreenplay();
        Assert.StartsWith("Title: Mary", draft);
        Assert.True(draft.IndexOf("INT. SCHOOL", StringComparison.Ordinal)
                    < draft.IndexOf("EXT. LANE", StringComparison.Ordinal));
        Assert.Contains("It followed her to school one day.", draft);
    }

    [Fact]
    public async Task ReorderScenes_refuses_when_screenplay_scene_count_disagrees()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("EXT. A - DAY", new[] { 1 }), ("EXT. B - DAY", new[] { 1 }));
        fx.WriteScreenplay("""
            EXT. A - DAY

            Only one scene here.
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderScenes(fx.ProjectId, new[] { 2, 1 }));
        Assert.Contains("shot plan", ex.Message);
        // Nothing moved.
        Assert.Equal(new[] { 1, 2 }, fx.BlueprintSceneNumbers());
    }

    [Fact]
    public async Task ReorderScenes_keeps_credits_last_and_tolerates_missing_credits_heading()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        fx.WriteBlueprint(("EXT. A - DAY", new[] { 1 }), ("EXT. B - DAY", new[] { 1 }), ("CREDITS", new[] { 1 }));
        fx.WriteScreenplay("""
            EXT. A - DAY

            Alpha.

            EXT. B - DAY

            Beta.
            """);

        // Credits anywhere but last is refused.
        Assert.Throws<InvalidOperationException>(() => fx.Store.ReorderScenes(fx.ProjectId, new[] { 3, 1, 2 }));

        // Valid: swap the two real scenes, credits stays last (it has no Fountain chunk).
        fx.Store.ReorderScenes(fx.ProjectId, new[] { 2, 1, 3 });
        Assert.Equal("EXT. B - DAY", fx.BlueprintSceneSetting(1));
        Assert.Equal("CREDITS", fx.BlueprintSceneSetting(3));
        var draft = fx.ReadScreenplay();
        Assert.True(draft.IndexOf("EXT. B", StringComparison.Ordinal) < draft.IndexOf("EXT. A", StringComparison.Ordinal));
    }

    // ---- helpers under test directly ----------------------------------------------------------

    [Theory]
    [InlineData("scene_01_clip_02.mp4", "scene_01_clip_05.mp4")]
    [InlineData("scene_01_clip_02_take_03.clip.json", "scene_01_clip_05_take_03.clip.json")]
    [InlineData("scene_01_clip_02.mp4.client.json", "scene_01_clip_05.mp4.client.json")]
    [InlineData("scene_01_clip_02_20260101.mp4", "scene_01_clip_05_20260101.mp4")]
    public void MapClipFileName_renames_number_keyed_names(string from, string expected)
    {
        var map = new Dictionary<int, int> { [2] = 5 };
        Assert.Equal(expected, ProjectStore.MapClipFileName(from, 1, map));
    }

    [Theory]
    [InlineData("scene_02_clip_02.mp4")]      // different scene
    [InlineData("scene_01_clip_020.mp4")]     // clip 20, not clip 2 — digit boundary
    [InlineData("scene_01.mp4")]              // scene composite, no clip part
    [InlineData("workspace.json")]
    public void MapClipFileName_leaves_everything_else_alone(string name)
    {
        var map = new Dictionary<int, int> { [2] = 5 };
        Assert.Null(ProjectStore.MapClipFileName(name, 1, map));
    }

    [Theory]
    [InlineData("scene_02_clip_01.mp4", "scene_07_clip_01.mp4")]
    [InlineData("scene_02.meta.json", "scene_07.meta.json")]
    [InlineData("scene_02.mp4.sources.json", "scene_07.mp4.sources.json")]
    [InlineData("scene_02_seg_01.wav", "scene_07_seg_01.wav")]
    [InlineData("_extend_src_s02c03.json", "_extend_src_s07c03.json")]
    public void MapSceneFileName_renames_number_keyed_names(string from, string expected)
    {
        var map = new Dictionary<int, int> { [2] = 7 };
        Assert.Equal(expected, ProjectStore.MapSceneFileName(from, map));
    }

    [Fact]
    public void MapSceneFileName_respects_digit_boundaries()
    {
        var map = new Dictionary<int, int> { [2] = 7 };
        Assert.Null(ProjectStore.MapSceneFileName("scene_020_clip_01.mp4", map));
        Assert.Null(ProjectStore.MapSceneFileName("scene_03_clip_02.mp4", map));
    }

    [Fact]
    public void SplitFountainSceneChunks_roundtrips_text()
    {
        var text = "Title: T\n\nEXT. A - DAY\n\nAlpha.\n\nINT. B - NIGHT\n\nBeta.\n";
        var chunks = ProjectStore.SplitFountainSceneChunks(text, out var prefix);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(text, prefix + string.Concat(chunks));
        Assert.StartsWith("EXT. A", chunks[0]);
        Assert.StartsWith("INT. B", chunks[1]);
    }

    // ---- registry mirroring -------------------------------------------------------------------

    [Fact]
    public async Task Registry_RenamePathsAsync_handles_swap_cycles_and_deletes()
    {
        await using var fx = await ReorderFixture.CreateAsync();
        var registry = new MediaRegistryService(Options.Create(new PageToMovieOptions { WorkspaceRoot = fx.Root }));
        await registry.UpsertAsync(fx.ProjectId, "assets/video/scene_01_clip_01.mp4", MediaRegistryService.HashBytes(new byte[] { 1 }), 10, "clip", 1, 1, "alice");
        await registry.UpsertAsync(fx.ProjectId, "assets/video/scene_01_clip_02.mp4", MediaRegistryService.HashBytes(new byte[] { 2 }), 20, "clip", 1, 2, "alice");
        await registry.UpsertAsync(fx.ProjectId, "assets/video/scene_01.mp4.sources.json", MediaRegistryService.HashBytes(new byte[] { 3 }), 5, "clip", 1, null, "alice");

        await registry.RenamePathsAsync(fx.ProjectId,
            new[]
            {
                new MediaRenameEntry("assets/video/scene_01_clip_01.mp4", "assets/video/scene_01_clip_02.mp4"),
                new MediaRenameEntry("assets/video/scene_01_clip_02.mp4", "assets/video/scene_01_clip_01.mp4"),
            },
            new[] { "assets/video/scene_01.mp4.sources.json" });

        var one = await registry.TryGetAsync(fx.ProjectId, "assets/video/scene_01_clip_01.mp4");
        var two = await registry.TryGetAsync(fx.ProjectId, "assets/video/scene_01_clip_02.mp4");
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal(20, one!.SizeBytes);  // the old clip_02 row now sits at clip_01
        Assert.Equal(10, two!.SizeBytes);
        Assert.Equal(1, one.Clip);
        Assert.Equal(2, two.Clip);
        Assert.Null(await registry.TryGetAsync(fx.ProjectId, "assets/video/scene_01.mp4.sources.json"));
    }

    // ---- fixture --------------------------------------------------------------------------------

    private sealed class ReorderFixture : IAsyncDisposable
    {
        public string Root { get; private init; } = "";
        public ProjectStore Store { get; private init; } = null!;
        public string ProjectId { get; private init; } = "";
        public string ProjectDir { get; private init; } = "";

        public static async Task<ReorderFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ptm_reorder_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "projects"));
            var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = root }));
            var project = await store.CreateProjectAsync("ReorderBook", ownerUserId: "alice");
            var projectDir = await store.GetProjectDirAsync(project.Id);
            return new ReorderFixture { Root = root, Store = store, ProjectId = project.Id, ProjectDir = projectDir };
        }

        public void WriteBlueprint(params (string Setting, int[] Clips)[] scenes)
        {
            var sceneArr = new JsonArray();
            for (var s = 0; s < scenes.Length; s++)
            {
                var clipArr = new JsonArray();
                foreach (var c in scenes[s].Clips)
                {
                    clipArr.Add(new JsonObject
                    {
                        ["clip_number"] = c,
                        ["dialogue"] = $"line s{s + 1} c{c}",
                    });
                }
                sceneArr.Add(new JsonObject
                {
                    ["scene_number"] = s + 1,
                    ["setting"] = scenes[s].Setting,
                    ["scene_heading"] = scenes[s].Setting,
                    ["veo_clips"] = clipArr,
                });
            }
            var root = new JsonObject { ["scenes"] = sceneArr };
            File.WriteAllText(Path.Combine(ProjectDir, "blueprint.clips.grok.json"), root.ToJsonString());
        }

        public void WriteScreenplay(string fountain)
        {
            var dir = Path.Combine(ProjectDir, "source");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "screenplay.fountain"), fountain.Replace("\r\n", "\n"));
        }

        public string ReadScreenplay() =>
            File.ReadAllText(Path.Combine(ProjectDir, "source", "screenplay.fountain"));

        public void WriteVideoFile(string name, string content)
        {
            var path = Path.Combine(ProjectDir, "assets", "video", name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteMusicFile(string name, string content)
        {
            var path = Path.Combine(ProjectDir, "assets", "music", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteQaFile(string name, string content)
        {
            var path = Path.Combine(ProjectDir, "assets", "qa", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public bool VideoFileExists(string name) =>
            File.Exists(Path.Combine(ProjectDir, "assets", "video", name.Replace('/', Path.DirectorySeparatorChar)));

        public bool MusicFileExists(string name) => File.Exists(Path.Combine(ProjectDir, "assets", "music", name));
        public bool QaFileExists(string name) => File.Exists(Path.Combine(ProjectDir, "assets", "qa", name));

        public string ReadVideoFile(string name) =>
            File.ReadAllText(Path.Combine(ProjectDir, "assets", "video", name.Replace('/', Path.DirectorySeparatorChar)));

        public string ReadQaFile(string name) => File.ReadAllText(Path.Combine(ProjectDir, "assets", "qa", name));

        public int[] BlueprintClipNumbers(int scene)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectDir, "blueprint.clips.grok.json")))!.AsObject();
            var s = root["scenes"]!.AsArray().OfType<JsonObject>()
                .First(x => x["scene_number"]!.GetValue<int>() == scene);
            return s["veo_clips"]!.AsArray().OfType<JsonObject>()
                .Select(c => c["clip_number"]!.GetValue<int>()).ToArray();
        }

        public int[] BlueprintSceneNumbers()
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectDir, "blueprint.clips.grok.json")))!.AsObject();
            return root["scenes"]!.AsArray().OfType<JsonObject>()
                .Select(s => s["scene_number"]!.GetValue<int>()).ToArray();
        }

        public string BlueprintSceneSetting(int scene)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectDir, "blueprint.clips.grok.json")))!.AsObject();
            return root["scenes"]!.AsArray().OfType<JsonObject>()
                .First(x => x["scene_number"]!.GetValue<int>() == scene)["setting"]!.GetValue<string>();
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            try { Directory.Delete(Root, recursive: true); } catch { /* ignore */ }
        }
    }
}
