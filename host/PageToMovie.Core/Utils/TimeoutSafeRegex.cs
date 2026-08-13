using System.Text.RegularExpressions;

#if PAGETOMOVIE_FOUNTAIN
namespace PageToMovie.Fountain;

/// <summary>
/// Timeout-safe Regex helpers for Fountain parsing. Same source as
/// <c>PageToMovie.Core.Utils.CommonRegex</c> — compiled into this leaf assembly so Fountain
/// stays dependency-free (no PageToMovie.* references).
/// </summary>
internal static class FountainRegex
#else
namespace PageToMovie.Core.Utils;

/// <summary>
/// Timeout-safe Regex helpers (Sonar S6444). Shared source with Fountain's <c>FountainRegex</c>
/// via a linked compile; do not clone these wrappers.
/// </summary>
public static partial class CommonRegex
#endif
{
#if PAGETOMOVIE_FOUNTAIN
    /// <summary>Default match budget for every helper and compiled pattern here.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
#endif

    public static Regex Create(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options, Timeout);

    public static bool IsMatch(string input, string pattern) =>
        Regex.IsMatch(input ?? "", pattern, RegexOptions.None, Timeout);

    public static bool IsMatch(string input, string pattern, RegexOptions options) =>
        Regex.IsMatch(input ?? "", pattern, options, Timeout);

    public static Match Match(string input, string pattern) =>
        Regex.Match(input ?? "", pattern, RegexOptions.None, Timeout);

    public static Match Match(string input, string pattern, RegexOptions options) =>
        Regex.Match(input ?? "", pattern, options, Timeout);

    public static MatchCollection Matches(string input, string pattern) =>
        Regex.Matches(input ?? "", pattern, RegexOptions.None, Timeout);

    public static MatchCollection Matches(string input, string pattern, RegexOptions options) =>
        Regex.Matches(input ?? "", pattern, options, Timeout);

    public static string Replace(string input, string pattern, string replacement) =>
        Regex.Replace(input ?? "", pattern, replacement ?? "", RegexOptions.None, Timeout);

    public static string Replace(string input, string pattern, string replacement, RegexOptions options) =>
        Regex.Replace(input ?? "", pattern, replacement ?? "", options, Timeout);

#if !PAGETOMOVIE_FOUNTAIN
    public static string Replace(string input, string pattern, MatchEvaluator evaluator) =>
        Regex.Replace(input ?? "", pattern, evaluator, RegexOptions.None, Timeout);

    public static string Replace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options) =>
        Regex.Replace(input ?? "", pattern, evaluator, options, Timeout);
#endif

    public static string[] Split(string input, string pattern) =>
        Regex.Split(input ?? "", pattern, RegexOptions.None, Timeout);

    public static string[] Split(string input, string pattern, RegexOptions options) =>
        Regex.Split(input ?? "", pattern, options, Timeout);
}
