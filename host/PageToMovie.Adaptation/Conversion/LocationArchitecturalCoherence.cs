using PageToMovie.Core.Models;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Synchronizes architectural visual locks across locations that share an AI-assigned
/// <c>SettingAnchor</c> (e.g. "Schoolhouse", "Tavern", "Castle") so that window styles,
/// materials, and structural details emitted by Stage-1 stay 100% consistent across takes.
/// Pure metadata propagation — no regexes or keyword dictionaries.
/// </summary>
public static class LocationArchitecturalCoherence
{
    /// <summary>
    /// Synchronize architectural visual locks across locations sharing a SettingAnchor.
    /// </summary>
    public static void Harmonize(IList<LocationSummary> locations)
    {
        if (locations == null || locations.Count <= 1)
            return;

        // Group strictly by AI-assigned SettingAnchor
        var groups = locations
            .Where(loc => !string.IsNullOrWhiteSpace(loc.SettingAnchor))
            .GroupBy(loc => loc.SettingAnchor!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            HarmonizeGroup(group.ToList());
        }
    }

    private static void HarmonizeGroup(List<LocationSummary> group)
    {
        // Collect explicit architectural features across the group
        var collectedFeatures = group
            .Select(loc => loc.ArchitecturalFeatures?.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (collectedFeatures.Count == 0) return;

        var unifiedFeatures = string.Join("; ", collectedFeatures);
        var lockClause = $"Architectural anchor ({group[0].SettingAnchor}): {unifiedFeatures}. Structure, window shapes, and wall materials must match between interior and exterior.";

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
