namespace PageToMovie.Engine.Deterministic;

/// <summary>
/// Marks code whose result is produced locally without model or network access.
/// Validators, parsers, normalization, estimators, and heuristic fallbacks belong here.
/// </summary>
public static class NamespaceMarker
{
    /// <summary>Stable kind token for architecture tests that scan this namespace.</summary>
    public const string Kind = "deterministic";
}
