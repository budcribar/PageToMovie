using System.Collections.Generic;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Overlay and Easy Start catalog share one completion check:
/// reviewed Dialogue Timing rows, or alignment segments that are not estimate-only.
/// </summary>
public class VoiceSubstitutionOverlayGateTests
{
    [Fact]
    public void CanOverlay_false_when_both_missing()
    {
        Assert.False(VoiceSubstitutionOverlayGate.CanOverlay(null, null));
    }

    [Fact]
    public void CanOverlay_false_for_estimate_only_alignment()
    {
        var alignment = new ProjectVoiceAlignment
        {
            Clips =
            {
                new ClipSpeechAlignment
                {
                    Scene = 1, Clip = 1,
                    Segments =
                    {
                        new SpeechSegment
                        {
                            DialogueText = "Hello",
                            StartSec = 0, EndSec = 1.2,
                            Source = SpeechTimestampSource.Estimate,
                        },
                    },
                },
            },
        };

        Assert.False(VoiceSubstitutionOverlayGate.AlignmentReviewed(alignment));
        Assert.False(VoiceSubstitutionOverlayGate.CanOverlay(alignment, timing: null));
    }

    [Fact]
    public void CanOverlay_true_when_alignment_segments_are_measured()
    {
        var alignment = new ProjectVoiceAlignment
        {
            Clips =
            {
                new ClipSpeechAlignment
                {
                    Scene = 1, Clip = 1,
                    Segments =
                    {
                        new SpeechSegment
                        {
                            DialogueText = "Hello",
                            StartSec = 0.4, EndSec = 1.8,
                            Source = SpeechTimestampSource.Silence,
                        },
                    },
                },
            },
        };

        Assert.True(VoiceSubstitutionOverlayGate.AlignmentReviewed(alignment));
        Assert.True(VoiceSubstitutionOverlayGate.CanOverlay(alignment, timing: null));
    }

    [Fact]
    public void CanOverlay_true_when_dialogue_timing_rows_are_reviewed()
    {
        var timing = new DialogueTimingDoc
        {
            Scenes =
            {
                new DialogueTimingScene
                {
                    Scene = 1,
                    Rows =
                    {
                        new DialogueTimingRow
                        {
                            ScriptText = "Hello class",
                            WindowStartSec = 0.5,
                            WindowEndSec = 2.0,
                            Reviewed = true,
                        },
                    },
                },
            },
        };

        Assert.True(VoiceSubstitutionOverlayGate.TimingReviewed(timing));
        Assert.True(VoiceSubstitutionOverlayGate.CanOverlay(alignment: null, timing));
    }

    [Fact]
    public void CanOverlay_false_when_timing_rows_exist_but_are_not_reviewed()
    {
        var timing = new DialogueTimingDoc
        {
            Scenes =
            {
                new DialogueTimingScene
                {
                    Scene = 1,
                    Rows =
                    {
                        new DialogueTimingRow
                        {
                            ScriptText = "Hello class",
                            WindowStartSec = 0.5,
                            WindowEndSec = 2.0,
                            Reviewed = false,
                        },
                    },
                },
            },
        };

        Assert.False(VoiceSubstitutionOverlayGate.TimingReviewed(timing));
        Assert.False(VoiceSubstitutionOverlayGate.CanOverlay(alignment: null, timing));
    }

    [Fact]
    public void ShowEasyStartEntry_only_when_at_least_one_timing_complete_title()
    {
        Assert.False(VoiceSubstitutionOverlayGate.ShowEasyStartEntry(0));
        Assert.True(VoiceSubstitutionOverlayGate.ShowEasyStartEntry(1));
        Assert.True(VoiceSubstitutionOverlayGate.ShowEasyStartEntry(3));
    }

    [Fact]
    public void IsMissingSceneList_false_when_scenes_exist()
    {
        var scenes = new List<SceneSummary>
        {
            new() { SceneNumber = 1, ClipCount = 2, ClipsOnDisk = 2 },
            new() { SceneNumber = 2, ClipCount = 1, ClipsOnDisk = 1 },
        };

        Assert.False(VoiceSubstitutionOverlayGate.IsMissingSceneList(scenes));
    }

    [Fact]
    public void IsMissingSceneList_true_only_when_null_or_empty()
    {
        Assert.True(VoiceSubstitutionOverlayGate.IsMissingSceneList(null));
        Assert.True(VoiceSubstitutionOverlayGate.IsMissingSceneList(new List<SceneSummary>()));
    }

    [Fact]
    public void FirstRecordedCharacterKey_prefers_clone_id_not_narrator_default()
    {
        var chars = new List<CharacterSummary>
        {
            new() { Key = "Character_Mary", VoiceProviderVoiceId = null },
            new() { Key = "Character_Teacher", VoiceProviderVoiceId = "voice_abc" },
        };

        Assert.Equal("Character_Teacher", VoiceSubstitutionOverlayGate.FirstRecordedCharacterKey(chars));
    }
}
