namespace PageToMovie.Core.Utils;

/// <summary>
/// Shared look-edit rules for Characters: generate-3 vs iterative tweak-1,
/// image-edit prompt (instruction wins), and merging a tweak into look text.
/// </summary>
public static class CharacterLookEdit
{
    public const int MaxVariants = 6;
    public const int GenerateLooksCount = 3;
    public const int TweakLooksCount = 1;

    public static int VariantCount(bool iterativeEdit) =>
        iterativeEdit ? TweakLooksCount : GenerateLooksCount;

    /// <summary>
    /// Vision ranking across siblings is for generate-3 only. An iterative tweak
    /// must lock the new plate — never re-pick the pre-tweak preferred.
    /// </summary>
    public static bool ShouldAutoLockBest(bool iterativeEdit) => !iterativeEdit;

    public static string GeneratingLooksMessage(string displayName, int count)
    {
        var n = count > 0 ? count : GenerateLooksCount;
        var looks = n == 1 ? "1 look" : $"{n} looks";
        return $"Generating {looks} for {displayName}…";
    }

    public static string GeneratedOptionsHeading(int existingCount)
    {
        var n = existingCount < 0 ? 0 : existingCount;
        var variants = n == 1 ? "1 variant" : $"{n} variants";
        return $"Generated options ({variants}):";
    }

    /// <summary>
    /// Image-edit prompt: identity comes from the attached plate; the instruction
    /// is last and wins over any conflicting trait in description / visual lock.
    /// </summary>
    public static string BuildImageEditPrompt(string? description, string? visualLock, string instruction)
    {
        var inst = (instruction ?? "").Trim();
        var sb = new System.Text.StringBuilder();
        sb.Append("Edit this character reference image. Keep the same person, face identity, and era. ");
        sb.Append("Change only what the instruction asks. ");
        if (!string.IsNullOrWhiteSpace(visualLock))
            sb.Append("Visual lock (background only — ignore any trait that conflicts with the instruction): ")
                .Append(visualLock.Trim()).Append(". ");
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append("Base description (background only — ignore any trait that conflicts with the instruction): ")
                .Append(description.Trim()).Append(". ");
        sb.Append("Instruction (this wins — apply it even when it conflicts with the description or visual lock): ")
            .Append(inst);
        if (inst.Length > 0 && !inst.EndsWith('.'))
            sb.Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Persist a successful tweak into look text so the next generate is not
    /// fighting the pre-tweak wording. Instruction is prepended (winning clause).
    /// </summary>
    public static (string Description, string VisualLock) ApplyTweakToLookText(
        string? description, string? visualLock, string instruction)
    {
        var inst = (instruction ?? "").Trim();
        return (PrependUniqueClause(description, inst), PrependUniqueClause(visualLock, inst));
    }

    private static string PrependUniqueClause(string? existing, string instruction)
    {
        var e = (existing ?? "").Trim();
        if (string.IsNullOrWhiteSpace(instruction))
            return e;
        if (e.Contains(instruction, StringComparison.OrdinalIgnoreCase))
            return e;
        if (string.IsNullOrWhiteSpace(e))
            return instruction;
        var head = instruction.TrimEnd('.');
        return head + ". " + e;
    }
}
