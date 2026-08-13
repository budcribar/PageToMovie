using System.Text.Json;
using PageToMovie.Core.Models;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Conversion;

using PageToMovie.Core.Utils;
namespace PageToMovie.Adaptation.Validation;

/// <summary>
/// Deterministic Stage-1→cast package gate: every speaking Fountain character must resolve
/// to a stable <c>cast_seeds.json</c> entry with production-usable look fields.
/// Used by benchmarks and as a pre-Stage-2 readiness check. Does not invent cast members.
/// Pure text only — no ProjectStore, no Engine.
/// </summary>
public static class CastPackageCrossCheck
{
    private static readonly Regex ExtensionStrip = new(@"\s*(\(.*\)|V\.?O\.?|O\.?S\.?|O\.?C\.?|CONT'D|CONTINUED)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z']+", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Speakers that never need cast plates (optional packaging).</summary>
    private static readonly HashSet<string> OptionalSpeakers = new(StringComparer.OrdinalIgnoreCase)
    {
        "NARRATOR", "NARRATION", "TITLE", "CARD", "SUPER", "CHYRON",
    };

    public sealed class Report
    {
        public bool Ok => Failures.Count == 0;
        /// <summary>0–100. Membership + description quality for speaking cast.</summary>
        public double Score { get; set; }
        public List<string> Speakers { get; set; } = new();
        public List<string> RequiredSpeakers { get; set; } = new();
        public List<string> CastKeys { get; set; } = new();
        public List<string> MatchedKeys { get; set; } = new();
        public List<string> Failures { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public Dictionary<string, CharacterQuality> Quality { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Speakers that look like invented proper names (not present in book text).
        /// Advisory — does not fail the package by itself.
        /// </summary>
        public List<string> SpeakersMissingFromBook { get; set; } = new();
        /// <summary>Dialogue speakers with no matching cast_seeds entry (hard membership failure).</summary>
        public List<string> SpeakersMissingFromCast { get; set; } = new();
        /// <summary>Cast keys classified as group/chorus (no single-face portrait expected).</summary>
        public List<string> GroupCastKeys { get; set; } = new();
        /// <summary>0–100 membership sub-score (speakers found in seeds).</summary>
        public double MembershipScore { get; set; }
        /// <summary>0–100 description/visual quality sub-score.</summary>
        public double DescriptionScore { get; set; }
        /// <summary>True when no invented proper names were flagged against book text.</summary>
        public bool NoInventedNames => SpeakersMissingFromBook.Count == 0;
    }

    public sealed class CharacterQuality
    {
        public string CastKey { get; set; } = "";
        public string Speaker { get; set; } = "";
        public bool HasDescription { get; set; }
        public bool HasVisualLock { get; set; }
        public bool HasWardrobe { get; set; }
        public bool HasSpecies { get; set; }
        public int DescriptionChars { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public static Report Evaluate(string? fountainText, string? castSeedsJson, string? bookText = null)
    {
        var report = new Report();
        var speakers = ExtractSpeakers(fountainText);
        report.Speakers = speakers.ToList();
        report.RequiredSpeakers = speakers
            .Where(s => !OptionalSpeakers.Contains(NormalizeSpeaker(s)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(bookText))
        {
            report.SpeakersMissingFromBook = FindSpeakersMissingFromBook(
                    report.RequiredSpeakers, bookText)
                .ToList();
            foreach (var name in report.SpeakersMissingFromBook)
            {
                report.Warnings.Add(
                    $"Speaking character '{name}' does not appear in the book text " +
                    "(possible invented name; prefer group tokens like CHILDREN for unnamed groups).");
            }
        }

        if (string.IsNullOrWhiteSpace(castSeedsJson))
        {
            report.Failures.Add(
                "No cast_seeds.json — cast package missing. Stage 1 alone is not a complete adaptation package.");
            report.Score = 0;
            return report;
        }

        Dictionary<string, JsonElement> seeds;
        try
        {
            seeds = ParseSeeds(castSeedsJson);
        }
        catch (Exception ex)
        {
            report.Failures.Add($"cast_seeds.json did not parse: {ex.Message}");
            report.Score = 0;
            return report;
        }

        report.CastKeys = seeds.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (seeds.Count == 0)
        {
            report.Failures.Add("character_seed_tokens is empty.");
            report.Score = 0;
            return report;
        }

        var points = 0.0;
        var maxPoints = 0.0;

        foreach (var speaker in report.RequiredSpeakers)
        {
            maxPoints += 10;
            var match = ResolveCastKey(speaker, seeds);
            if (match is null)
            {
                report.SpeakersMissingFromCast.Add(speaker);
                report.Failures.Add(
                    $"Speaking character '{speaker}' has no cast_seeds entry " +
                    $"(expected Character_{SanitizeKey(speaker)} or matching display name).");
                continue;
            }

            report.MatchedKeys.Add(match);
            points += 5; // membership
            var seed = seeds[match];
            var q = ScoreSeed(match, speaker, seed);
            report.Quality[match] = q;
            // description + visual detail (5 pts)
            var detail = 0.0;
            if (q.HasDescription && q.DescriptionChars >= 24) detail += 2.5;
            else if (q.HasDescription) detail += 1.0;
            else report.Failures.Add($"Cast '{match}' missing usable description.");

            if (q.HasVisualLock) detail += 1.5;
            else report.Warnings.Add($"Cast '{match}' missing visual_lock — portraits may drift.");

            if (q.HasWardrobe) detail += 0.5;
            else report.Warnings.Add($"Cast '{match}' missing wardrobe lock.");

            if (q.HasSpecies) detail += 0.5;
            else report.Warnings.Add($"Cast '{match}' missing species_kind.");

            points += Math.Min(5.0, detail);
        }

        // Extra cast not required for dialogue is fine (animals, background).
        foreach (var key in report.CastKeys)
        {
            if (report.MatchedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            var seed = seeds[key];
            var q = ScoreSeed(key, key, seed);
            report.Quality[key] = q;
            if (!q.HasDescription)
                report.Warnings.Add($"Non-speaking cast '{key}' has weak/empty description.");
        }

        report.Score = maxPoints <= 0
            ? (report.Failures.Count == 0 ? 100 : 0)
            : Math.Round(100.0 * points / maxPoints, 1);

        // Sub-scores for the cast-judge layer (benchmark / admin).
        var memberOk = report.RequiredSpeakers.Count == 0
            ? 100.0
            : 100.0 * report.MatchedKeys.Count / Math.Max(1, report.RequiredSpeakers.Count);
        report.MembershipScore = Math.Round(Math.Min(100.0, memberOk), 1);
        var descPoints = 0.0;
        var descMax = 0.0;
        foreach (var q in report.Quality.Values)
        {
            descMax += 5;
            if (q.HasDescription && q.DescriptionChars >= 24) descPoints += 2.5;
            else if (q.HasDescription) descPoints += 1.0;
            if (q.HasVisualLock) descPoints += 1.5;
            if (q.HasWardrobe) descPoints += 0.5;
            if (q.HasSpecies) descPoints += 0.5;
        }
        report.DescriptionScore = descMax <= 0
            ? 0
            : Math.Round(100.0 * descPoints / descMax, 1);

        foreach (var key in report.CastKeys)
        {
            seeds.TryGetValue(key, out var seedEl);
            string? castKind = null;
            string? display = null;
            string? desc = null;
            if (seedEl.ValueKind == JsonValueKind.Object)
            {
                if (seedEl.TryGetProperty("cast_kind", out var ck) && ck.ValueKind == JsonValueKind.String)
                    castKind = ck.GetString();
                if (seedEl.TryGetProperty("canonical_given_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                    display = cn.GetString();
                if (seedEl.TryGetProperty("description", out var ds) && ds.ValueKind == JsonValueKind.String)
                    desc = ds.GetString();
            }
            if (CastKindClassifier.IsGroup(key, display, castKind, desc))
                report.GroupCastKeys.Add(key);
        }

        if (report.RequiredSpeakers.Count == 0 && seeds.Count > 0)
            report.Warnings.Add("No non-narrator dialogue cues found — cast membership could not be cross-checked.");

        return report;
    }

    /// <summary>
    /// Speakers that look like invented proper names (title-case single tokens not found in book).
    /// Group words (CHILDREN, PUPILS, CROWD) and roles (TEACHER, NARRATOR) are allowed without book match.
    /// </summary>
    public static IReadOnlyList<string> FindSpeakersMissingFromBook(
        IEnumerable<string> speakers,
        string? bookText)
    {
        if (string.IsNullOrWhiteSpace(bookText))
            return Array.Empty<string>();

        var bookWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordToken.Matches(bookText))
            bookWords.Add(m.Value);

        var missing = new List<string>();
        foreach (var raw in speakers)
        {
            var s = NormalizeSpeaker(raw);
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (OptionalSpeakers.Contains(s)) continue;
            if (IsAllowedWithoutBookMention(s)) continue;

            // Multi-word speakers: all significant tokens should appear, or full phrase.
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var found = parts.All(p => bookWords.Contains(p) || IsAllowedWithoutBookMention(p));
            if (!found && !bookWords.Contains(s.Replace(" ", "")))
                missing.Add(s);
        }

        return missing
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ExtractSpeakers(string? fountainText)
    {
        if (string.IsNullOrWhiteSpace(fountainText))
            return Array.Empty<string>();

        // Line-scan (same assembly as BookToFountainConverter) — no Engine FountainParser.
        var list = new List<string>();
        foreach (var raw in BookToFountainConverter.EnumerateCharacterCueNames(fountainText))
        {
            var name = NormalizeSpeaker(raw);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            list.Add(name);
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string NormalizeSpeaker(string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.StartsWith('@')) t = t[1..].Trim();
        t = ExtensionStrip.Replace(t, "").Trim();
        // Collapse internal whitespace
        t = CommonRegex.Replace(t, @"\s+", " ");
        return t.ToUpperInvariant();
    }

    /// <summary>Public entry for Engine gates that need the same speaker normalization.</summary>
    public static string NormalizeSpeakerToken(string raw) => NormalizeSpeaker(raw);

    /// <summary>
    /// Public: map numbered generic speaker (or Character_Suitor_1 key suffix) to a group seed key.
    /// </summary>
    public static string? TryResolveNumberedToGroupKey(
        string speakerOrKey,
        IReadOnlyDictionary<string, JsonElement> seeds)
    {
        if (seeds.Count == 0) return null;
        var dict = seeds as Dictionary<string, JsonElement>
            ?? new Dictionary<string, JsonElement>(seeds, StringComparer.OrdinalIgnoreCase);
        var raw = (speakerOrKey ?? "").Trim();
        if (raw.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase))
            raw = CastKindClassifier.StripPrefix(raw).Replace('_', ' ');
        var want = NormalizeSpeaker(raw);
        return ResolveNumberedSpeakerToGroupKey(want, dict);
    }

    private static bool IsAllowedWithoutBookMention(string speaker)
    {
        // Roles / group chorus tokens common in adaptations of unnamed ensembles.
        if (speaker is "TEACHER" or "MOTHER" or "FATHER" or "PARENT" or "DOCTOR"
            or "NURSE" or "POLICE" or "OFFICER" or "GUARD" or "KING" or "QUEEN"
            or "PRINCE" or "PRINCESS" or "CHILDREN" or "CHILD" or "KIDS" or "PUPILS"
            or "STUDENTS" or "CROWD" or "CLASS" or "CLASSMATES" or "VILLAGERS"
            or "TOWNSFOLK" or "PEOPLE" or "GROUP" or "CHORUS" or "ENSEMBLE"
            or "BOY" or "GIRL" or "MAN" or "WOMAN" or "LAMB" or "DOG" or "CAT"
            or "ANIMAL" or "CREATURE" or "SUITOR" or "SUITORS")
            return true;

        // Numbered generics (SUITOR 1) are role labels, not invented proper names.
        if (ExtractNumberedStem(speaker) is not null)
            return true;

        return false;
    }

    private static string SanitizeKey(string speaker) =>
        CommonRegex.Replace(speaker, @"[^A-Za-z0-9]+", "_").Trim('_');

    /// <summary>
    /// Resolve a Fountain speaker cue to a cast_seeds key.
    /// North Star: numbered generics (SUITOR 1, MAN 2) map to an ensemble group seed
    /// (Character_Suitors / Character_Men) when present — do not invent individual plate keys.
    /// </summary>
    private static string? ResolveCastKey(string speaker, Dictionary<string, JsonElement> seeds)
    {
        var want = NormalizeSpeaker(speaker);
        var wantKey = JsonKeys.CharacterPrefix + SanitizeKey(want);

        foreach (var key in seeds.Keys)
        {
            if (key.Equals(wantKey, StringComparison.OrdinalIgnoreCase))
                return key;
            var suffix = CastKindClassifier.StripPrefix(key);
            if (NormalizeSpeaker(suffix).Equals(want, StringComparison.OrdinalIgnoreCase))
                return key;

            if (seeds[key].ValueKind != JsonValueKind.Object)
                continue;
            foreach (var prop in new[] { "canonical_given_name", "display_name", "name" })
            {
                if (seeds[key].TryGetProperty(prop, out var p) &&
                    p.ValueKind == JsonValueKind.String &&
                    NormalizeSpeaker(p.GetString() ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
        }

        // Numbered / ordinal generic → ensemble group (SUITOR 1 → Character_Suitors).
        var groupKey = ResolveNumberedSpeakerToGroupKey(want, seeds);
        if (groupKey is not null)
            return groupKey;

        return null;
    }

    /// <summary>
    /// SUITOR 1 / MAN 2 / FIRST OFFICER → matching group cast key when that seed is an ensemble.
    /// </summary>
    internal static string? ResolveNumberedSpeakerToGroupKey(
        string normalizedSpeaker,
        Dictionary<string, JsonElement> seeds)
    {
        if (string.IsNullOrWhiteSpace(normalizedSpeaker) || seeds.Count == 0)
            return null;

        var stem = ExtractNumberedStem(normalizedSpeaker);
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        // Candidate group labels from the stem (SUITOR → SUITORS, MAN → MEN / PEOPLE).
        var candidates = new List<string> { stem, stem + "S", stem + "ES" };
        if (stem.Equals("MAN", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange(new[] { "MEN", "PEOPLE", "CROWD" });
        if (stem.Equals("WOMAN", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange(new[] { "WOMEN", "PEOPLE", "CROWD" });
        if (stem.Equals("CHILD", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange(new[] { "CHILDREN", "KIDS" });
        if (stem.Equals("SUITOR", StringComparison.OrdinalIgnoreCase))
            candidates.Add("THE SUITORS");

        foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tryKey = JsonKeys.CharacterPrefix + SanitizeKey(cand);
            foreach (var key in seeds.Keys)
            {
                if (!key.Equals(tryKey, StringComparison.OrdinalIgnoreCase)
                    && !NormalizeSpeaker(CastKindClassifier.StripPrefix(key))
                        .Equals(NormalizeSpeaker(cand), StringComparison.OrdinalIgnoreCase))
                    continue;

                // Prefer true groups; still accept a singular stem key if that's all we have.
                var seed = seeds[key];
                string? castKind = null;
                string? display = null;
                string? desc = null;
                if (seed.ValueKind == JsonValueKind.Object)
                {
                    if (seed.TryGetProperty("cast_kind", out var ck) && ck.ValueKind == JsonValueKind.String)
                        castKind = ck.GetString();
                    if (seed.TryGetProperty("canonical_given_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                        display = cn.GetString();
                    if (seed.TryGetProperty("description", out var ds) && ds.ValueKind == JsonValueKind.String)
                        desc = ds.GetString();
                }
                var isGroup = CastKindClassifier.IsGroup(key, display, castKind, desc);
                // Numbered generics should land on groups when available.
                if (isGroup || key.Equals(tryKey, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
        }

        return null;
    }

    /// <summary>SUITOR 1 / FIRST SUITOR / SUITOR #2 → SUITOR; null if not numbered-generic shaped.</summary>
    internal static string? ExtractNumberedStem(string normalizedSpeaker)
    {
        var s = (normalizedSpeaker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s)) return null;

        // FIRST/SECOND/… STEM
        var m = CommonRegex.Match(s, @"^(FIRST|SECOND|THIRD|FOURTH|FIFTH|SIXTH|SEVENTH|EIGHTH|NINTH|TENTH|1ST|2ND|3RD|4TH|5TH)\s+(.+)$");
        if (m.Success)
            return m.Groups[2].Value.Trim();

        // STEM #? N  or  STEM ONE/TWO…
        m = CommonRegex.Match(s, @"^(.+?)\s*[#]?\s*(\d{1,2}|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN)$");
        if (m.Success)
        {
            var stem = m.Groups[1].Value.Trim();
            // Avoid stripping real names that end with roman noise — stem must look like a role noun.
            if (stem.Length >= 3 && !stem.Contains('\'') && stem.All(c => char.IsLetter(c) || c is ' ' or '-'))
                return stem.Replace(' ', ' ').Trim();
        }

        return null;
    }

    private static CharacterQuality ScoreSeed(string key, string speaker, JsonElement seed)
    {
        var q = new CharacterQuality { CastKey = key, Speaker = speaker };
        if (seed.ValueKind != JsonValueKind.Object)
        {
            q.Notes.Add("not an object");
            return q;
        }

        var desc = GetText(seed, "description");
        var vlock = GetText(seed, "visual_lock");
        var wardrobe = GetText(seed, "wardrobe_lock") ?? GetText(seed, "wardrobe_always");
        var species = GetText(seed, "species_kind") ?? GetText(seed, "species");

        q.DescriptionChars = desc?.Length ?? 0;
        q.HasDescription = !string.IsNullOrWhiteSpace(desc) && q.DescriptionChars >= 8;
        q.HasVisualLock = !string.IsNullOrWhiteSpace(vlock) && vlock.Length >= 8;
        q.HasWardrobe = !string.IsNullOrWhiteSpace(wardrobe);
        q.HasSpecies = !string.IsNullOrWhiteSpace(species);
        if (q.HasDescription && q.DescriptionChars < 40)
            q.Notes.Add("description is short for portrait generation");
        return q;
    }

    private static string? GetText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static Dictionary<string, JsonElement> ParseSeeds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement seedsEl = default;
        var found = false;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("character_seed_tokens", out seedsEl) &&
                seedsEl.ValueKind == JsonValueKind.Object)
                found = true;
            else if (root.TryGetProperty("global_production_variables", out var gpv) &&
                     gpv.ValueKind == JsonValueKind.Object &&
                     gpv.TryGetProperty("character_seed_tokens", out seedsEl) &&
                     seedsEl.ValueKind == JsonValueKind.Object)
                found = true;
            else if (root.TryGetProperty("cast_seeds", out var cs) &&
                     cs.ValueKind == JsonValueKind.Object &&
                     cs.TryGetProperty("character_seed_tokens", out seedsEl) &&
                     seedsEl.ValueKind == JsonValueKind.Object)
                found = true;
            // Whole object is character map (Character_* keys)
            else if (root.EnumerateObject().Any(p =>
                         p.Name.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                seedsEl = root;
                found = true;
            }
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!found)
            return map;

        foreach (var prop in seedsEl.EnumerateObject())
            map[prop.Name] = prop.Value.Clone();
        return map;
    }
}
