using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The one-writer passes take directives out of prose someone else wrote. Each case here is a
/// shape where removing the phrase used to leave the sentence broken, or where a substring match
/// caught a garment it had no business touching.
/// </summary>
public class CameraAndWardrobeProseTests
{
    [Fact]
    public void A_camera_order_welded_into_a_sentence_stays_rather_than_break_the_beat()
    {
        Assert.Equal(
            "He steps into a close-up shot of the letter and reads it aloud",
            CameraTagWriter.StripFromAction("He steps into a close-up shot of the letter and reads it aloud."));

        Assert.Equal(
            "Nick crosses to the window in a slow push-in as he speaks the confession",
            CameraTagWriter.StripFromAction("Nick crosses to the window in a slow push-in as he speaks the confession."));
    }

    [Fact]
    public void A_clause_that_is_only_camera_orders_still_goes()
    {
        var stripped = CameraTagWriter.StripFromAction(
            "Character faces the window. Camera behind, back to camera. 35mm lens, shallow depth of field.");

        Assert.Contains("faces the window", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("camera behind", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35mm", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optics_language_leaves_with_its_own_clause_and_no_debris()
    {
        Assert.Equal(
            "Medium close-up, 50mm lens",
            CameraTagWriter.SanitizeCameraProse("Medium close-up, 50mm lens, shallow depth of field with the lantern in frame"));

        // Used to leave "Wide shot, 24mm,, slow dolly in".
        Assert.Equal(
            "Wide shot, 24mm, slow dolly in",
            CameraTagWriter.SanitizeCameraProse("Wide shot, 24mm, deep focus, slow dolly in"));

        Assert.Equal(
            "Close-up, 85mm",
            CameraTagWriter.SanitizeCameraProse("Close-up, 85mm, f/1.8, creamy soft bokeh"));
    }

    [Fact]
    public void A_wardrobe_clause_leaves_whole_and_the_face_stays()
    {
        var locked = CharacterVisualTextScrubber.StripOutfitFromVisualLock(
            "Tall gaunt man, deep-set pale blue eyes, a livid scar over his left eye, "
            + "wears a red wool coat over a grey waistcoat",
            ["red wool coat", "grey waistcoat"]);

        Assert.Equal(
            "Tall gaunt man, deep-set pale blue eyes, a livid scar over his left eye",
            locked);
        Assert.DoesNotContain("wears a", locked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identity_in_the_same_clause_as_a_garment_survives_it()
    {
        Assert.Equal(
            "green eyes",
            CharacterVisualTextScrubber.StripOutfitFromVisualLock("green eyes and a red coat", ["red coat"]));
    }

    [Fact]
    public void A_new_layer_lands_even_when_its_name_ends_in_one_already_worn()
    {
        var list = new List<string> { "coat" };
        WardrobeState.PrependLayers(list, ["yellow raincoat"]);

        Assert.Equal(["yellow raincoat", "coat"], list);
    }

    [Fact]
    public void Taking_off_a_cap_leaves_the_cape_on()
    {
        var list = new List<string> { "cape", "black boots" };
        WardrobeState.RemoveItems(list, ["cap"]);

        Assert.Equal(["cape", "black boots"], list);
    }

    [Fact]
    public void The_same_garment_named_loosely_is_still_the_same_garment()
    {
        Assert.True(WardrobeState.AlreadyHas(["red winter coat"], "coat"));
        Assert.True(WardrobeState.IsSameGarment("black boots", "boots"));
        Assert.False(WardrobeState.IsSameGarment("cape", "cap"));
        Assert.False(WardrobeState.IsSameGarment("undershirt", "shirt"));
    }
}
