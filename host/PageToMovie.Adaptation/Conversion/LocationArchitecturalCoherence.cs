using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Harmonizes architectural and visual continuity across paired/connected locations
/// (e.g. INT. SCHOOLROOM and EXT. SCHOOLHOUSE / COUNTRY LANE) so that window styles,
/// siding/wall materials, and structural details stay 100% consistent across takes.
/// </summary>
public static class LocationArchitecturalCoherence
{
    private static readonly Regex WindowFeatureRegex = new(
        @"\b(?:(?:three|two|four|several|tall|large|small|narrow|wide|\d+[- ]pane|sash|multi-pane|stained[- ]glass|bay|arched|casement|diamond[- ]pane|shuttered|latticed|double-hung|glass)\s+)+windows?\b[^.,;:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex MaterialFeatureRegex = new(
        @"\b(?:(?:red[- ]painted|horizontal|weathered|timber|stone|brick|clapboard|log|granite|adobe|plank|wood|wooden|stucco|cobblestone)\s+)+(?:siding|walls?|masonry|foundation|exterior|interior|facade)\b[^.,;:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex RoofOrStructureFeatureRegex = new(
        @"\b(?:(?:bell|cupola|steep|gable|thatched|shingle|chimney|porch|veranda|tower|spire|turret|steeple)\s+)+(?:roof|ridge|cupola|tower|porch|structure)?\b[^.,;:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    private static readonly Regex StructuralStemPrefixRegex = new(
        @"^(int|ext|interior|exterior)[\.\s_-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        CommonRegex.Timeout);

    /// <summary>
    /// Harmonize architectural visual locks across a set of locations.
    /// </summary>
    public static void Harmonize(IList<LocationSummary> locations)
    {
        if (locations == null || locations.Count <= 1)
            return;

        // Group by explicit setting anchor or extracted structural stem
        var groups = locations
            .GroupBy(loc => ResolveSettingGroup(loc), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            HarmonizeGroup(group.ToList());
        }
    }

    /// <summary>
    /// Resolves the setting group for a location (explicit SettingAnchor or derived place stem).
    /// </summary>
    public static string ResolveSettingGroup(LocationSummary loc)
    {
        if (!string.IsNullOrWhiteSpace(loc.SettingAnchor))
            return loc.SettingAnchor.Trim();

        var name = (loc.DisplayName ?? loc.Key ?? "").Trim();
        var clean = StructuralStemPrefixRegex.Replace(name, "").Trim();

        // Common building stems
        var tokens = clean.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";

        // Check for common paired building keywords
        foreach (var token in tokens)
        {
            var t = token.ToLowerInvariant();
            if (t.Contains("school") || t.Contains("classroom") || t.Contains("schoolhouse") || t.Contains("schoolroom"))
                return "Schoolhouse";
            if (t.Contains("tavern") || t.Contains("pub") || t.Contains("inn") || t.Contains("bar"))
                return "Tavern";
            if (t.Contains("cabin") || t.Contains("cottage") || t.Contains("hut") || t.Contains("shack"))
                return "Cabin";
            if (t.Contains("castle") || t.Contains("fortress") || t.Contains("palace") || t.Contains("chateau"))
                return "Castle";
            if (t.Contains("church") || t.Contains("chapel") || t.Contains("cathedral") || t.Contains("temple"))
                return "Church";
            if (t.Contains("barn") || t.Contains("stable") || t.Contains("shed"))
                return "Barn";
            if (t.Contains("manor") || t.Contains("mansion") || t.Contains("estate") || t.Contains("house"))
                return "Manor";
            if (t.Contains("lab") || t.Contains("laboratory") || t.Contains("workshop") || t.Contains("study"))
                return "Laboratory";
        }

        return clean;
    }

    private static void HarmonizeGroup(List<LocationSummary> group)
    {
        var collectedWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectedRoofFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var loc in group)
        {
            var text = $"{loc.Description} {loc.VisualLock} {loc.ArchitecturalFeatures}";

            foreach (Match m in WindowFeatureRegex.Matches(text))
            {
                var val = m.Value.Trim();
                if (val.Length > 3) collectedWindows.Add(val);
            }

            foreach (Match m in MaterialFeatureRegex.Matches(text))
            {
                var val = m.Value.Trim();
                if (val.Length > 3) collectedMaterials.Add(val);
            }

            foreach (Match m in RoofOrStructureFeatureRegex.Matches(text))
            {
                var val = m.Value.Trim();
                if (val.Length > 3) collectedRoofFeatures.Add(val);
            }
        }

        var parts = new List<string>();
        if (collectedWindows.Count > 0)
            parts.Add(string.Join(", ", collectedWindows));
        if (collectedMaterials.Count > 0)
            parts.Add(string.Join(", ", collectedMaterials));
        if (collectedRoofFeatures.Count > 0)
            parts.Add(string.Join(", ", collectedRoofFeatures));

        if (parts.Count == 0) return;

        var unifiedFeatures = string.Join("; ", parts);
        var lockClause = $"Architectural anchor: {unifiedFeatures}. Structure, window shapes, and wall materials must match between interior and exterior.";

        foreach (var loc in group)
        {
            if (string.IsNullOrWhiteSpace(loc.ArchitecturalFeatures))
                loc.ArchitecturalFeatures = unifiedFeatures;

            if (string.IsNullOrWhiteSpace(loc.VisualLock))
            {
                loc.VisualLock = lockClause;
            }
            else if (!loc.VisualLock.Contains("Architectural anchor", StringComparison.OrdinalIgnoreCase))
            {
                loc.VisualLock = $"{loc.VisualLock.TrimEnd('.')}. {lockClause}";
            }
        }
    }
}
