using System.Text;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// One voice instruction per speaker in generate / extend prompts.
/// No catalog sample: <c>&lt;VoiceLock&gt;</c> owns the full <c>voice_profile</c>.
/// Catalog preset attached as <c>reference_audios</c>: the sample owns timbre;
/// leftover <c>&lt;Voice&gt;</c> is pace / accent / manner only.
/// </summary>
public static class VoiceTagWriter
{
    public const string VoiceTag = "Voice";
    public const string VoiceLockTag = "VoiceLock";

    /// <summary>Pitch / register / grain. Sexed voice types live on <see cref="VoiceProfileGuard"/>.</summary>
    private static readonly Regex TimbreTerms = new(
        @"\b(?:pitch|register|timbre|rasp(?:y|ing)?|gravelly|husky|throaty|nasal|breathy|resonant)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex PerformanceTerms = new(
        @"\b(?:pace|accent|manner|cadence|diction|energy|delivery|urgency|whisper(?:ed|ing)?|measured|clipped|confessional|storytell(?:er|ing)?|rhythm|tempo|lilt|drawl|hesitat(?:e|ion|ing)?|stammer|hurried|articulate|precise|controlled|fevered|intimate|polished|quick|slow|calm)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex CrossClipIdentityRestatement = new(
        @"\bsame voice\b(?:\s+on[- ]camera)?(?:\s+and\s+in\s+v\.?o\.?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly char[] ClauseSeps = [';', '.', '!', '?'];

    /// <summary>
    /// Pace / accent / manner only. Empty when the profile was only sex, age, or timbre.
    /// </summary>
    public static string SlimToPerformance(string? voiceProfile)
    {
        var raw = (voiceProfile ?? "").Trim();
        if (raw.Length == 0)
            return "";

        var kept = new List<string>();
        foreach (var clause in SplitClauses(raw))
        {
            var slim = SlimClause(clause);
            if (slim.Length > 0)
                kept.Add(slim);
        }

        return string.Join("; ", kept);
    }

    /// <summary>
    /// Character-line <c>&lt;Voice&gt;</c> prose, or empty when VoiceLock / silence owns the speaker.
    /// </summary>
    public static string VoiceProseForCharacterLine(
        string key,
        string? voiceProfile,
        IReadOnlySet<string> speakers,
        IReadOnlyDictionary<string, string>? audioTags)
    {
        var raw = PromptTags.SanitizeValue(voiceProfile?.Trim());
        if (raw.Length == 0 || !IsSpokenKey(key, speakers))
            return "";
        if (!SpeakerHasAttachedPreset(key, audioTags))
            return "";
        return SlimToPerformance(raw);
    }

    /// <summary>
    /// Full-profile VoiceLock for every speaker who has no attached catalog sample.
    /// Empty when every speaker has a preset (or no profile).
    /// </summary>
    public static string BuildVoiceLocks(
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile>? characters,
        IEnumerable<string> speakers,
        IReadOnlyDictionary<string, string>? audioTags)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in speakers)
        {
            if (string.IsNullOrWhiteSpace(speaker) || !seen.Add(speaker))
                continue;
            if (SpeakerHasAttachedPreset(speaker, audioTags))
                continue;
            var prof = ClipVideoPromptBuilder.GetCharacterProfile(characters, speaker);
            if (prof is null || string.IsNullOrWhiteSpace(prof.VoiceProfile))
                continue;
            sb.Append(' ');
            sb.Append(PromptTags.Wrap(VoiceLockTag,
                $"{speaker}: {PromptTags.SanitizeValue(prof.VoiceProfile)} — exactly this one voice (same sex, age and timbre) as in every other clip of this film."));
        }

        return sb.ToString();
    }

    public static bool SpeakerHasAttachedPreset(
        string? key,
        IReadOnlyDictionary<string, string>? audioTags)
    {
        if (string.IsNullOrWhiteSpace(key) || audioTags is null || audioTags.Count == 0)
            return false;
        if (audioTags.ContainsKey(key))
            return true;
        var norm = Stage2PlannerService.NormalizeCharacterKey(key);
        return audioTags.Keys.Any(tagged =>
            string.Equals(
                Stage2PlannerService.NormalizeCharacterKey(tagged),
                norm,
                StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSpokenKey(string? key, IReadOnlySet<string>? speakers)
    {
        if (string.IsNullOrWhiteSpace(key) || speakers is null || speakers.Count == 0)
            return false;
        if (speakers.Contains(key))
            return true;
        var norm = Stage2PlannerService.NormalizeCharacterKey(key);
        return speakers.Any(speaker =>
            string.Equals(
                Stage2PlannerService.NormalizeCharacterKey(speaker),
                norm,
                StringComparison.OrdinalIgnoreCase));
    }

    public static int CountVoiceTags(string? prompt) => CountTag(prompt, VoiceTag);

    public static int CountVoiceLocks(string? prompt) => CountTag(prompt, VoiceLockTag);

    private static int CountTag(string? prompt, string tag)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return 0;
        return CommonRegex.Matches(prompt, $@"<{tag}>", RegexOptions.IgnoreCase).Count;
    }

    private static string SlimClause(string clause)
    {
        var hasPerformance = PerformanceTerms.IsMatch(clause);
        var hasIdentity =
            VoiceProfileGuard.StatesSex(clause) ||
            VoiceProfileGuard.StatesAge(clause) ||
            TimbreTerms.IsMatch(clause);
        if (hasIdentity && !hasPerformance)
            return "";

        var stripped = VoiceProfileGuard.StripIdentityTokens(clause);
        stripped = TimbreTerms.Replace(stripped, " ");
        stripped = CrossClipIdentityRestatement.Replace(stripped, " ");
        stripped = CommonRegex.WhitespaceCollapse.Replace(stripped, " ").Trim(' ', ',', ';', '-', ':');
        return stripped;
    }

    private static List<string> SplitClauses(string text)
    {
        var clauses = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(ClauseSeps, text[i]) < 0)
                continue;
            var piece = text[start..i].Trim();
            if (piece.Length > 0)
                clauses.Add(piece);
            start = i + 1;
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
            clauses.Add(tail);
        return clauses;
    }
}
