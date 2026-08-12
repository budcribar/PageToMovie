using System.Text.RegularExpressions;

namespace PageToMovie.Fountain;

/// <summary>Fountain stays dependency-free — local timeout twin of <c>CommonRegex.Timeout</c>.</summary>
internal static class FountainRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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

    public static string[] Split(string input, string pattern) =>
        Regex.Split(input ?? "", pattern, RegexOptions.None, Timeout);

    public static string[] Split(string input, string pattern, RegexOptions options) =>
        Regex.Split(input ?? "", pattern, options, Timeout);
}
