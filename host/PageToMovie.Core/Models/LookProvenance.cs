namespace PageToMovie.Core.Models;

/// <summary>
/// Where a character's on-screen appearance came from.
///
/// The pipeline has to invent a look for a character the source never describes — an image
/// model needs pixels, and "no description" is not a portrait. What it must not do is launder
/// that invention into the same field, with the same authority, as a fact from the book. An
/// invented hair colour sitting in <c>visual_lock</c> — the must-never-drift field — then rides
/// into every clip prompt for the rest of the film as though the author had written it.
///
/// Only <see cref="Invented"/> changes behaviour. The rest read as "the source backs this".
/// </summary>
public enum LookProvenance
{
    /// <summary>
    /// Extracted before provenance was recorded. Treated exactly as <see cref="Sourced"/>:
    /// an old seed's look is not voided on a guess. Re-extracting the cast upgrades it.
    /// </summary>
    Unspecified = 0,

    /// <summary>The book or screenplay states it.</summary>
    Sourced,

    /// <summary>
    /// Not stated, but constrained by what is — era, setting, occupation, age band. Honest
    /// narrowing rather than free invention, so it keeps <c>visual_lock</c> standing.
    /// </summary>
    Inferred,

    /// <summary>
    /// Nothing upstream supports it. The pipeline authored it so a portrait could exist.
    /// Never enters <c>visual_lock</c>; yields to a reference image the moment one exists.
    /// </summary>
    Invented,
}

/// <summary>Seed-file spelling of <see cref="LookProvenance"/> — one vocabulary for prompt, store and UI.</summary>
public static class LookProvenanceTokens
{
    /// <summary>Field name in <c>cast_seeds.json</c> and in the cast-extract prompt schema.</summary>
    public const string SeedKey = "look_provenance";

    public const string Sourced = "sourced";
    public const string Inferred = "inferred";
    public const string Invented = "invented";

    public static LookProvenance Parse(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        Sourced => LookProvenance.Sourced,
        Inferred => LookProvenance.Inferred,
        Invented => LookProvenance.Invented,
        _ => LookProvenance.Unspecified,
    };

    /// <summary>Null for <see cref="LookProvenance.Unspecified"/> so an unknown value is not written back as one.</summary>
    public static string? ToToken(LookProvenance value) => value switch
    {
        LookProvenance.Sourced => Sourced,
        LookProvenance.Inferred => Inferred,
        LookProvenance.Invented => Invented,
        _ => null,
    };

    /// <summary>True when the look is the pipeline's own invention rather than the story's.</summary>
    public static bool IsInvented(string? token) => Parse(token) == LookProvenance.Invented;
}
