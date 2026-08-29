using System;
using System.Collections.Generic;

namespace PageToMovie.Core.Models;

public sealed class MovieAutoReviewReport
{
    public string ProjectId { get; set; } = "";
    public int OverallScore { get; set; } = 8; // 1 to 10
    public string Verdict { get; set; } = "Pass"; // "Pass", "Needs Polish", "Continuity Fixes"
    public string SummaryNotes { get; set; } = "";
    public string ExecutiveSummary { get; set; } = "";
    public Dictionary<string, int> CategoryScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MovieSceneGroupFeedback> GroupFeedback { get; set; } = new();
    public List<int> FlaggedScenes { get; set; } = new();
    public string ProviderUsed { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Sequence-group notes that apply to one scene. Empty when the report has no match.</summary>
    public IReadOnlyList<MovieSceneGroupFeedback> GroupsForScene(int sceneNumber)
    {
        if (GroupFeedback.Count == 0)
            return Array.Empty<MovieSceneGroupFeedback>();
        List<MovieSceneGroupFeedback>? matches = null;
        foreach (var group in GroupFeedback)
        {
            if (!group.IncludesScene(sceneNumber))
                continue;
            matches ??= new List<MovieSceneGroupFeedback>();
            matches.Add(group);
        }
        return matches ?? (IReadOnlyList<MovieSceneGroupFeedback>)Array.Empty<MovieSceneGroupFeedback>();
    }
}

public sealed class MovieSceneGroupFeedback
{
    public string SceneRange { get; set; } = ""; // e.g. "Scenes 1-4"
    public int Score { get; set; } = 8;
    public int ContinuityScore { get; set; } = 8;
    public int CharacterScore { get; set; } = 8;
    public int LightingScore { get; set; } = 8;
    public int PacingScore { get; set; } = 8;
    public int DialogueScore { get; set; } = 8;
    public int MusicScore { get; set; } = 8;
    public string ContinuityNotes { get; set; } = "";
    public string VisualConsistencyNotes { get; set; } = "";
    public string LightingNotes { get; set; } = "";
    public string DialogueNotes { get; set; } = "";
    public string AudioNotes { get; set; } = "";
    public List<int> SceneNumbers { get; set; } = new();
    /// <summary>Scene/clip cites for style or medium claims (e.g. S03C01).</summary>
    public List<MovieReviewEvidence> Evidence { get; set; } = new();

    /// <summary>True when this group covers <paramref name="sceneNumber"/> (explicit list, else range text).</summary>
    public bool IncludesScene(int sceneNumber)
    {
        if (SceneNumbers.Count > 0)
            return SceneNumbers.Contains(sceneNumber);
        return RangeTextIncludesScene(SceneRange, sceneNumber);
    }

    /// <summary>"Scenes 1-4" / "Scene 3" style labels — used only when <see cref="SceneNumbers"/> is empty.</summary>
    public static bool RangeTextIncludesScene(string? range, int sceneNumber)
    {
        if (string.IsNullOrWhiteSpace(range) || sceneNumber <= 0)
            return false;
        var nums = new List<int>();
        var n = 0;
        var inNum = false;
        foreach (var ch in range)
        {
            if (char.IsDigit(ch))
            {
                n = inNum ? n * 10 + (ch - '0') : ch - '0';
                inNum = true;
            }
            else if (inNum)
            {
                nums.Add(n);
                n = 0;
                inNum = false;
            }
        }
        if (inNum) nums.Add(n);
        if (nums.Count == 0) return false;
        if (nums.Count == 1) return nums[0] == sceneNumber;
        var lo = nums[0];
        var hi = nums[^1];
        if (hi < lo) (lo, hi) = (hi, lo);
        return sceneNumber >= lo && sceneNumber <= hi;
    }
}

public sealed class MovieReviewEvidence
{
    public string Ref { get; set; } = "";
    public string Claim { get; set; } = "";
}

public sealed class MovieAutoReviewKeyframe
{
    public int SceneNumber { get; set; }
    public int ClipNumber { get; set; }
    public string Label { get; set; } = "";
    public string Base64 { get; set; } = "";
    public string Mime { get; set; } = "image/jpeg";
}
