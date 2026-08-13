using System.Text;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Utils;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Trim is a view: pick sequences from the index, then cut <c>screenplay.max</c> to those scenes.
/// Never mutates the index or the max master.
/// </summary>
public static class ScreenplayIndexCutter
{
    public const double DefaultCardMinutes = 2.0;

    public sealed class CutPlan
    {
        public bool KeepAll { get; init; }
        public int TargetMinutes { get; init; }
        public double TotalMinutes { get; init; }
        public double KeptMinutes { get; init; }
        public IReadOnlyList<string> KeptSequenceIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> DroppedSequenceIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ScreenplayIndexCard> KeptCards { get; init; } = Array.Empty<ScreenplayIndexCard>();
        public string Reason { get; init; } = "";
    }

    public static double SequenceMinutes(ScreenplayIndexSequence seq)
    {
        var sum = 0.0;
        var any = false;
        foreach (var card in seq.Scenes ?? [])
        {
            if (card.ApproxMinutes is > 0)
            {
                sum += card.ApproxMinutes.Value;
                any = true;
            }
            else
                sum += DefaultCardMinutes;
        }
        return any || (seq.Scenes?.Count ?? 0) > 0 ? sum : 0;
    }

    public static CutPlan Plan(ScreenplayIndex? index, int targetMinutes, string? runtimeMode = null)
    {
        var sequences = index?.Acts
            .SelectMany(a => a.Sequences)
            .Where(s => s.Scenes is { Count: > 0 })
            .ToList() ?? [];
        var total = sequences.Sum(SequenceMinutes);
        var naturalMode = string.Equals(runtimeMode, "natural", StringComparison.OrdinalIgnoreCase);

        if (sequences.Count == 0)
            return new CutPlan { KeepAll = true, TargetMinutes = targetMinutes, Reason = "no_index" };

        if (ShouldKeepAll(naturalMode, targetMinutes, total))
            return KeepAllPlan(sequences, targetMinutes, total, naturalMode);

        return CutToTarget(sequences, targetMinutes, total);
    }

    /// <summary>
    /// Cut <paramref name="maxFountain"/> to scenes matching kept cards.
    /// Returns null when matching is too weak (caller should use the AI trim).
    /// </summary>
    public static string? ApplyToFountain(string? maxFountain, CutPlan plan)
    {
        if (string.IsNullOrWhiteSpace(maxFountain)) return null;
        if (plan.KeepAll) return maxFountain;
        if (plan.KeptCards.Count == 0) return null;

        var (title, scenes) = SplitScenes(maxFountain);
        if (scenes.Count == 0) return null;

        var keptKeys = CollectKeptKeys(plan.KeptCards);
        var (kept, matched) = MatchKeptScenes(scenes, keptKeys);

        if (matched < Math.Max(1, (int)Math.Ceiling(plan.KeptCards.Count * 0.50)))
            return null;

        return AssembleCutFountain(title, kept);
    }

    internal static (string Title, List<string> Scenes) SplitScenes(string fountain)
    {
        var lines = fountain.Replace("\r\n", "\n").Split('\n');
        var scenes = new List<string>();
        var title = new StringBuilder();
        StringBuilder? cur = null;
        foreach (var line in lines)
        {
            if (IsSceneHeading(line))
            {
                if (cur is not null)
                    scenes.Add(cur.ToString());
                cur = new StringBuilder();
                cur.AppendLine(line);
            }
            else if (cur is null)
                title.AppendLine(line);
            else
                cur.AppendLine(line);
        }
        if (cur is not null)
            scenes.Add(cur.ToString());
        return (title.ToString(), scenes);
    }

    internal static string NormalizeHeading(string? heading)
    {
        var t = (heading ?? "").Trim().ToUpperInvariant();
        t = CommonRegex.Replace(t, @"^(INT\.?/EXT\.?|INT\.?|EXT\.?|EST\.?|I/E)\s*", "");
        t = CommonRegex.Replace(t, @"\s*[-–—]\s*(DAY|NIGHT|DAWN|DUSK|EVENING|MORNING|CONTINUOUS|LATER)\s*$", "");
        t = CommonRegex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    internal static string NormalizeLocationKey(string? key)
    {
        var t = (key ?? "").Trim();
        if (t.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase))
            t = t[4..];
        t = CommonRegex.Replace(t, @"([a-z])([A-Z])", "$1 $2");
        t = t.Replace('_', ' ');
        return CommonRegex.Replace(t, @"\s+", " ").Trim().ToUpperInvariant();
    }

    private static bool ShouldKeepAll(bool naturalMode, int targetMinutes, double total) =>
        naturalMode || targetMinutes <= 0 || targetMinutes + 0.5 >= total;

    private static CutPlan KeepAllPlan(
        List<ScreenplayIndexSequence> sequences, int targetMinutes, double total, bool naturalMode) =>
        new()
        {
            KeepAll = true,
            TargetMinutes = targetMinutes,
            TotalMinutes = total,
            KeptMinutes = total,
            KeptSequenceIds = sequences.Select(s => s.Id).ToList(),
            KeptCards = sequences.SelectMany(s => s.Scenes).ToList(),
            Reason = naturalMode ? "full_master" : "target_covers_master",
        };

    private static CutPlan CutToTarget(List<ScreenplayIndexSequence> sequences, int targetMinutes, double total)
    {
        var keep = new bool[sequences.Count];
        var keptMin = TakeSequence(keep, sequences, 0, 0.0);
        if (sequences.Count > 1)
            keptMin = TakeSequence(keep, sequences, sequences.Count - 1, keptMin);

        for (var i = 1; i < sequences.Count - 1; i++)
        {
            var next = SequenceMinutes(sequences[i]);
            if (keptMin + next <= targetMinutes + 0.75)
                keptMin = TakeSequence(keep, sequences, i, keptMin);
        }

        var keptSeq = new List<string>();
        var dropped = new List<string>();
        var keptCards = new List<ScreenplayIndexCard>();
        for (var i = 0; i < sequences.Count; i++)
        {
            if (keep[i])
            {
                keptSeq.Add(sequences[i].Id);
                keptCards.AddRange(sequences[i].Scenes);
            }
            else
                dropped.Add(sequences[i].Id);
        }

        return new CutPlan
        {
            KeepAll = dropped.Count == 0,
            TargetMinutes = targetMinutes,
            TotalMinutes = total,
            KeptMinutes = keptMin,
            KeptSequenceIds = keptSeq,
            DroppedSequenceIds = dropped,
            KeptCards = keptCards,
            Reason = dropped.Count == 0 ? "target_covers_master" : "sequence_cut",
        };
    }

    private static double TakeSequence(
        bool[] keep, List<ScreenplayIndexSequence> sequences, int i, double keptMin)
    {
        if (keep[i]) return keptMin;
        keep[i] = true;
        return keptMin + SequenceMinutes(sequences[i]);
    }

    private static HashSet<string> CollectKeptKeys(IReadOnlyList<ScreenplayIndexCard> cards)
    {
        var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            var h = NormalizeHeading(card.Heading);
            if (h.Length > 0) keptKeys.Add(h);
            var loc = NormalizeLocationKey(card.LocationKey);
            if (loc.Length > 0) keptKeys.Add(loc);
        }
        return keptKeys;
    }

    private static (List<string> Kept, int Matched) MatchKeptScenes(List<string> scenes, HashSet<string> keptKeys)
    {
        var kept = new List<string>();
        var matched = 0;
        foreach (var scene in scenes)
        {
            var heading = FirstHeading(scene);
            var norm = NormalizeHeading(heading);
            var loc = HeadingPlace(heading);
            if (keptKeys.Contains(norm) || (loc.Length > 0 && keptKeys.Contains(loc)))
            {
                kept.Add(scene.Trim());
                matched++;
            }
        }
        return (kept, matched);
    }

    private static string AssembleCutFountain(string title, List<string> kept)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append(title.TrimEnd()).Append("\n\n");
        sb.Append(string.Join("\n\n", kept));
        var text = sb.ToString().Trim();
        if (!CommonRegex.IsMatch(text, @"(?im)^(FADE OUT\.|THE END)\s*$"))
            text += "\n\nFADE OUT.\n\nTHE END\n";
        return BookToFountainConverter.NormalizeFountainText(text);
    }

    private static string HeadingPlace(string? heading) => NormalizeHeading(heading);

    private static string FirstHeading(string scene)
    {
        foreach (var line in scene.Replace("\r\n", "\n").Split('\n'))
        {
            if (IsSceneHeading(line)) return line.Trim();
        }
        return "";
    }

    private static bool IsSceneHeading(string line)
    {
        var t = line.Trim();
        return CommonRegex.IsMatch(t, @"^(INT\.?/EXT\.?|INT\.|EXT\.|EST\.|I/E)[\./ ]", RegexOptions.IgnoreCase);
    }
}
