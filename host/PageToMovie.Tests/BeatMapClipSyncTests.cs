using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.ModelExecution;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// <c>stage1_beat_map</c> is one entry per clip, in clip order, and only the planner used to write
/// it — so deleting a clip in the Scenes editor left its beat id behind and the next Stage 2 replan
/// failed <see cref="Stage2AggregateValidator"/>'s beat/clip 1:1 rule for a scene nobody had asked
/// to replan. Mary19 scene 3 sat at 7 clips against 8 beats: a one-scene replan of scene 1 was
/// rejected outright, and the only thing that cleared it was a replan wide enough to cover scene 3,
/// which brought the deleted clip back.
/// </summary>
public sealed class BeatMapClipSyncTests
{
    private const string Blueprint = """
        {
          "scenes": [
            {
              "scene_number": 1,
              "stage1_beat_map": ["sb_one", "sb_two", "sb_three"],
              "veo_clips": [
                { "clip_number": 1, "visual_prompt": "clip one", "stage1_beat_id": "sb_one" },
                { "clip_number": 2, "visual_prompt": "clip two", "stage1_beat_id": "sb_two" },
                { "clip_number": 3, "visual_prompt": "clip three", "stage1_beat_id": "sb_three" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Deleting_a_clip_drops_its_beat_from_the_map()
    {
        using var p = new Project(Blueprint);

        p.Store.DeleteClip("Demo", scene: 1, clip: 2);

        Assert.Equal(new[] { "sb_one", "sb_three" }, p.BeatMap());
        Assert.Equal(new[] { "sb_one", "sb_three" }, p.ClipBeatIds());
        Assert.Empty(Validate(p));
    }

    /// <summary>AddClip keeps clips sorted by number, so a filled gap lands mid-array.</summary>
    [Fact]
    public void Adding_a_clip_into_a_gap_puts_its_beat_at_the_same_position()
    {
        using var p = new Project("""
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "stage1_beat_map": ["sb_one", "sb_four"],
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "one", "stage1_beat_id": "sb_one" },
                    { "clip_number": 4, "visual_prompt": "four", "stage1_beat_id": "sb_four" }
                  ]
                }
              ]
            }
            """);

        p.Store.AddClip("Demo", scene: 1, new ClipEditRequest
        {
            Scene = 1,
            Clip = 2,
            VisualPrompt = "inserted",
        });

        var map = p.BeatMap();
        Assert.Equal(3, map.Count);
        Assert.Equal("sb_one", map[0]);
        Assert.False(string.IsNullOrWhiteSpace(map[1]));
        Assert.Equal("sb_four", map[2]);
        Assert.Equal(p.ClipBeatIds(), map);
        Assert.Empty(Validate(p));
    }

    [Fact]
    public void Added_clip_at_the_end_extends_the_map()
    {
        using var p = new Project(Blueprint);

        p.Store.AddClip("Demo", scene: 1, new ClipEditRequest
        {
            Scene = 1,
            Clip = 4,
            VisualPrompt = "clip four",
        });

        var map = p.BeatMap();
        Assert.Equal(4, map.Count);
        Assert.Equal(new[] { "sb_one", "sb_two", "sb_three" }, map.Take(3));
        Assert.False(string.IsNullOrWhiteSpace(map[3]));
        Assert.Equal(p.ClipBeatIds(), map);
        Assert.Empty(Validate(p));
    }

    /// <summary>
    /// Rebuilding from the clips also repairs a scene that is already out of step — which is the
    /// state every project that has ever had a clip deleted is sitting in.
    /// </summary>
    [Fact]
    public void Deleting_a_clip_heals_a_map_that_was_already_mismatched()
    {
        using var p = new Project("""
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "stage1_beat_map": ["sb_one", "sb_orphan", "sb_two", "sb_three"],
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "one", "stage1_beat_id": "sb_one" },
                    { "clip_number": 2, "visual_prompt": "two", "stage1_beat_id": "sb_two" },
                    { "clip_number": 3, "visual_prompt": "three", "stage1_beat_id": "sb_three" }
                  ]
                }
              ]
            }
            """);

        p.Store.DeleteClip("Demo", scene: 1, clip: 3);

        Assert.Equal(new[] { "sb_one", "sb_two" }, p.BeatMap());
        Assert.Empty(Validate(p));
    }

    /// <summary>
    /// A clip with no beat id cannot be represented in the map, so the map is left exactly as it
    /// was rather than silently shortened into a different mismatch. A replan is the fix there.
    /// </summary>
    [Fact]
    public void A_clip_without_a_beat_id_leaves_the_map_untouched()
    {
        using var p = new Project("""
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "stage1_beat_map": ["sb_one", "sb_two", "sb_three"],
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "one", "stage1_beat_id": "sb_one" },
                    { "clip_number": 2, "visual_prompt": "two" },
                    { "clip_number": 3, "visual_prompt": "three", "stage1_beat_id": "sb_three" }
                  ]
                }
              ]
            }
            """);

        p.Store.DeleteClip("Demo", scene: 1, clip: 3);

        Assert.Equal(new[] { "sb_one", "sb_two", "sb_three" }, p.BeatMap());
    }

    /// <summary>A credits card carries no beat map; nothing should invent one for it.</summary>
    [Fact]
    public void A_scene_with_no_beat_map_does_not_get_one()
    {
        using var p = new Project("""
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "is_credits": true,
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "card", "is_credits": true },
                    { "clip_number": 2, "visual_prompt": "spare" }
                  ]
                }
              ]
            }
            """);

        p.Store.DeleteClip("Demo", scene: 1, clip: 2);

        Assert.False(p.Scene().TryGetProperty("stage1_beat_map", out _));
    }

    private static IReadOnlyList<ModelValidationIssue> Validate(Project p)
    {
        var plan = JsonSerializer.Deserialize<Dictionary<string, object?>>(p.Json())!;
        return Stage2AggregateValidator.Validate(ToPlanShape(plan))
            .Where(i => i.Code == "beat_clip_mismatch")
            .ToList();
    }

    /// <summary>
    /// The validator walks plain dictionaries/lists; System.Text.Json hands back JsonElement, so
    /// re-shape once here rather than teaching every test about the difference.
    /// </summary>
    private static Dictionary<string, object?> ToPlanShape(Dictionary<string, object?> plan)
    {
        static object? Convert(object? value) => value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } o =>
                o.EnumerateObject().ToDictionary(pr => pr.Name, pr => Convert(pr.Value)),
            JsonElement { ValueKind: JsonValueKind.Array } a =>
                a.EnumerateArray().Select(e => Convert(e)).ToList(),
            JsonElement e => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetInt32(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            },
            _ => value,
        };
        return plan.ToDictionary(kv => kv.Key, kv => Convert(kv.Value));
    }

    private sealed class Project : IDisposable
    {
        private readonly string _root;
        private readonly string _dir;
        public ProjectStore Store { get; }

        public Project(string blueprintJson)
        {
            _root = Path.Combine(Path.GetTempPath(), "fs_beatmap_" + Guid.NewGuid().ToString("N"));
            _dir = Path.Combine(_root, "projects", "Demo");
            Directory.CreateDirectory(Path.Combine(_dir, "assets", "video"));
            File.WriteAllText(Path.Combine(_dir, "project.json"), """{"id":"Demo"}""");
            File.WriteAllText(Path.Combine(_dir, "pipeline_config.json"),
                """{"blueprint_file":"blueprint.clips.grok.json","model_name":"grok-imagine-video"}""");
            File.WriteAllText(Path.Combine(_dir, "blueprint.clips.grok.json"), blueprintJson);
            Store = new ProjectStore(Options.Create(
                new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false }));
        }

        public string Json() => File.ReadAllText(Path.Combine(_dir, "blueprint.clips.grok.json"));

        public JsonElement Scene()
        {
            var doc = JsonDocument.Parse(Json());
            return doc.RootElement.GetProperty("scenes")[0].Clone();
        }

        public List<string> BeatMap() =>
            Scene().TryGetProperty("stage1_beat_map", out var m)
                ? m.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();

        public List<string> ClipBeatIds() =>
            Scene().GetProperty("veo_clips").EnumerateArray()
                .Select(c => c.TryGetProperty("stage1_beat_id", out var b) ? b.GetString() ?? "" : "")
                .ToList();

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* */ }
        }
    }
}
