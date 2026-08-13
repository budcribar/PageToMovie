using System.Text.Json;
using System.Text.Json.Nodes;
using PageToMovie.Adaptation;

namespace PageToMovie.Engine;

/// <summary>
/// Project film-length storage and orchestration.
/// <para>
/// <b>Pure math</b> (natural density, clamp, mode) lives in
/// <see cref="NaturalRuntime"/> / <see cref="AdaptationService"/> — never reimplemented here.
/// This type only loads book text and reads/writes <c>pipeline_config</c> + <c>extract_meta</c>
/// for target/mode persistence (retarget is Engine; natural math is Adaptation).
/// </para>
/// </summary>
public static class FilmRuntime
{
    public const int MinMinutes = NaturalRuntime.MinMinutes;
    public const int MaxMinutes = NaturalRuntime.MaxMinutes;
    private const string NaturalRuntimeMinutesKey = "natural_runtime_minutes";
    private const string TargetRuntimeMinutesKey = "target_runtime_minutes";
    private const string RuntimeModeKey = "runtime_mode";

    public sealed class Snapshot
    {
        /// <summary>True when source/book_full.txt exists (natural length is meaningful).</summary>
        public bool HasBookText { get; init; }
        public int NaturalMinutes { get; init; }
        public int TargetMinutes { get; init; }
        public string Mode { get; init; } = "natural"; // natural | reduced | custom
        public int? TextWords { get; init; }
        public string? BookKind { get; init; }
        public string Source { get; init; } = ""; // config | extract_meta | density | none
    }

    /// <summary>Delegates to <see cref="NaturalRuntime.ClampMinutes"/> (API surface for Engine callers).</summary>
    public static int ClampMinutes(int minutes) => NaturalRuntime.ClampMinutes(minutes);

    /// <summary>
    /// Resolve target minutes for screenplay generation.
    /// Natural always from Adaptation when book text is available; target from
    /// override → pipeline_config → extract_meta → natural.
    /// </summary>
    public static async Task<Snapshot> ResolveAsync(
        ProjectStore store,
        string projectId,
        string? bookText = null,
        int? overrideTargetMinutes = null,
        CancellationToken ct = default)
    {
        var dir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(dir, "source", "extract_meta.json");
        var bookPath = Path.Combine(dir, "source", "book_full.txt");

        bookText = await LoadBookTextIfMissingAsync(bookText, bookPath, ct).ConfigureAwait(false);
        var meta = await TryReadExtractMetaAsync(metaPath, ct).ConfigureAwait(false);
        var hasBook = !string.IsNullOrWhiteSpace(bookText) || File.Exists(bookPath);
        var (natural, densitySource) = ResolveNaturalMinutes(bookText, meta.Natural, meta.Target);
        var cfg = await store.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        var (configTarget, configMode) = ReadConfigTarget(cfg);
        var (target, mode, source) = ResolveTargetAndMode(
            overrideTargetMinutes, configTarget, configMode, meta.Target, natural, densitySource);

        return new Snapshot
        {
            HasBookText = hasBook,
            NaturalMinutes = natural,
            TargetMinutes = target,
            Mode = mode,
            TextWords = meta.Words,
            BookKind = meta.BookKind,
            Source = source,
        };
    }

    private static async Task<string?> LoadBookTextIfMissingAsync(
        string? bookText, string bookPath, CancellationToken ct)
    {
        if (bookText is null && File.Exists(bookPath))
            return await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
        return bookText;
    }

    private readonly record struct ExtractMeta(int? Natural, int? Target, int? Words, string? BookKind);

    private static async Task<ExtractMeta> TryReadExtractMetaAsync(string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath))
            return default;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false));
            return ParseExtractMeta(doc.RootElement);
        }
        catch
        {
            return default;
        }
    }

    private static ExtractMeta ParseExtractMeta(JsonElement root)
    {
        int? metaNatural = null;
        int? metaTarget = null;
        int? metaWords = null;
        string? bookKind = null;
        if (TryInt(root, NaturalRuntimeMinutesKey, out var n0)) metaNatural = n0;
        if (TryInt(root, TargetRuntimeMinutesKey, out var t0)) metaTarget = t0;
        if (metaTarget is null && TryInt(root, "suggested_total_minutes", out var s0))
            metaTarget = s0;
        if (TryInt(root, "text_words", out var w0)) metaWords = w0;
        if (root.TryGetProperty("book_kind", out var bk))
            bookKind = bk.GetString();
        return new ExtractMeta(metaNatural, metaTarget, metaWords, bookKind);
    }

    private static (int Natural, string DensitySource) ResolveNaturalMinutes(
        string? bookText, int? metaNatural, int? metaTarget)
    {
        // Natural minutes: Adaptation math only (never re-derived in Engine).
        if (!string.IsNullOrWhiteSpace(bookText))
            return (NaturalRuntime.EstimateNaturalMinutes(bookText), "density");
        if (metaNatural is > 0)
        {
            // Cached value written by BookPrepare / SetTarget (originally from Adaptation).
            return (ClampMinutes(metaNatural.Value), "extract_meta");
        }
        if (metaTarget is > 0)
            return (ClampMinutes(metaTarget.Value), "extract_meta");
        return (0, "none");
    }

    private static (int? Target, string? Mode) ReadConfigTarget(Dictionary<string, JsonElement> cfg)
    {
        int? configTarget = null;
        string? configMode = null;
        if (cfg.TryGetValue(TargetRuntimeMinutesKey, out var tr) && tr.ValueKind == JsonValueKind.Number &&
            tr.TryGetInt32(out var ctm) && ctm > 0)
            configTarget = ctm;
        if (cfg.TryGetValue(RuntimeModeKey, out var rm) && rm.ValueKind == JsonValueKind.String)
            configMode = rm.GetString();
        return (configTarget, configMode);
    }

    private static (int Target, string Mode, string Source) ResolveTargetAndMode(
        int? overrideTargetMinutes,
        int? configTarget,
        string? configMode,
        int? metaTarget,
        int natural,
        string densitySource)
    {
        if (overrideTargetMinutes is > 0)
        {
            var target = ClampMinutes(overrideTargetMinutes.Value);
            return (target, NaturalRuntime.ResolveMode(natural, target), "override");
        }
        if (configTarget is > 0)
        {
            var target = ClampMinutes(configTarget.Value);
            var mode = string.IsNullOrWhiteSpace(configMode)
                ? NaturalRuntime.ResolveMode(natural, target)
                : configMode.Trim().ToLowerInvariant();
            return (target, mode, "config");
        }
        if (metaTarget is > 0)
        {
            var target = ClampMinutes(metaTarget.Value);
            return (target, NaturalRuntime.ResolveMode(natural, target), "extract_meta");
        }
        if (natural > 0)
        {
            // Default product behavior: estimate natural for display, but do not force a
            // target into Stage‑1 (prompt gets unlimited until the user retargets).
            return (0, "unlimited", densitySource);
        }
        return (0, "unlimited", "none");
    }

    /// <summary>
    /// Persist user target (and keep natural). Updates pipeline_config + extract_meta when present.
    /// </summary>
    public static async Task<Snapshot> SetTargetAsync(
        ProjectStore store,
        string projectId,
        int targetMinutes,
        CancellationToken ct = default)
    {
        var snap = await ResolveAsync(store, projectId, ct: ct).ConfigureAwait(false);
        if (!snap.HasBookText || snap.NaturalMinutes <= 0)
            throw new InvalidOperationException(
                "Import the book first so we can measure a natural film length, then set a shorter target if you want.");
        targetMinutes = ClampMinutes(targetMinutes);
        var mode = NaturalRuntime.ResolveMode(snap.NaturalMinutes, targetMinutes);

        using var updateDoc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [TargetRuntimeMinutesKey] = targetMinutes,
            [NaturalRuntimeMinutesKey] = snap.NaturalMinutes,
            [RuntimeModeKey] = mode,
        }));
        await store.SaveConfigAsync(projectId, updateDoc.RootElement.Clone(), ct).ConfigureAwait(false);

        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var metaPath = Path.Combine(projectDir, "source", "extract_meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false)) as JsonObject
                           ?? new JsonObject();
                node[NaturalRuntimeMinutesKey] = snap.NaturalMinutes;
                node[TargetRuntimeMinutesKey] = targetMinutes;
                node["suggested_total_minutes"] = targetMinutes; // backward compatible
                node[RuntimeModeKey] = mode;
                await File.WriteAllTextAsync(
                    metaPath,
                    node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                    ct).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        }

        return new Snapshot
        {
            HasBookText = snap.HasBookText,
            NaturalMinutes = snap.NaturalMinutes,
            TargetMinutes = targetMinutes,
            Mode = mode,
            TextWords = snap.TextWords,
            BookKind = snap.BookKind,
            Source = "config",
        };
    }

    /// <summary>
    /// Write natural (+ default target=natural) into extract_meta after book prepare.
    /// Natural minutes must come from Adaptation; this only fills storage fields.
    /// </summary>
    public static void ApplyNaturalToMetaDictionary(
        Dictionary<string, object?> meta,
        int naturalMinutes,
        int? existingTarget = null)
    {
        naturalMinutes = ClampMinutes(naturalMinutes);
        var target = existingTarget is > 0 ? ClampMinutes(existingTarget.Value) : naturalMinutes;
        meta[NaturalRuntimeMinutesKey] = naturalMinutes;
        meta[TargetRuntimeMinutesKey] = target;
        meta["suggested_total_minutes"] = target;
        meta[RuntimeModeKey] = NaturalRuntime.ResolveMode(naturalMinutes, target);
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return value > 0;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return value > 0;
        return false;
    }
}
