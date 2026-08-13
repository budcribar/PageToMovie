namespace PageToMovie.Core.Utils;

/// <summary>
/// Shared Regex catalog and timeout-safe static helpers. Sonar S6444 requires a match timeout
/// on every Regex construction / static call so a crafted input cannot hang the process.
/// Timeout wrappers and compiled catalog patterns live in TimeoutSafeRegex.cs (this partial)
/// and compile into Fountain as FountainRegex so that leaf module has no PageToMovie.* references.
/// </summary>
public static partial class CommonRegex
{
}
