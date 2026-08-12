using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("loc:Loc_Flashback_Aeolus", "loc_Loc_Flashback_Aeolus")]
    [InlineData("cast:Odysseus", "cast_Odysseus")]
    [InlineData("scene:12", "scene_12")]
    [InlineData("ok_name", "ok_name")]
    public void SanitizeFileName_strips_portable_invalid_chars(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeRelativePath_leases_entry_windows_safe()
    {
        var rel = FileNameSanitizer.SanitizeRelativePath("budcribar/The_Odyssey2/leases/loc:Loc_Flashback_Aeolus.json");
        Assert.DoesNotContain(":", rel);
        Assert.EndsWith("loc_Loc_Flashback_Aeolus.json", rel);
        Assert.Contains("/leases/", rel);
    }
}
