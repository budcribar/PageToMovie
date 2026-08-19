using System.Text.RegularExpressions;

namespace PageToMovie.Core.Models;

/// <summary>
/// A voice profile that does not say male/female (or boy/girl) lets every independently generated
/// clip pick its own voice — Mary19's narrator was "Warm adult storytelling voice, even mid register"
/// and S02C05 came out female after four male clips. The profile is the only cross-clip voice lock
/// for in-video speech, so sex + age range must be in it.
/// </summary>
public static class VoiceProfileGuard
{
    private static readonly Regex SexTerms = new(
        @"\b(male|female|man|woman|men|women|boy|girl|masculine|feminine|gentleman|lady|father|mother|grandfather|grandmother|king|queen|prince|princess|he|she|his|her|baritone|bass|tenor|alto|soprano|contralto|mezzo)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>True when the profile names the speaker's sex (or a sexed voice type / role word).</summary>
    public static bool StatesSex(string? voiceProfile) =>
        !string.IsNullOrWhiteSpace(voiceProfile) && SexTerms.IsMatch(voiceProfile);

    public const string MissingSexWarning =
        "This voice doesn't say male or female — each clip may pick a different voice.";

    /// <summary>Prefix the profile with an explicit sex so the lock holds across clips.</summary>
    public static string WithSex(string? voiceProfile, string sexWord)
    {
        var p = (voiceProfile ?? "").Trim();
        var lead = sexWord.Trim() switch
        {
            "male" => "Adult male voice",
            "female" => "Adult female voice",
            "boy" => "Young boy's voice",
            "girl" => "Young girl's voice",
            var other => other + " voice",
        };
        if (p.Length == 0) return lead + ".";
        return lead + ", " + char.ToLowerInvariant(p[0]) + p[1..];
    }
}
