namespace PageToMovie.Core.Utils;

/// <summary>
/// Tag names for the fields of a clip's <c>visual_prompt</c>. Stage 2 emits them, the clip editor
/// splits on them, ShotPlanLint reads them, and ClipVideoPromptBuilder salvages look from them —
/// one spelling, in one place, so none of those can drift apart.
/// </summary>
public static class PromptFieldTags
{
    public const string StyleLock = "StyleLock";
    public const string Setting = "Setting";
    public const string Cast = "Cast";
    public const string Action = "Action";
    public const string Sound = "Sound";
    public const string Speech = "Speech";
    public const string MustNot = "MustNot";
    public const string Wardrobe = "Wardrobe";
    public const string Lighting = "Lighting";
    public const string Camera = "Camera";
    public const string Performance = "Performance";
    public const string Optics = "Optics";
    public const string Grade = "Grade";

    /// <summary>
    /// A cast member's human display name inside a <c>&lt;Characters&gt;</c> line. Compression
    /// drops it once the keys are aliased to C1/C2 — a tag makes that an exact strip instead of a
    /// bracket match that had to run after aliasing and silently skipped the voice-only line.
    /// </summary>
    public const string Name = "Name";
}
