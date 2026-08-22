using System.Text.RegularExpressions;

namespace PageToMovie.Cut.Cut;

/// <summary>Fountain sidecar line → join. UI override is cut / dissolve / dip only.</summary>
public static class CutTransitionMap
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static CutJoinKind FromFountain(string? line)
    {
        var t = Normalize(line);
        if (t.Length == 0)
            return CutJoinKind.Unset;
        if (IsMatch(t, @"WIPE"))
            return CutJoinKind.Cut;
        if (IsMatch(t, @"CUT\s+TO\s+BLACK"))
            return CutJoinKind.CutToBlack;
        if (IsMatch(t, @"FADE\s+TO\s+WHITE"))
            return CutJoinKind.FadeWhite;
        if (IsMatch(t, @"FADE\s+IN"))
            return CutJoinKind.FadeIn;
        if (IsMatch(t, @"FADE\s+(OUT|TO\s+BLACK)") || IsMatch(t, @"BLACKOUT"))
            return CutJoinKind.Dip;
        if (IsMatch(t, @"DISSOLVE"))
            return CutJoinKind.Dissolve;
        if (IsMatch(t, @"\b(CUT|SMASH|MATCH|JUMP)\b"))
            return CutJoinKind.Cut;
        return CutJoinKind.Unset;
    }

    public static CutJoinKind Resolve(string? fountainLine, bool sceneChanged, CutJoinKind? uiOverride)
    {
        if (uiOverride is { } chosen && chosen != CutJoinKind.Unset)
            return chosen;
        var mapped = FromFountain(fountainLine);
        if (mapped != CutJoinKind.Unset)
            return mapped;
        return sceneChanged ? CutJoinKind.Dissolve : CutJoinKind.Cut;
    }

    public static string TickLabel(CutJoinKind kind) => kind switch
    {
        CutJoinKind.Dissolve => "Dissolve",
        CutJoinKind.Dip or CutJoinKind.FadeOut => "Dip to black",
        CutJoinKind.FadeIn => "Fade in",
        CutJoinKind.FadeWhite => "Fade to white",
        CutJoinKind.CutToBlack => "Cut to black",
        _ => "Cut",
    };

    public static string WireName(CutJoinKind kind) => kind switch
    {
        CutJoinKind.Dissolve => "dissolve",
        CutJoinKind.Dip => "dip",
        CutJoinKind.FadeIn => "fadein",
        CutJoinKind.FadeOut => "fadeout",
        CutJoinKind.FadeWhite => "fadewhite",
        CutJoinKind.CutToBlack => "cuttoblack",
        _ => "cut",
    };

    public static string? ReadSidecarTransition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var key in new[] { "fountainTransition", "transition", "transition_type" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // sidecar is optional
        }

        return null;
    }

    public static string? ReadSidecarCard(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var key in new[] { "card", "cardText", "incomingJoinCard" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (TryReadCardNote(s, out var fromNote))
                        return fromNote;
                    if (!string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                }
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && TryReadCardNote(prop.Value.GetString(), out var note))
                    return note;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // sidecar is optional
        }

        return TryReadCardNote(json, out var onlyNote) ? onlyNote : null;
    }

    public static bool TryReadCardNote(string? line, out string text)
    {
        text = "";
        var t = (line ?? "").Trim();
        if (!t.StartsWith("[[", StringComparison.Ordinal) || !t.EndsWith("]]", StringComparison.Ordinal) || t.Length < 5)
            return false;
        var inner = t[2..^2].Trim();
        const string prefix = "CARD:";
        if (!inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        text = inner[prefix.Length..].Trim();
        return text.Length > 0;
    }

    private static string Normalize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";
        var t = line.Trim().TrimEnd(':').TrimEnd('.').ToUpperInvariant();
        return Regex.Replace(t, @"\s+", " ", RegexOptions.None, RegexTimeout);
    }

    private static bool IsMatch(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
}
