using PageToMovie.Core.Utils;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CharacterLookEditTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void VariantCount_is_3_for_generate_and_1_for_iterative_edit(bool iterative, int expected)
    {
        Assert.Equal(expected, CharacterLookEdit.VariantCount(iterative));
    }

    [Fact]
    public void Waiting_copy_uses_count_not_hardcoded_three()
    {
        Assert.Equal(
            "Generating 1 look for Ada…",
            CharacterLookEdit.GeneratingLooksMessage("Ada", 1));
        Assert.Equal(
            "Generating 3 looks for Ada…",
            CharacterLookEdit.GeneratingLooksMessage("Ada", 3));
        Assert.Equal(
            "Generating 3 looks for Ada…",
            CharacterLookEdit.GeneratingLooksMessage("Ada", 0));
    }

    [Fact]
    public void Generated_options_heading_follows_existing_count()
    {
        Assert.Equal("Generated options (1 variant):", CharacterLookEdit.GeneratedOptionsHeading(1));
        Assert.Equal("Generated options (3 variants):", CharacterLookEdit.GeneratedOptionsHeading(3));
        Assert.Equal("Generated options (4 variants):", CharacterLookEdit.GeneratedOptionsHeading(4));
    }

    [Fact]
    public void AutoLockBest_is_off_for_iterative_edit()
    {
        Assert.False(CharacterLookEdit.ShouldAutoLockBest(iterativeEdit: true));
        Assert.True(CharacterLookEdit.ShouldAutoLockBest(iterativeEdit: false));
    }

    [Fact]
    public void BuildImageEditPrompt_instruction_wins_over_conflicting_description_color()
    {
        var prompt = CharacterLookEdit.BuildImageEditPrompt(
            "A young woman with brunette hair and a wool coat",
            "brunette hair, brown eyes",
            "red hair");

        var instructionAt = prompt.IndexOf("Instruction (this wins", StringComparison.Ordinal);
        var descAt = prompt.IndexOf("Base description", StringComparison.Ordinal);
        var lockAt = prompt.IndexOf("Visual lock", StringComparison.Ordinal);
        Assert.True(instructionAt > 0);
        Assert.True(descAt > 0 && descAt < instructionAt);
        Assert.True(lockAt > 0 && lockAt < instructionAt);
        Assert.Contains("red hair", prompt, StringComparison.Ordinal);
        Assert.Contains("conflicts with the instruction", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Instruction: red hair. Visual lock:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyTweakToLookText_prepends_instruction_so_next_gen_is_not_fighting_old_color()
    {
        var (desc, vis) = CharacterLookEdit.ApplyTweakToLookText(
            "A young woman with brunette hair",
            "brunette hair, wool coat",
            "red hair");
        Assert.StartsWith("red hair.", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brunette", desc, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("red hair.", vis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wool coat", vis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyTweakToLookText_does_not_duplicate_the_same_instruction()
    {
        var (desc, vis) = CharacterLookEdit.ApplyTweakToLookText(
            "red hair. A young woman with brunette hair",
            "red hair. wool coat",
            "red hair");
        Assert.Equal("red hair. A young woman with brunette hair", desc);
        Assert.Equal("red hair. wool coat", vis);
    }

    [Fact]
    public void BuildRegenRequest_iterative_edit_is_one_look_and_does_not_AutoLockBest()
    {
        var req = Characters.CharactersLookPipeline.BuildRegenRequest(
            projectId: "p1",
            charKey: "Character_Ada",
            hasImageEdit: true,
            includePref: true,
            variants: new List<int> { 1, 2 },
            books: new List<int> { 0 },
            sendOrder: new List<string> { "p", "v1" },
            maxSend: 3,
            description: "A young woman with brunette hair",
            visualLock: "brunette hair",
            imageEditInstruction: "red hair",
            selectedSeedCount: 2);

        Assert.Equal(1, req.Count);
        Assert.True(req.IterativeEdit);
        Assert.False(req.AutoLockBest);
        Assert.Equal("preferred_only", req.SeedMode);
        Assert.Equal("red hair", req.ImageEditInstruction);
        Assert.Equal("A young woman with brunette hair", req.DescriptionOverride);
        Assert.False(req.PersistDescription);
        Assert.Equal(new List<string> { "p" }, req.SeedOrderKeys);
    }

    [Fact]
    public void BuildRegenRequest_generate_from_description_is_three_looks_and_AutoLockBest()
    {
        var req = Characters.CharactersLookPipeline.BuildRegenRequest(
            projectId: "p1",
            charKey: "Character_Ada",
            hasImageEdit: false,
            includePref: false,
            variants: new List<int>(),
            books: new List<int>(),
            sendOrder: new List<string>(),
            maxSend: 3,
            description: "A pale adult in a dark coat",
            visualLock: null,
            imageEditInstruction: "",
            selectedSeedCount: 0);

        Assert.Equal(3, req.Count);
        Assert.False(req.IterativeEdit);
        Assert.True(req.AutoLockBest);
        Assert.Null(req.ImageEditInstruction);
        Assert.True(req.PersistDescription);
        Assert.Equal("none", req.SeedMode);
    }

    [Fact]
    public void Compare_mode_frame_click_zooms_and_does_not_lock()
    {
        var razor = ReadCharactersLookPanel();
        Assert.Contains("OnFrameClick=\"@(() => LookPipe.OpenLookZoom(c))\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("OnFrameClick=\"@(() => LookPipe.LockCandidateAsync(c))\"", razor, StringComparison.Ordinal);
        Assert.Contains("OnUse=\"@(() => LookPipe.LockCandidateAsync(c))\"", razor, StringComparison.Ordinal);
        Assert.Contains("Click to zoom", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Click to save this look", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Generating three looks", razor, StringComparison.Ordinal);
        Assert.Contains("GeneratingLooksMessage", razor, StringComparison.Ordinal);
        Assert.Contains("UseThisLook", razor, StringComparison.Ordinal);
    }

    private static string ReadCharactersLookPanel()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", "CharactersLookPanel.razor");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException("CharactersLookPanel.razor");
    }
}
