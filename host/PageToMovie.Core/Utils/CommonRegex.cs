using System.Text.RegularExpressions;

namespace PageToMovie.Core.Utils;

/// <summary>
/// Shared Regex catalog and timeout-safe static helpers. Sonar S6444 requires a match timeout
/// on every Regex construction / static call so a crafted input cannot hang the process.
/// Timeout wrappers live in TimeoutSafeRegex.cs (this partial) and compile into Fountain as
/// FountainRegex so that leaf module has no PageToMovie.* references.
/// </summary>
public static partial class CommonRegex
{
    /// <summary>Default match budget for every helper and compiled pattern here.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(MatchTimeoutSeconds);

    /// <summary>Matches consecutive whitespace characters (\s+).</summary>
    public static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(MatchTimeoutSeconds));

    /// <summary>Matches consecutive dots or dots with surrounding spaces (\s*\.\s*\.+).</summary>
    public static readonly Regex DotCollapse = new(@"\s*\.\s*\.+", RegexOptions.Compiled, TimeSpan.FromSeconds(MatchTimeoutSeconds));

    /// <summary>Matches standard HTML tags (<[^>]+>).</summary>
    public static readonly Regex HtmlTags = new(@"<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(MatchTimeoutSeconds));
}
