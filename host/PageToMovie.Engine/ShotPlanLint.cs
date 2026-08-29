using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// One generic place to say "this clip's plan text contradicts what we know about the cast" —
/// instead of a gen-time patch per defect (which hides the fault and multiplies special cases).
/// Findings are surfaced (clip stale reason "plan_lint", job log, Stage-2 report); the fix is
/// always the same: correct the planner rule, rebuild the shot plan. Add rules here, not strips.
/// </summary>
public static class ShotPlanLint
{
    /// <param name="Advisory">
    /// True for a finding that says the plan was built by an older rule rather than that this
    /// clip's text is wrong. Both go in the job log; only a non-advisory finding marks a rendered
    /// clip stale, because staling a clip asks the user to spend money regenerating it.
    /// </param>
    public sealed record Finding(string Rule, string Message, bool Advisory = false);

    /// <summary>Lint a blueprint clip against cast facts. Empty when the plan text is consistent.</summary>
    /// <param name="currentStyleHead">
    /// The project's live style lock. When given, a clip whose plan text bakes in a different one
    /// is reported — the plan predates a style change and every clip is carrying both.
    /// </param>
    public static IReadOnlyList<Finding> Check(
        JsonElement clipEl,
        IReadOnlyCollection<string> voiceOnlyKeys,
        string? currentStyleHead = null)
    {
        var findings = new List<Finding>();
        var visual = clipEl.TryGetProperty("visual_prompt", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
        AddStyleLockDrift(findings, visual, currentStyleHead);
        AddUncheckedContinuation(findings, clipEl);
        // Rule 1: a voice-only role (never_on_screen) placed on screen or dressed.
        foreach (var key in voiceOnlyKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var k = Regex.Escape(key);
            var dressed = Regex.IsMatch(visual, $@"\b{k}\s+still\s+wears\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            // The cast list is a <Cast> tag now — the "also on screen:" label it used to carry is
            // gone, so matching that prose would silently stop finding anything.
            var listedOnScreen =
                Regex.IsMatch(visual, $@"<{PromptFieldTags.Cast}>[^<]*\b{k}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
                || Regex.IsMatch(visual, $@"\b{k}\s+is\s+on\s+screen\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (dressed || listedOnScreen)
            {
                string how;
                if (listedOnScreen && dressed)
                    how = "lists it on screen and dresses it";
                else if (listedOnScreen)
                    how = "lists it on screen";
                else
                    how = "gives it a wardrobe";
                findings.Add(new Finding("voice_only_on_screen",
                    $"{key} is voice-only but the plan {how} — rebuild the shot plan"));
            }
        }
        return findings;
    }

    /// <summary>
    /// Rule 3: this clip continues the previous one, but the plan does not record which rule
    /// decided that. Stage 2 stamps <c>continuity_rule</c> on every clip it plans, so a missing
    /// stamp means the plan predates the staging test — the check that an extend's action opens
    /// where the previous clip's action left the cast. Those plans are where a continuation clip
    /// gets an <c>&lt;Action&gt;</c> restaging the room beside a <c>&lt;Continuity&gt;</c> block
    /// telling the model to pick up from the previous last frame; the model resolves the
    /// contradiction in favour of the Action and the subject jumps across the set.
    ///
    /// <para>Advisory, not staling: most extends in an old plan are genuinely continuous, and this
    /// rule cannot tell which ones are not — only a rebuild can. It says "rebuild the shot plan",
    /// it does not condemn the clip.</para>
    /// </summary>
    private static void AddUncheckedContinuation(List<Finding> findings, JsonElement clipEl)
    {
        if (!IsExtendPrevious(clipEl) || HasContinuityRule(clipEl))
            return;
        findings.Add(new Finding("continuation_unchecked",
            "this clip continues the previous one, but the plan predates the check that its action "
            + "starts where the previous clip left the cast — rebuild the shot plan",
            Advisory: true));
    }

    private static bool IsExtendPrevious(JsonElement clipEl) =>
        clipEl.TryGetProperty("veo_continuation_source", out var cs)
        && cs.ValueKind == JsonValueKind.String
        && string.Equals(cs.GetString(), "extend_previous", StringComparison.OrdinalIgnoreCase);

    private static bool HasContinuityRule(JsonElement clipEl) =>
        clipEl.TryGetProperty("continuity_rule", out var cr)
        && cr.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(cr.GetString());

    /// <summary>
    /// Rule 2: the clip's baked-in STYLE LOCK disagrees with the project's current one. Changing
    /// the style after a shot plan is built leaves the old lock inside every clip prompt, so the
    /// model gets two mediums at once — Mary19 shipped "flat watercolor washes" alongside
    /// "stylized 3D animated CG" in 19 clips. Generation overwrites the clip lock from the
    /// project visual medium; rebuild the shot plan so the stored prompt matches.
    /// </summary>
    private static void AddStyleLockDrift(List<Finding> findings, string visual, string? currentStyleHead)
    {
        // The plan's copy is a tag; the project's live head is still the prose the style
        // classifier produces, so its "STYLE LOCK:" label has to come off before they compare.
        var planned = ExtractStyleLock(visual);
        var current = ExtractStyleLock(currentStyleHead) ?? StripStyleLockLabel(currentStyleHead);
        if (string.IsNullOrEmpty(planned) || string.IsNullOrEmpty(current))
            return;
        if (VisualMediumStyles.StyleLocksAgree(planned, current))
            return;
        findings.Add(new Finding("style_lock_drift",
            $"plan says \"{Excerpt(planned)}\"; project style is \"{Excerpt(current)}\" — " +
            "rebuild the shot plan"));
    }

    private static string? ExtractStyleLock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        // Tag only — Stage 2 emits it. A plan predating that simply does not report drift, which
        // is the right answer: rebuilding it is the fix for drift anyway.
        var m = Regex.Match(
            text, $@"<{PromptFieldTags.StyleLock}>(.*?)</{PromptFieldTags.StyleLock}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        return m.Success ? Normalize(m.Groups[1].Value) : null;
    }

    /// <summary>The live style head is prose and may or may not lead with the label.</summary>
    private static string StripStyleLockLabel(string? value)
    {
        var t = Normalize(value);
        var m = Regex.Match(t, @"^STYLE LOCK(?:\s*\(hard\))?:\s*", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success ? Normalize(t[m.Length..]) : t;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
                .TrimEnd('.', ';', ',');

    private static string Excerpt(string value) =>
        value.Length <= 60 ? value : value[..60] + "…";

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
