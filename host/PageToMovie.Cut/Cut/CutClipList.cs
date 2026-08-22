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
            .Select(g => ToClip(g.Value, pointers.GetValueOrDefault(g.Key), sidecars.GetValueOrDefault(g.Key)))
            .Where(c => c.Takes.Count > 0)
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

    private static CutClip ToClip(Slot slot, int pointerTake, string? fountainTransition)
    {
        var clip = new CutClip { Scene = slot.Scene, Clip = slot.Clip, ActiveTakeNumber = pointerTake };
        clip.FountainTransition = fountainTransition;
        foreach (var file in PreferUniqueTakes(slot.Takes).OrderBy(t => t.TakeHint))
            clip.Takes.Add(ToTake(file.TakeHint, file));

        var pointerPath = PreferOne(slot.Takes) is { } sample
            ? CutClipNaming.PointerPathBeside(sample.RelativePath, slot.Scene, slot.Clip)
            : CutClipNaming.CurrentTakePointerFileName(slot.Scene, slot.Clip);
        clip.PointerRelativePath = pointerPath;
        clip.SeedSelection();
        return clip;
    }

    private static CutTake ToTake(int take, FoundMediaFile file) => new()
    {
        Take = take,
        FileName = CutClipNaming.FileNameOnly(file.FileName),
        RelativePath = file.RelativePath,
        SizeBytes = file.SizeBytes,
        Missing = file.SizeBytes <= 0,
        MissingReason = file.SizeBytes <= 0 ? "Clip file is empty." : null,
    };

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
