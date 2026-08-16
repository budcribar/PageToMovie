using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Models;
using Xunit;

namespace PageToMovie.Tests;

public class LocationArchitecturalCoherenceTests
{
    [Fact]
    public void Harmonize_Propagates_Window_And_Material_Features_Between_Paired_Locations()
    {
        var extSchool = new LocationSummary
        {
            Key = "Loc_Country_Lane",
            DisplayName = "EXT. COUNTRY LANE / SCHOOLHOUSE",
            SettingAnchor = "Schoolhouse",
            Description = "Rural dirt lane rolling towards a small one-room schoolhouse with red-painted wood clapboard siding and a bell on the roof ridge.",
            VisualLock = "Red horizontal clapboard, bell on roof.",
        };

        var intSchool = new LocationSummary
        {
            Key = "Loc_Schoolroom",
            DisplayName = "INT. SCHOOLROOM",
            SettingAnchor = "Schoolhouse",
            Description = "Sunlit wooden classroom with student benches. Warm morning light streams through three tall 6-pane sash windows.",
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

        // Verify that extSchool received the 6-pane sash window details in its VisualLock
        Assert.Contains("6-pane sash windows", extSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural anchor", extSchool.VisualLock, StringComparison.OrdinalIgnoreCase);

        // Verify that intSchool received the red-painted clapboard details in its VisualLock
        Assert.Contains("red-painted", intSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clapboard", intSchool.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural anchor", intSchool.VisualLock, StringComparison.OrdinalIgnoreCase);

        // Verify that independent location remains untouched
        Assert.DoesNotContain("Architectural anchor", independent.VisualLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Harmonize_Detects_Paired_Locations_By_Place_Stem_When_Anchor_Is_Omitted()
    {
        var tavernExt = new LocationSummary
        {
            Key = "Loc_Tavern_Exterior",
            DisplayName = "EXT. BOAR'S HEAD TAVERN - NIGHT",
            Description = "Muddy cobblestone street in front of a half-timbered stone tavern with diamond-pane windows and a heavy oak door.",
        };

        var tavernInt = new LocationSummary
        {
            Key = "Loc_Tavern_Common_Room",
            DisplayName = "INT. TAVERN COMMON ROOM - NIGHT",
            Description = "Smoky room lit by tallow candles and a roaring stone masonry fireplace.",
        };

        var list = new List<LocationSummary> { tavernExt, tavernInt };

        LocationArchitecturalCoherence.Harmonize(list);

        Assert.Contains("diamond-pane windows", tavernInt.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural anchor", tavernInt.VisualLock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stone masonry", tavernExt.VisualLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Harmonize_Handles_Empty_Or_Single_Location_Gracefully()
    {
        var single = new List<LocationSummary>
        {
            new() { Key = "Loc_Solitary", DisplayName = "EXT. DESERT" },
        };

        LocationArchitecturalCoherence.Harmonize(single);
        Assert.Empty(single[0].VisualLock);
    }
}
