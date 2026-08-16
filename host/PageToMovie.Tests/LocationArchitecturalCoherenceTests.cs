using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

public class LocationArchitecturalCoherenceTests
{
    [Fact]
    public void Harmonize_Synchronizes_Architectural_Features_Between_Locations_With_Same_SettingAnchor()
    {
        var extSchool = new LocationSummary
        {
            Key = "Loc_Country_Lane",
            DisplayName = "EXT. COUNTRY LANE / SCHOOLHOUSE",
            SettingAnchor = "Schoolhouse",
            ArchitecturalFeatures = "3 tall 6-pane sash windows with white trim, red horizontal clapboard siding, single bell on ridge",
            Description = "Rural dirt lane rolling towards a small one-room schoolhouse.",
            VisualLock = "Red horizontal clapboard, bell on roof.",
        };

        var intSchool = new LocationSummary
        {
            Key = "Loc_Schoolroom",
            DisplayName = "INT. SCHOOLROOM",
            SettingAnchor = "Schoolhouse",
            Description = "Sunlit wooden classroom with student benches.",
            VisualLock = "Wooden student desks, blackboard on front wall.",
        };

        var independent = new LocationSummary
        {
            Key = "Loc_Open_Ocean",
            DisplayName = "EXT. OPEN OCEAN",
            SettingAnchor = "Ocean",
            Description = "Endless deep blue ocean waves under overcast skies.",
            VisualLock = "Open ocean, rolling waves.",
        };

        var list = new List<LocationSummary> { extSchool, intSchool, independent };

        LocationArchitecturalCoherence.Harmonize(list);

        // Verify that extSchool and intSchool both have the architectural anchor in VisualLock
        Assert.Contains("6-pane sash windows", extSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural anchor (Schoolhouse)", extSchool.VisualLock, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("6-pane sash windows", intSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural anchor (Schoolhouse)", intSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(extSchool.ArchitecturalFeatures, intSchool.ArchitecturalFeatures);

        // Verify that independent location remains untouched
        Assert.DoesNotContain("Architectural anchor", independent.VisualLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Harmonize_Ignores_Locations_Without_SettingAnchor_Or_Singletons()
    {
        var single = new List<LocationSummary>
        {
            new() { Key = "Loc_Solitary", DisplayName = "EXT. DESERT", SettingAnchor = "Desert" },
            new() { Key = "Loc_Unknown", DisplayName = "INT. ROOM" },
        };

        LocationArchitecturalCoherence.Harmonize(single);
        Assert.Empty(single[0].VisualLock);
        Assert.Empty(single[1].VisualLock);
    }
}
