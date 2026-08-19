using System.Text.Json;
using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>
/// One generic place to say "this clip's plan text contradicts what we know about the cast" —
/// instead of a gen-time patch per defect (which hides the fault and multiplies special cases).
/// Findings are surfaced (clip stale reason "plan_lint", job log, Stage-2 report); the fix is
/// always the same: correct the planner rule, rebuild the shot plan. Add rules here, not strips.
/// </summary>
public static class ShotPlanLint
{
    public sealed record Finding(string Rule, string Message);

    /// <summary>Lint a blueprint clip against cast facts. Empty when the plan text is consistent.</summary>
    public static IReadOnlyList<Finding> Check(JsonElement clipEl, IReadOnlyCollection<string> voiceOnlyKeys)
    {
        var findings = new List<Finding>();
        var visual = clipEl.TryGetProperty("visual_prompt", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
        // Rule 1: a voice-only role (never_on_screen) placed on screen or dressed.
        foreach (var key in voiceOnlyKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var k = Regex.Escape(key);
            var dressed = Regex.IsMatch(visual, $@"\b{k}\s+still\s+wears\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            // characters_on_screen legitimately carries the VO speaker (how the line's speaker is attached;
            // gen-time cast filtering drops voice-only keys) — only the PROSE is the fault.
            var listedOnScreen =
                Regex.IsMatch(visual, $@"also on screen:[^.<]*\b{k}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
                || Regex.IsMatch(visual, $@"\b{k}\s+is\s+on\s+screen\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (dressed || listedOnScreen)
                findings.Add(new Finding("voice_only_on_screen",
                    $"{key} is voice-only but the plan {(listedOnScreen && dressed ? "lists it on screen and dresses it" : listedOnScreen ? "lists it on screen" : "gives it a wardrobe")} — rebuild the shot plan"));
        }
        return findings;
    }

    /// <summary>Cast keys whose seed says display_name_policy = never_on_screen.</summary>
    public static IReadOnlyCollection<string> VoiceOnlyKeys(Dictionary<string, JsonElement> seeds)
    {
        var keys = new List<string>();
        foreach (var (key, info) in seeds)
        {
            if (info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("display_name_policy", out var p) && p.ValueKind == JsonValueKind.String
                && string.Equals(p.GetString(), "never_on_screen", StringComparison.OrdinalIgnoreCase))
                keys.Add(key);
        }
        return keys;
    }
}
