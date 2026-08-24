using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Catalog-driven pick of a video model's <c>presetVoices[]</c> id from a character profile.
/// Scoring uses sex/gender, age, and temperament/tags — no hardcoded model ids.
/// </summary>
public static class ImagineVoicePicker
{
    public readonly record struct VoiceHints(
        string? Gender,
        string? AgeBand,
        string? Profile,
        string? Label,
        string? Temperament);

    public static string? NormalizeVoiceId(IReadOnlyList<PresetVoiceEntry>? roster, string? voiceId)
    {
        if (roster is null || roster.Count == 0 || string.IsNullOrWhiteSpace(voiceId))
            return null;
        var match = roster.FirstOrDefault(v =>
            string.Equals(v.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.Id) ? null : match.Id;
    }

    public static string? Pick(IReadOnlyList<PresetVoiceEntry>? roster, VoiceHints hints)
    {
        if (roster is null || roster.Count == 0)
            return null;
        var usable = roster.Where(v => !string.IsNullOrWhiteSpace(v.Id)).ToList();
        if (usable.Count == 0)
            return null;

        var wantGender = NormalizeGender(hints.Gender, hints.Profile, hints.Label);
        var wantAge = NormalizeAge(hints.AgeBand, hints.Profile, hints.Label);
        var tokens = Tokenize(hints.Profile, hints.Label, hints.Temperament);

        return usable
            .OrderByDescending(v => Score(v, wantGender, wantAge, tokens))
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .First()
            .Id;
    }

    internal static int Score(
        PresetVoiceEntry voice,
        string? wantGender,
        string? wantAge,
        IReadOnlyCollection<string> tokens)
    {
        var score = 0;
        if (wantGender is not null &&
            string.Equals(voice.Gender, wantGender, StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (wantAge is not null &&
            string.Equals(voice.Age, wantAge, StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (!string.IsNullOrWhiteSpace(voice.Temperament) &&
            tokens.Contains(voice.Temperament.Trim().ToLowerInvariant()))
            score += 10;
        foreach (var tag in voice.Tags ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(tag) && tokens.Contains(tag.Trim().ToLowerInvariant()))
                score += 5;
        }
        return score;
    }

    internal static string? NormalizeGender(string? gender, string? profile, string? label)
    {
        var blob = $"{gender} {profile} {label}";
        if (ContainsAny(blob, "female", "woman", "girl", "her ", "she "))
            return "female";
        if (ContainsAny(blob, "male", "man", "boy", "him ", "he "))
            return "male";
        return null;
    }

    internal static string? NormalizeAge(string? ageBand, string? profile, string? label)
    {
        var blob = $"{ageBand} {profile} {label}";
        if (ContainsAny(blob, "elderly", "elder", "old", "senior", "aged", "weathered"))
            return "elderly";
        if (ContainsAny(blob, "child", "kid", "youthful", "teen", "young", "boy", "girl"))
            return "youthful";
        if (ContainsAny(blob, "adult", "middle"))
            return "adult";
        return null;
    }

    private static HashSet<string> Tokenize(params string?[] parts)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;
            foreach (var raw in part.Split(
                         new[] { ' ', ',', ';', '.', '/', '|', '-', '_' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = raw.ToLowerInvariant();
                if (token.Length >= 3)
                    set.Add(token);
            }
        }
        return set;
    }

    private static bool ContainsAny(string blob, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(blob))
            return false;
        foreach (var needle in needles)
        {
            if (blob.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
