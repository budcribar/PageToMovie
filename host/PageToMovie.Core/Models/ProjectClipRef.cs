namespace PageToMovie.Core.Models;

/// <summary>
/// Shared project + scene + clip identity used by review, sidecars, cost, and media registry.
/// </summary>
public readonly record struct ProjectClipRef(string ProjectId, int Scene, int Clip);
