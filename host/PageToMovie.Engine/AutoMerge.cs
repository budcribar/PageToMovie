using System.Text.Json;

namespace PageToMovie.Engine.Collaboration;

public static class AutoTextMerger
{
    public enum Strategy { Auto, PreferOurs, PreferTheirs, Union }
    public sealed record Hunk(int BaseStartLine, IReadOnlyList<string> BaseLines, IReadOnlyList<string> OursLines, IReadOnlyList<string> TheirsLines);
    public sealed record MergeOutcome(string MergedText, bool HasConflicts, IReadOnlyList<Hunk> Conflicts, int AutoResolvedCount);

    public static MergeOutcome Merge(string? baseText, string? oursText, string? theirsText, Strategy strategy = Strategy.Auto)
    {
        var bas = Split(baseText); var ours = Split(oursText); var theirs = Split(theirsText);
        if (Eq(ours, theirs)) return new MergeOutcome(Join(ours), false, Array.Empty<Hunk>(), 0);
        if (Eq(bas, ours)) return new MergeOutcome(Join(theirs), false, Array.Empty<Hunk>(), 1);
        if (Eq(bas, theirs)) return new MergeOutcome(Join(ours), false, Array.Empty<Hunk>(), 1);
        var merged = new List<string>(); var conflicts = new List<Hunk>(); var auto = 0;
        int i = 0, oi = 0, ti = 0;
        while (i < bas.Count || oi < ours.Count || ti < theirs.Count)
        {
            while (i < bas.Count && oi < ours.Count && ti < theirs.Count && ours[oi] == bas[i] && theirs[ti] == bas[i])
            {
                merged.Add(bas[i]);
                i++;
                oi++;
                ti++;
            }
            if (i >= bas.Count && oi >= ours.Count && ti >= theirs.Count) break;
            var baseStart = i; var bc = new List<string>(); var oc = new List<string>(); var tc = new List<string>();
            if (i < bas.Count)
            {
                int nextSync = -1;
                for (int bi = i; bi < bas.Count; bi++)
                {
                    if (Idx(ours, bas[bi], oi) >= 0 && Idx(theirs, bas[bi], ti) >= 0)
                    {
                        nextSync = bi;
                        break;
                    }
                }
                int baseEnd = nextSync >= 0 ? nextSync : bas.Count;
                for (int bi = i; bi < baseEnd; bi++) bc.Add(bas[bi]);
                int oEnd = nextSync >= 0 ? Idx(ours, bas[nextSync], oi) : ours.Count;
                int tEnd = nextSync >= 0 ? Idx(theirs, bas[nextSync], ti) : theirs.Count;
                if (oEnd < 0)
                    oEnd = ours.Count;
                if (tEnd < 0)
                    tEnd = theirs.Count;
                for (int x = oi; x < oEnd; x++) oc.Add(ours[x]);
                for (int x = ti; x < tEnd; x++) tc.Add(theirs[x]);
                i = baseEnd; oi = oEnd; ti = tEnd;
            }
            else
            {
                while (oi < ours.Count)
                    oc.Add(ours[oi++]);
                while (ti < theirs.Count)
                    tc.Add(theirs[ti++]);
            }
            if (oc.Count == 0 && tc.Count == 0 && bc.Count == 0) break;
            if (Eq(oc, tc)) { merged.AddRange(oc); if (!Eq(oc, bc)) auto++; }
            else if (Eq(oc, bc)) { merged.AddRange(tc); auto++; }
            else if (Eq(tc, bc)) { merged.AddRange(oc); auto++; }
            else if (bc.Count == 0 && oc.All(l => !tc.Contains(l))) { merged.AddRange(oc); merged.AddRange(tc); auto++; }
            else if (bc.Count == oc.Count && bc.Count == tc.Count && TryResolvePerLine(bc, oc, tc, out var perLine))
            { merged.AddRange(perLine); auto++; }
            else
            {
                switch (strategy)
                {
                    case Strategy.PreferOurs: merged.AddRange(oc); auto++; break;
                    case Strategy.PreferTheirs: merged.AddRange(tc); auto++; break;
                    case Strategy.Union:
                        merged.AddRange(oc);
                        foreach (var l in tc)
                        {
                            if (!oc.Contains(l))
                                merged.Add(l);
                        }
                        auto++;
                        break;
                    default:
                        conflicts.Add(new Hunk(baseStart, bc, oc, tc));
                        merged.Add("<<<<<<< ours");
                        merged.AddRange(oc);
                        merged.Add("=======");
                        merged.AddRange(tc);
                        merged.Add(">>>>>>> theirs");
                        break;
                }
            }
        }
        return new MergeOutcome(Join(merged), conflicts.Count > 0, conflicts, auto);
    }
    static List<string> Split(string? t) => string.IsNullOrEmpty(t) ? new() : t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
    static string Join(IReadOnlyList<string> lines) => string.Join("\n", lines);
    static bool Eq(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
    static int Idx(IReadOnlyList<string> lines, string v, int start)
    {
        for (int i = start; i < lines.Count; i++)
        {
            if (lines[i] == v)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// A hunk with equal line counts on all three sides usually means independent, adjacent
    /// single-line edits (no unchanged line separated them enough to become their own hunk via
    /// the sync-point search above). Resolve line-by-line so e.g. base/ours/theirs editing two
    /// different adjacent lines doesn't get reported as one whole-block conflict.
    /// </summary>
    static bool TryResolvePerLine(IReadOnlyList<string> bc, IReadOnlyList<string> oc, IReadOnlyList<string> tc, out List<string> resolved)
    {
        resolved = new List<string>(bc.Count);
        for (int k = 0; k < bc.Count; k++)
        {
            var bl = bc[k]; var ol = oc[k]; var tl = tc[k];
            if (ol == tl) resolved.Add(ol);
            else if (ol == bl) resolved.Add(tl);
            else if (tl == bl) resolved.Add(ol);
            else { resolved = null!; return false; }
        }
        return true;
    }
}

public interface IAutoProjectMerger
{
    AutoTextMerger.MergeOutcome MergeText(string? b, string? o, string? t, AutoTextMerger.Strategy s = AutoTextMerger.Strategy.Auto);
    Task<AutoFileMergeResult> MergeTextFilesAsync(string basePath, string oursPath, string theirsPath, string outputPath, AutoTextMerger.Strategy strategy = AutoTextMerger.Strategy.Auto, CancellationToken ct = default);
    AutoJsonMergeResult MergeJsonObjects(JsonElement? baseJson, JsonElement oursJson, JsonElement theirsJson, AutoTextMerger.Strategy strategy = AutoTextMerger.Strategy.Auto);
}

public sealed record AutoFileMergeResult(bool Success, bool HasConflicts, int AutoResolvedCount, int ConflictCount, string? OutputPath, string? MergedText, string? Error);
public sealed record AutoJsonMergeResult(JsonElement Merged, bool HasConflicts, int AutoResolvedCount, IReadOnlyList<string> ConflictPaths);

public sealed class AutoProjectMerger : IAutoProjectMerger
{
    public AutoTextMerger.MergeOutcome MergeText(string? b, string? o, string? t, AutoTextMerger.Strategy s = AutoTextMerger.Strategy.Auto)
        => AutoTextMerger.Merge(b, o, t, s);

    public async Task<AutoFileMergeResult> MergeTextFilesAsync(string basePath, string oursPath, string theirsPath, string outputPath, AutoTextMerger.Strategy strategy = AutoTextMerger.Strategy.Auto, CancellationToken ct = default)
    {
        try
        {
            string? R(string p) => File.Exists(p) ? File.ReadAllText(p) : null;
            var result = AutoTextMerger.Merge(R(basePath), R(oursPath) ?? "", R(theirsPath) ?? "", strategy);
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, result.MergedText, ct);
            return new AutoFileMergeResult(true, result.HasConflicts, result.AutoResolvedCount, result.Conflicts.Count, outputPath, result.MergedText, null);
        }
        catch (Exception ex) { return new AutoFileMergeResult(false, false, 0, 0, null, null, ex.Message); }
    }

    public AutoJsonMergeResult MergeJsonObjects(JsonElement? baseJson, JsonElement oursJson, JsonElement theirsJson, AutoTextMerger.Strategy strategy = AutoTextMerger.Strategy.Auto)
    {
        if (oursJson.ValueKind != JsonValueKind.Object || theirsJson.ValueKind != JsonValueKind.Object)
        {
            var r = AutoTextMerger.Merge(baseJson?.GetRawText(), oursJson.GetRawText(), theirsJson.GetRawText(), strategy);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.MergedText) ? "{}" : r.MergedText);
            return new AutoJsonMergeResult(doc.RootElement.Clone(), r.HasConflicts, r.AutoResolvedCount, r.Conflicts.Select((_, i) => $"hunk[{i}]").ToList());
        }
        var conflicts = new List<string>(); var auto = 0;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in oursJson.EnumerateObject()) keys.Add(p.Name);
            foreach (var p in theirsJson.EnumerateObject()) keys.Add(p.Name);
            if (baseJson is { ValueKind: JsonValueKind.Object } bObj)
                foreach (var p in bObj.EnumerateObject()) keys.Add(p.Name);
            foreach (var key in keys)
            {
                JsonElement baseVal = default;
                var hasBase = baseJson is { ValueKind: JsonValueKind.Object } bo && bo.TryGetProperty(key, out baseVal);
                var hasOurs = oursJson.TryGetProperty(key, out var oursVal);
                var hasTheirs = theirsJson.TryGetProperty(key, out var theirsVal);
                if (hasOurs && hasTheirs)
                {
                    var oursRaw = oursVal.GetRawText();
                    var theirsRaw = theirsVal.GetRawText();
                    var baseRaw = hasBase ? baseVal.GetRawText() : null;
                    if (oursRaw == theirsRaw)
                    {
                        W(writer, key, oursVal);
                        if (!hasBase || oursRaw != baseRaw) auto++;
                    }
                    else
                    {
                        // Three-way "one side changed" wins over PreferOurs/PreferTheirs.
                        var useTheirs = hasBase && oursRaw == baseRaw
                            || (!(hasBase && theirsRaw == baseRaw)
                                && strategy != AutoTextMerger.Strategy.PreferOurs
                                && strategy == AutoTextMerger.Strategy.PreferTheirs);
                        if (useTheirs)
                        {
                            W(writer, key, theirsVal);
                            auto++;
                        }
                        else if ((hasBase && theirsRaw == baseRaw) || strategy == AutoTextMerger.Strategy.PreferOurs)
                        {
                            W(writer, key, oursVal);
                            auto++;
                        }
                        else
                        {
                            conflicts.Add(key);
                            W(writer, key, oursVal);
                        }
                    }
                }
                else if (hasOurs) { W(writer, key, oursVal); auto++; }
                else if (hasTheirs) { W(writer, key, theirsVal); auto++; }
            }
            writer.WriteEndObject();
        }
        using var resultDoc = JsonDocument.Parse(stream.ToArray());
        return new AutoJsonMergeResult(resultDoc.RootElement.Clone(), conflicts.Count > 0, auto, conflicts);
    }
    static void W(Utf8JsonWriter w, string n, JsonElement v) { w.WritePropertyName(n); v.WriteTo(w); }
}
