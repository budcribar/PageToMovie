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

        if (bookText is null && File.Exists(bookPath))
            bookText = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);

        int? metaNatural = null;
        int? metaTarget = null;
        int? metaWords = null;
        string? bookKind = null;

        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false));
                var root = doc.RootElement;
                if (TryInt(root, NaturalRuntimeMinutesKey, out var n0)) metaNatural = n0;
                if (TryInt(root, TargetRuntimeMinutesKey, out var t0)) metaTarget = t0;
                if (metaTarget is null && TryInt(root, "suggested_total_minutes", out var s0))
                    metaTarget = s0;
                if (TryInt(root, "text_words", out var w0)) metaWords = w0;
                if (root.TryGetProperty("book_kind", out var bk))
                    bookKind = bk.GetString();
            }
            catch { /* ignore */ }
        }

        var hasBook = !string.IsNullOrWhiteSpace(bookText) || File.Exists(bookPath);

        // Natural minutes: Adaptation math only (never re-derived in Engine).
        int natural;
        string densitySource;
        if (!string.IsNullOrWhiteSpace(bookText))
        {
            natural = NaturalRuntime.EstimateNaturalMinutes(bookText);
            densitySource = "density";
        }
        else if (metaNatural is > 0)
        {
            // Cached value written by BookPrepare / SetTarget (originally from Adaptation).
            natural = ClampMinutes(metaNatural.Value);
            densitySource = "extract_meta";
        }
        else if (metaTarget is > 0)
        {
            natural = ClampMinutes(metaTarget.Value);
            densitySource = "extract_meta";
        }
        else
        {
            natural = 0;
            densitySource = "none";
        }

        var cfg = await store.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        int? configTarget = null;
        string? configMode = null;
        if (cfg.TryGetValue(TargetRuntimeMinutesKey, out var tr) && tr.ValueKind == JsonValueKind.Number &&
            tr.TryGetInt32(out var ctm) && ctm > 0)
            configTarget = ctm;
        if (cfg.TryGetValue(RuntimeModeKey, out var rm) && rm.ValueKind == JsonValueKind.String)
            configMode = rm.GetString();

        int target;
        string mode;
        string source;
        if (overrideTargetMinutes is > 0)
        {
            target = ClampMinutes(overrideTargetMinutes.Value);
            mode = NaturalRuntime.ResolveMode(natural, target);
            source = "override";
        }
        else if (configTarget is > 0)
        {
            target = ClampMinutes(configTarget.Value);
            mode = string.IsNullOrWhiteSpace(configMode)
                ? NaturalRuntime.ResolveMode(natural, target)
                : configMode.Trim().ToLowerInvariant();
            source = "config";
        }
        else if (metaTarget is > 0)
        {
            target = ClampMinutes(metaTarget.Value);
            mode = NaturalRuntime.ResolveMode(natural, target);
            source = "extract_meta";
        }
        else if (natural > 0)
        {
            // Default product behavior: estimate natural for display, but do not force a
            // target into Stage‑1 (prompt gets unlimited until the user retargets).
            target = 0;
            mode = "unlimited";
            source = densitySource;
        }
        else
        {
            target = 0;
            mode = "unlimited";
            source = "none";
        }

        return new Snapshot
        {
            HasBookText = hasBook,
            NaturalMinutes = natural,
            TargetMinutes = target,
            Mode = mode,
            TextWords = metaWords,
            BookKind = bookKind,
            Source = source,
        };
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
