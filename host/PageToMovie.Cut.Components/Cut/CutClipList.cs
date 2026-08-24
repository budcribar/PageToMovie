using System.Text.Json;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Groups stable <c>_take_NN.mp4</c> files into strip slots.
/// Current take is <c>.current.json</c> only — never a bare alias MP4.
/// </summary>
public static class CutClipList
{
    public static IReadOnlyList<CutClip> FromFiles(IEnumerable<FoundMediaFile> files)
    {
        var list = files.ToList();
        var pointers = ReadPointers(list);
        var sidecars = ReadSidecarTransitions(list);
        var cards = ReadSidecarCards(list);
        var hops = ReadSidecarHops(list);
        var groups = new Dictionary<(int Scene, int Clip), Slot>();

        foreach (var file in list)
        {
            if (!CutClipNaming.IsUsableClipMp4(file.FileName)
                && !CutClipNaming.IsUsableClipMp4(file.RelativePath))
                continue;
            if (!CutClipNaming.TryParseSceneClip(file.FileName, out var scene, out var clip)
                && !CutClipNaming.TryParseSceneClip(file.RelativePath, out scene, out clip))
                continue;

            var take = CutClipNaming.ParseTakeNumber(file.FileName);
            if (take <= 0)
                take = CutClipNaming.ParseTakeNumber(file.RelativePath);
            if (take <= 0)
                continue;

            var key = (scene, clip);
            if (!groups.TryGetValue(key, out var slot))
            {
                slot = new Slot(scene, clip);
                groups[key] = slot;
            }

            slot.Takes.Add(file with { TakeHint = take });
        }

        return groups
            .OrderBy(g => g.Key.Scene)
            .ThenBy(g => g.Key.Clip)
            .Select(g => ToClip(
                g.Value,
                pointers.GetValueOrDefault(g.Key),
                sidecars.GetValueOrDefault(g.Key),
                cards.GetValueOrDefault(g.Key),
                hops))
            .ToList();
    }

    private static Dictionary<(int Scene, int Clip), int> ReadPointers(IEnumerable<FoundMediaFile> files)
    {
        var best = new Dictionary<(int Scene, int Clip), (int Take, int Score)>();
        foreach (var file in files)
        {
            if (!CutClipNaming.IsCurrentPointerName(file.FileName)
                && !CutClipNaming.IsCurrentPointerName(file.RelativePath))
                continue;
            if (!CutClipNaming.TryParseSceneClip(file.FileName, out var scene, out var clip)
                && !CutClipNaming.TryParseSceneClip(file.RelativePath, out scene, out clip))
                continue;
            var take = ParsePointerTake(file.Text);
            if (take <= 0)
                continue;
            var key = (scene, clip);
            var score = PathScore(file.RelativePath);
            if (!best.TryGetValue(key, out var cur) || score < cur.Score)
                best[key] = (take, score);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Take);
    }

    internal static int ParsePointerTake(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("take", out var t) && t.TryGetInt32(out var n) && n > 0)
                return n;
        }
        catch
        {
            // best-effort pointer
        }

        return 0;
    }

    private static Dictionary<(int Scene, int Clip), string> ReadSidecarTransitions(IEnumerable<FoundMediaFile> files)
    {
        var best = new Dictionary<(int Scene, int Clip), (string Line, int Score)>();
        foreach (var file in files)
        {
            if (!CutClipNaming.IsClipSidecarName(file.FileName)
                && !CutClipNaming.IsClipSidecarName(file.RelativePath))
                continue;
            if (!CutClipNaming.TryParseSceneClip(file.FileName, out var scene, out var clip)
                && !CutClipNaming.TryParseSceneClip(file.RelativePath, out scene, out clip))
                continue;
            var line = CutTransitionMap.ReadSidecarTransition(file.Text);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var key = (scene, clip);
            var score = PathScore(file.RelativePath);
            if (!best.TryGetValue(key, out var cur) || score < cur.Score)
                best[key] = (line, score);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Line);
    }

    private static Dictionary<(int Scene, int Clip), string> ReadSidecarCards(IEnumerable<FoundMediaFile> files)
    {
        var best = new Dictionary<(int Scene, int Clip), (string Text, int Score)>();
        foreach (var file in files)
        {
            if (!CutClipNaming.IsClipSidecarName(file.FileName)
                && !CutClipNaming.IsClipSidecarName(file.RelativePath))
                continue;
            if (!CutClipNaming.TryParseSceneClip(file.FileName, out var scene, out var clip)
                && !CutClipNaming.TryParseSceneClip(file.RelativePath, out scene, out clip))
                continue;
            var text = CutTransitionMap.ReadSidecarCard(file.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var key = (scene, clip);
            var score = PathScore(file.RelativePath);
            if (!best.TryGetValue(key, out var cur) || score < cur.Score)
                best[key] = (text, score);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
    }

    private static Dictionary<(int Scene, int Clip, int Take), CutHop> ReadSidecarHops(
        IEnumerable<FoundMediaFile> files)
    {
        var best = new Dictionary<(int Scene, int Clip, int Take), (CutHop Hop, int Score)>();
        foreach (var file in files)
        {
            if (!CutClipNaming.IsClipSidecarName(file.FileName)
                && !CutClipNaming.IsClipSidecarName(file.RelativePath))
                continue;
            if (!CutClipNaming.TryParseSceneClip(file.FileName, out var scene, out var clip)
                && !CutClipNaming.TryParseSceneClip(file.RelativePath, out scene, out clip))
                continue;
            var hop = CutHop.Read(file.Text);
            var hopDuration = hop.DurationSeconds ?? 0;
            if (!hop.HasSlice && hopDuration <= 0)
                continue;
            var take = CutClipNaming.ParseTakeNumber(file.FileName);
            if (take <= 0)
                take = CutClipNaming.ParseTakeNumber(file.RelativePath);
            var key = (scene, clip, take);
            var score = PathScore(file.RelativePath);
            if (!best.TryGetValue(key, out var cur) || score < cur.Score)
                best[key] = (hop, score);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Hop);
    }

    private static CutHop HopFor(
        Dictionary<(int Scene, int Clip, int Take), CutHop> hops, int scene, int clip, int take)
    {
        if (hops.TryGetValue((scene, clip, take), out var exact)
            && (exact.HasSlice || (exact.DurationSeconds ?? 0) > 0))
            return exact;
        if (hops.TryGetValue((scene, clip, 0), out var fallback)
            && (fallback.HasSlice || (fallback.DurationSeconds ?? 0) > 0))
            return fallback;
        return CutHop.None;
    }

    private static CutClip ToClip(
        Slot slot,
        int pointerTake,
        string? fountainTransition,
        string? cardText,
        Dictionary<(int Scene, int Clip, int Take), CutHop> hops)
    {
        var clip = new CutClip { Scene = slot.Scene, Clip = slot.Clip, ActiveTakeNumber = pointerTake };
        clip.FountainTransition = fountainTransition;
        if (!string.IsNullOrWhiteSpace(cardText))
        {
            clip.Card.Enabled = true;
            clip.Card.Text = cardText.Trim();
        }
        if (pointerTake > 0)
        {
            var current = PreferUniqueTakes(slot.Takes).FirstOrDefault(t => t.TakeHint == pointerTake);
            if (current is { } file)
                clip.Takes.Add(ToTake(file.TakeHint, file, HopFor(hops, slot.Scene, slot.Clip, file.TakeHint)));
        }
        else if (RecoverSameSlotTake(slot.Takes) is { } recovered)
        {
            // Same (scene, clip) only — never a previous scene's MP4.
            clip.ActiveTakeNumber = recovered.TakeHint;
            clip.Takes.Add(ToTake(recovered.TakeHint, recovered, HopFor(hops, slot.Scene, slot.Clip, recovered.TakeHint)));
        }

        var pointerPath = PreferOne(slot.Takes) is { } sample
            ? CutClipNaming.PointerPathBeside(sample.RelativePath, slot.Scene, slot.Clip)
            : CutClipNaming.CurrentTakePointerFileName(slot.Scene, slot.Clip);
        clip.PointerRelativePath = pointerPath;
        clip.SeedSelection();
        var sidecarDuration = SlotDuration(hops, slot.Scene, slot.Clip);
        if (sidecarDuration > 0 && !clip.HasDuration)
            clip.SetDuration(sidecarDuration);
        clip.EnsureInOutFromDuration();
        return clip;
    }

    private static CutTake ToTake(int take, FoundMediaFile file, CutHop hop)
    {
        var row = new CutTake
        {
            Take = take,
            FileName = CutClipNaming.FileNameOnly(file.FileName),
            RelativePath = file.RelativePath,
            SizeBytes = file.SizeBytes,
            Missing = !CutTake.IsCandidateFile(file.SizeBytes),
            MissingReason = MissingReasonFor(file.SizeBytes),
        };
        if (hop.HasSlice)
            row.SetHop(hop);
        else if (hop.DurationSeconds is { } duration && duration > 0)
            row.SetDuration(duration);
        return row;
    }

    private static string? MissingReasonFor(long sizeBytes)
    {
        if (sizeBytes <= 0)
            return "Clip file is empty.";
        if (CutTake.IsCandidateFile(sizeBytes))
            return null;
        return "Clip file is not a playable take.";
    }

    /// <summary>
    /// When Film left no <c>.current.json</c>, bind this slot's own playable
    /// take (highest number). Stubs stay unbound. Never another scene.
    /// </summary>
    internal static FoundMediaFile? RecoverSameSlotTake(IReadOnlyList<FoundMediaFile> takes)
    {
        FoundMediaFile? best = null;
        foreach (var file in PreferUniqueTakes(takes.ToList()))
        {
            if (file.TakeHint <= 0 || !CutTake.IsCandidateFile(file.SizeBytes))
                continue;
            if (best is null || file.TakeHint > best.Value.TakeHint)
                best = file;
        }

        return best;
    }

    internal static double SlotDuration(
        Dictionary<(int Scene, int Clip, int Take), CutHop> hops, int scene, int clip)
    {
        if (hops.TryGetValue((scene, clip, 0), out var clipHop)
            && clipHop.DurationSeconds is { } clipSec
            && clipSec > 0)
            return clipSec;
        var best = 0.0;
        foreach (var (key, hop) in hops)
        {
            if (key.Scene != scene || key.Clip != clip)
                continue;
            var sec = hop.DurationSeconds ?? 0;
            if (sec > best)
                best = sec;
        }

        return best;
    }

    private static List<FoundMediaFile> PreferUniqueTakes(List<FoundMediaFile> takes)
    {
        var result = new List<FoundMediaFile>();
        foreach (var group in takes.GroupBy(t => t.TakeHint))
        {
            var preferred = PreferOne(group.ToList());
            if (preferred is { } file)
                result.Add(file);
        }

        return result;
    }

    private static FoundMediaFile? PreferOne(IReadOnlyList<FoundMediaFile> candidates)
    {
        if (candidates.Count == 0)
            return null;
        return candidates
            .OrderBy(f => PathScore(f.RelativePath))
            .ThenBy(f => f.RelativePath.Length)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    internal static int PathScore(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (path.Contains("/history/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("history/", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (path.Contains("assets/video/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("assets/video/", StringComparison.OrdinalIgnoreCase))
            return 0;
        return 1;
    }

    private sealed class Slot(int scene, int clip)
    {
        public int Scene { get; } = scene;
        public int Clip { get; } = clip;
        public List<FoundMediaFile> Takes { get; } = [];
    }
}

public readonly record struct FoundMediaFile(
    string FileName,
    string RelativePath,
    long SizeBytes,
    string? Text = null,
    int TakeHint = 0);
