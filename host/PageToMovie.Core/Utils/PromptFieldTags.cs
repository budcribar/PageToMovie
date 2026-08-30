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

    /// <summary>
    /// Legacy only. Stage 2 no longer writes a <c>&lt;Cast&gt;</c> roster — Characters +
    /// CastCount at generation time are the single on-screen list. The tag stays because
    /// older plans still contain one, and generation-time sanitize plus the clip editor
    /// have to recognise it so the same names are not sent twice.
    /// </summary>
    public const string Cast = "Cast";
    public const string Action = "Action";
    public const string Sound = "Sound";

    /// <summary>
    /// Legacy only. Stage 2 no longer writes a <c>&lt;Speech&gt;</c> block: the spoken line lives
    /// in the clip's <c>audio_payload</c> and reaches the model once, as the AUDIO block built at
    /// generation time. The tag stays because plans built before that still contain one, and both
    /// the generation-time strip and the clip editor have to recognise it to keep the line from
    /// being sent — or edited — twice.
    /// </summary>
    public const string Speech = "Speech";

    /// <summary>
    /// Legacy only. Stage 2 no longer writes a <c>&lt;MustNot&gt;</c> tag — beat
    /// <c>must_not</c> feeds the clip's <c>negative_prompt</c> and reaches the model
    /// once, as the generation-time <c>&lt;Negative&gt;</c> block (with global + medium
    /// negatives). The tag stays so leftover plan copies can be stripped instead of
    /// billed again.
    /// </summary>
    public const string MustNot = "MustNot";
    public const string Wardrobe = "Wardrobe";
    public const string Lighting = "Lighting";
    public const string Camera = "Camera";
    public const string Performance = "Performance";
    public const string Optics = "Optics";
    public const string Grade = "Grade";

    /// <summary>
    /// Legacy only. Character lines no longer emit a display-name tag — the catalog key is the
    /// one spelling. Compression still strips leftover <c>&lt;Name&gt;</c> from older prompts
    /// after aliasing the name to the same C-index as the key.
    /// </summary>
    public const string Name = "Name";
}
