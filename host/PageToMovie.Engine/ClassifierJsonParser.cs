using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Shared template helper for AI classifier response fence-stripping, backoff retries, and formatting.
/// </summary>
public static class ClassifierJsonParser
{
    private static readonly Regex FencePrefixRegex = new(@"^```(?:json)?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>Strips markdown code fences (```json ... ```) from raw LLM text responses.</summary>
    public static string StripFences(string raw)
    {
        raw = (raw ?? "").Trim();
        if (!raw.StartsWith("```", StringComparison.Ordinal)) return raw;
        raw = FencePrefixRegex.Replace(raw, "");
        var fenceEnd = raw.IndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? raw[..fenceEnd] : raw).TrimEnd();
    }

    /// <summary>Standard exponential backoff for classifier AI retries.</summary>
    public static async Task BackoffAsync(int attempt, int baseMs, CancellationToken ct = default)
    {
        if (baseMs <= 0) return;
        var delayMs = Math.Min(4000, baseMs * attempt * attempt);
        await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }
}
