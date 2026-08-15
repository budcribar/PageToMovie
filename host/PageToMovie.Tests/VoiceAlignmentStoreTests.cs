using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Free tests (no paid provider calls) for the voice-substitution alignment model, persistence
/// round-trip, blueprint→speaker association, and detected-window → dialogue-line matching.
/// </summary>
public class VoiceAlignmentStoreTests
{
    private const string BlueprintJson = """
    {
      "scenes": [
        {
          "scene_number": 1,
          "veo_clips": [
            {
              "clip_number": 1,
              "duration_seconds": 6.0,
              "characters_on_screen": ["Character_Buster"],
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "He's Buster the Noodle Head Dog."
              }
            },
            {
              "clip_number": 2,
              "duration_seconds": 5.0,
              "audio_payload": {
                "speaker": "Character_Momma",
                "dialogue": "Buster, come here!"
              }
            },
            {
              "clip_number": 3,
              "duration_seconds": 4.0,
              "audio_payload": { "speaker": "Character_Narrator", "dialogue": "" }
            }
          ]
        }
      ]
    }
    """;

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // ── Association: blueprint → dialogue lines ──────────────────────────────────────────────

    [Fact]
    public void BuildDialogueLines_reads_speaker_and_text_from_audio_payload()
    {
        var clips = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(Root(BlueprintJson));

        // Clip 3 has empty dialogue → excluded. Clips 1 and 2 remain.
        Assert.Equal(2, clips.Count);

        var c1 = clips.Single(c => c.Clip == 1);
        Assert.Single(c1.Lines);
        Assert.Equal("Character_Narrator", c1.Lines[0].CharacterKey);
        Assert.Equal("He's Buster the Noodle Head Dog.", c1.Lines[0].Text);
        Assert.Equal(6.0, c1.PlannedDurationSeconds);

        var c2 = clips.Single(c => c.Clip == 2);
        Assert.Equal("Character_Momma", c2.Lines[0].CharacterKey);
    }

    [Fact]
    public void BuildDialogueLines_filter_keeps_only_matching_speaker()
    {
        var narratorOnly = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(
            Root(BlueprintJson),
            spk => string.Equals(spk, "Character_Narrator", StringComparison.OrdinalIgnoreCase));

        Assert.Single(narratorOnly);
        Assert.Equal(1, narratorOnly[0].Clip);
        Assert.Equal("Character_Narrator", narratorOnly[0].Lines[0].CharacterKey);
    }

    [Fact]
    public void BuildClipDialogueLines_supports_multi_line_array()
    {
        const string multi = """
        {
          "clip_number": 9,
          "audio_payload": {
            "lines": [
              { "speaker": "Character_A", "dialogue": "First line." },
              { "speaker": "Character_B", "dialogue": "Second line." }
            ]
          }
        }
        """;
        var lines = VoiceAlignmentStore.BuildClipDialogueLines(Root(multi));
        Assert.Equal(2, lines.Count);
        Assert.Equal("Character_A", lines[0].CharacterKey);
        Assert.Equal("Character_B", lines[1].CharacterKey);
        Assert.Equal("Second line.", lines[1].Text);
    }

    // ── Matching: detected windows → dialogue lines ──────────────────────────────────────────

    [Fact]
    public void MatchSegments_one_to_one_when_counts_equal()
    {
        var lines = new[]
        {
            new VoiceAlignmentStore.DialogueLine("Character_A", "Hello there."),
            new VoiceAlignmentStore.DialogueLine("Character_B", "General Kenobi."),
        };
        var windows = new[] { (0.5, 1.8), (2.2, 3.9) };

        var segs = VoiceAlignmentStore.MatchSegmentsToLines(windows, lines, clipDurationSeconds: 4.0);

        Assert.Equal(2, segs.Count);
        Assert.Equal(0.5, segs[0].StartSec);
        Assert.Equal(1.8, segs[0].EndSec);
        Assert.Equal("Character_A", segs[0].CharacterKey);
        Assert.Equal(2.2, segs[1].StartSec);
        Assert.Equal(SpeechTimestampSource.Silence, segs[1].Source);
    }

    [Fact]
    public void MatchSegments_single_line_uses_detected_speech_span()
    {
        var lines = new[] { new VoiceAlignmentStore.DialogueLine("Character_Narrator", "One long narrated line.") };
        // Two detected windows (a mid-line pause) but only one known line → span [first,last].
        var windows = new[] { (0.4, 1.5), (1.9, 3.1) };

        var segs = VoiceAlignmentStore.MatchSegmentsToLines(windows, lines, clipDurationSeconds: 6.0);

        Assert.Single(segs);
        Assert.Equal(0.4, segs[0].StartSec);
        Assert.Equal(3.1, segs[0].EndSec);
        Assert.Equal(SpeechTimestampSource.Silence, segs[0].Source);
    }

    [Fact]
    public void MatchSegments_no_windows_estimates_across_clip()
    {
        var lines = new[]
        {
            new VoiceAlignmentStore.DialogueLine("A", "aa"),   // weight 2
            new VoiceAlignmentStore.DialogueLine("B", "bbbbbb"), // weight 6
        };
        var segs = VoiceAlignmentStore.MatchSegmentsToLines(
            Array.Empty<(double, double)>(), lines, clipDurationSeconds: 8.0);

        Assert.Equal(2, segs.Count);
        Assert.Equal(SpeechTimestampSource.Estimate, segs[0].Source);
        Assert.Equal(0.0, segs[0].StartSec);
        // First slice ~ 8 * 2/8 = 2.0; last segment ends at clip end.
        Assert.Equal(2.0, segs[0].EndSec, 3);
        Assert.Equal(2.0, segs[1].StartSec, 3);
        Assert.Equal(8.0, segs[1].EndSec, 3);
    }

    [Fact]
    public void MatchSegments_drops_sub_threshold_noise_windows()
    {
        var lines = new[]
        {
            new VoiceAlignmentStore.DialogueLine("A", "line one"),
            new VoiceAlignmentStore.DialogueLine("B", "line two"),
        };
        // A 0.05s click plus two real windows → click dropped, counts then equal.
        var windows = new[] { (0.10, 0.15), (0.5, 1.6), (2.0, 3.4) };

        var segs = VoiceAlignmentStore.MatchSegmentsToLines(windows, lines, 4.0);

        Assert.Equal(2, segs.Count);
        Assert.Equal(0.5, segs[0].StartSec);
        Assert.Equal(2.0, segs[1].StartSec);
    }

    // ── ApplyTimestamps: merge client windows onto persisted clip ─────────────────────────────

    [Fact]
    public void ApplyTimestamps_matches_windows_and_preserves_audio_paths()
    {
        var clip = new ClipSpeechAlignment
        {
            Scene = 1,
            Clip = 2,
            ClipDurationSeconds = 0,
            Segments =
            {
                new SpeechSegment { Index = 0, CharacterKey = "A", DialogueText = "hello", Source = SpeechTimestampSource.Estimate, VoiceAudioRelativePath = "assets/audio/revoice/scene_01_clip_02_seg_00.mp3" },
            },
        };
        var update = new ClipTimestampUpdate
        {
            Scene = 1,
            Clip = 2,
            ClipDurationSeconds = 5.0,
            Windows = { new SpeechWindow { StartSec = 1.0, EndSec = 3.5 } },
        };

        VoiceAlignmentStore.ApplyTimestamps(clip, update);

        Assert.Equal(5.0, clip.ClipDurationSeconds);
        Assert.Equal(1.0, clip.Segments[0].StartSec);
        Assert.Equal(3.5, clip.Segments[0].EndSec);
        Assert.Equal(SpeechTimestampSource.Silence, clip.Segments[0].Source);
        // Association preserved.
        Assert.Equal("A", clip.Segments[0].CharacterKey);
        Assert.Equal("assets/audio/revoice/scene_01_clip_02_seg_00.mp3", clip.Segments[0].VoiceAudioRelativePath);
        Assert.True(clip.IsDetected);
    }

    // ── Persistence round-trip ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_then_Load_round_trips_alignment()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm_align_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var projects = new ProjectStore(opts);
            var project = await projects.CreateProjectAsync("testproj", "Test Proj");
            var projectId = project.Id;
            var store = new VoiceAlignmentStore(projects);

            var alignment = new ProjectVoiceAlignment
            {
                ProjectId = projectId,
                CharKey = "Character_Narrator",
                Clips =
                {
                    new ClipSpeechAlignment
                    {
                        Scene = 1, Clip = 1, ClipDurationSeconds = 6.0,
                        Segments =
                        {
                            new SpeechSegment
                            {
                                Index = 0, CharacterKey = "Character_Narrator",
                                DialogueText = "He's Buster the Noodle Head Dog.",
                                StartSec = 0.4, EndSec = 3.1, Source = SpeechTimestampSource.Silence,
                                VoiceAudioRelativePath = "assets/audio/revoice/scene_01_clip_01_seg_00.mp3",
                            },
                        },
                    },
                },
            };

            await store.SaveAsync(projectId, alignment);

            // File lands at the documented, export-portable location.
            var path = store.AlignmentPath(projectId);
            Assert.True(File.Exists(path));
            Assert.EndsWith(Path.Combine("assets", "alignment", "voice_alignment.json"),
                path, StringComparison.OrdinalIgnoreCase);

            var loaded = await store.LoadAsync(projectId);
            Assert.NotNull(loaded);
            var clip = loaded!.Find(1, 1);
            Assert.NotNull(clip);
            Assert.Single(clip!.Segments);
            var seg = clip.Segments[0];
            Assert.Equal("Character_Narrator", seg.CharacterKey);
            Assert.Equal("He's Buster the Noodle Head Dog.", seg.DialogueText);
            Assert.Equal(0.4, seg.StartSec);
            Assert.Equal(3.1, seg.EndSec);
            Assert.Equal(SpeechTimestampSource.Silence, seg.Source);
            Assert.True(clip.IsDetected);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ApplyReviewedTiming_writes_manual_windows_onto_alignment()
    {
        var alignment = new ProjectVoiceAlignment { ProjectId = "Demo" };
        var scene = new DialogueTimingScene
        {
            Scene = 1,
            SceneDurationSec = 8,
            Rows =
            {
                new DialogueTimingRow
                {
                    Clip = 1,
                    Speaker = "Character_Teacher",
                    ScriptText = "Mary, sit down.",
                    WindowStartSec = 1.1,
                    WindowEndSec = 2.8,
                    Reviewed = true,
                },
                new DialogueTimingRow
                {
                    Clip = 1,
                    ScriptText = "ignored — not reviewed",
                    WindowStartSec = 3,
                    WindowEndSec = 4,
                    Reviewed = false,
                },
            },
        };

        VoiceAlignmentStore.ApplyReviewedTiming(alignment, scene);

        var clip = alignment.Find(1, 1);
        Assert.NotNull(clip);
        Assert.Single(clip!.Segments);
        Assert.Equal(1.1, clip.Segments[0].StartSec);
        Assert.Equal(2.8, clip.Segments[0].EndSec);
        Assert.Equal(SpeechTimestampSource.Manual, clip.Segments[0].Source);
        Assert.True(clip.IsDetected);
        Assert.True(VoiceSubstitutionOverlayGate.CanOverlay(alignment, timing: null));
    }

    [Fact]
    public async Task EasyStart_timing_complete_requires_reviewed_or_measured_windows()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm_align_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
            var projects = new ProjectStore(opts);
            var project = await projects.CreateProjectAsync("libbook", "Library Book");
            var store = new VoiceAlignmentStore(projects);

            Assert.False(await store.IsEasyStartTimingCompleteAsync(project.Id));

            var timing = new DialogueTimingDoc
            {
                ProjectId = project.Id,
                Scenes =
                {
                    new DialogueTimingScene
                    {
                        Scene = 1,
                        Rows =
                        {
                            new DialogueTimingRow
                            {
                                ScriptText = "Good morning.",
                                WindowStartSec = 0.2,
                                WindowEndSec = 1.5,
                                Reviewed = true,
                            },
                        },
                    },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(store.DialogueTimingPath(project.Id))!);
            await File.WriteAllTextAsync(
                store.DialogueTimingPath(project.Id),
                JsonSerializer.Serialize(timing));

            Assert.True(await store.IsEasyStartTimingCompleteAsync(project.Id));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RevoiceSegmentAudioRelativePath_is_stable_and_per_segment()
    {
        Assert.Equal(
            "assets/audio/revoice/scene_01_clip_02_seg_00.mp3",
            MediaRegistryService.RevoiceSegmentAudioRelativePath(1, 2, 0));
        Assert.Equal(
            "assets/audio/revoice/scene_03_clip_04_seg_02.wav",
            MediaRegistryService.RevoiceSegmentAudioRelativePath(3, 4, 2, ".wav"));
    }
}
