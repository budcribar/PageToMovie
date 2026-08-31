using System.Text.Json;
using System.Text.Json.Serialization;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Engine.Abstractions;
using AdaptationConverter = PageToMovie.Adaptation.Conversion.BookToFountainConverter;

namespace PageToMovie.Engine;

/// <summary>
/// Status of the VISION_META trailer parsed from a Stage 1 model response — mirrors
/// <see cref="AdaptationVisionMetaStatus"/> (Adaptation's transport-shaped status) at the Engine/project layer.
/// </summary>
public enum ProjectVisionMetaStatus
{
    PrimaryResponse,
    RepairResponse,
    Missing,
    Malformed,
    InvalidValue,
}

/// <summary>
/// Structured visual medium decided at book→screenplay (adaptation / book prepare).
/// Single source of truth: <c>source/vision_meta.json</c> overlay, then <c>source/extract_meta.json</c>.
/// Stage 2 and video generate call <see cref="RequireDecided"/> — they do not invent photoreal or 3D CG.
/// </summary>
public static class ProjectVisionMeta
{
    public const string FileName = "vision_meta.json";
    public const string CurrentSchemaVersion = "vision_meta.v1";
    private const string Adaptation = "adaptation";

    public const string MediumAuto = "auto";
    public const string MediumPhotoreal = VisualMediumStyles.MediumPhotoreal;
    public const string MediumIllustrated = VisualMediumStyles.MediumIllustrated;
    public const string MediumStylized3d = VisualMediumStyles.MediumStylized3d;
    public const string MediumOther = VisualMediumStyles.MediumOther;

    public sealed class Document
    {
        [JsonPropertyName("schema_version")]
        public string SchemaVersion { get; set; } = ProjectVisionMeta.CurrentSchemaVersion;

        /// <summary>Machine enum: photoreal_live_action | illustrated_picture_book | stylized_3d_animated | other. Empty until decided.</summary>
        [JsonPropertyName("visual_medium")]
        public string VisualMedium { get; set; } = "";

        /// <summary>Full STYLE LOCK prose for image/video models.</summary>
        [JsonPropertyName("render_style_lock")]
        public string? RenderStyleLock { get; set; }

        [JsonPropertyName("performance_lock")]
        public string? PerformanceLock { get; set; }

        /// <summary>adaptation | cast_extract | user</summary>
        [JsonPropertyName("decided_by")]
        public string DecidedBy { get; set; } = Adaptation;

        [JsonPropertyName("decided_at")]
        public string? DecidedAt { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    public static string GetPath(string projectDir) =>
        Path.Combine(projectDir, "source", FileName);

    /// <summary>Import sidecar from BookPrepareService — primary shared store.</summary>
    public const string ExtractMetaFileName = "extract_meta.json";

    public static string GetExtractMetaPath(string projectDir) =>
        Path.Combine(projectDir, "source", ExtractMetaFileName);

    /// <summary>
    /// Operator-facing fail-fast when Stage 2 or video generate runs without a decided medium.
    /// Do not invent photoreal / 3D CG — the operator picks the look on the screenplay page.
    /// </summary>
    public const string MissingMediumMessage =
        "This film has no look yet. Open Screenplay, choose how it should look, and save.";

    /// <summary>
    /// Operator-facing fail-fast when Stage 2 or video generate runs without a film address lock.
    /// Do not invent confessional vs objective — Cast extract writes <c>performance_lock</c>
    /// from the screenplay. Book/screenplay VISION_META does not include this field.
    /// </summary>
    public const string MissingPerformanceLockMessage =
        "This film has no performance lock. Run Cast extract from the screenplay.";

    public static bool IsDecidedMedium(string? raw) => VisualMediumStyles.IsDecidedMedium(raw);

    /// <summary>
    /// Decided film medium from vision_meta overlay, then extract_meta. Null when missing or <c>auto</c>.
    /// Does not guess from GPV, cast_seeds, project_rules, or book_kind.
    /// </summary>
    public static Document? TryGetDecided(string projectDir)
    {
        var doc = TryRead(projectDir);
        if (doc is null || !IsDecidedMedium(doc.VisualMedium))
            return null;
        if (string.IsNullOrWhiteSpace(doc.RenderStyleLock))
            doc.RenderStyleLock = DefaultStyleLock(doc.VisualMedium);
        return doc;
    }

    /// <summary>Stage 2 / generate: decided medium or throw <see cref="MissingMediumMessage"/>.</summary>
    public static Document RequireDecided(string projectDir) =>
        TryGetDecided(projectDir)
        ?? throw new InvalidOperationException(MissingMediumMessage);

    /// <summary>
    /// Film-level address lock from vision_meta (overlay, then extract_meta). Empty is missing —
    /// do not invent confessional vs objective, STYLE LOCK, or a visual-medium shim.
    /// </summary>
    public static string? TryGetPerformanceLock(string projectDir)
    {
        var overlay = TryRead(projectDir);
        if (!string.IsNullOrWhiteSpace(overlay?.PerformanceLock))
            return overlay.PerformanceLock.Trim();
        var extract = TryReadExtractMeta(projectDir);
        return string.IsNullOrWhiteSpace(extract?.PerformanceLock)
            ? null
            : extract.PerformanceLock.Trim();
    }

    /// <summary>Stage 2 / generate: performance lock or throw <see cref="MissingPerformanceLockMessage"/>.</summary>
    public static string RequirePerformanceLock(string projectDir) =>
        TryGetPerformanceLock(projectDir)
        ?? throw new InvalidOperationException(MissingPerformanceLockMessage);

    public static Document? TryRead(string projectDir)
    {
        // 1) Overlay wins when it already has a decided medium (adaptation or user lock).
        var overlay = TryReadVisionFile(GetPath(projectDir));
        if (overlay is not null && IsDecidedMedium(overlay.VisualMedium))
            return overlay;

        // 2) extract_meta.json written at book prepare / adaptation.
        var fromExtract = TryReadExtractMeta(projectDir);
        if (fromExtract is not null && IsDecidedMedium(fromExtract.VisualMedium))
            return fromExtract;

        // 3) Overlay may still hold an auto preference (Stage-1 MEDIUM DIRECTIVE) — not a film decision.
        return overlay ?? TryReadVisionFile(GetPath(projectDir));
    }

    static Document? TryReadVisionFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (doc is null || string.IsNullOrWhiteSpace(doc.VisualMedium))
                return null;
            doc.VisualMedium = NormalizeMedium(doc.VisualMedium);
            return doc;
        }
        catch { return null; }
    }

    /// <summary>Read medium fields from import <c>extract_meta.json</c>.</summary>
    public static Document? TryReadExtractMeta(string projectDir)
    {
        var path = GetExtractMetaPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var jd = JsonDocument.Parse(File.ReadAllText(path));
            return ParseExtractMetaDocument(jd.RootElement);
        }
        catch { return null; }
    }

    private static Document? ParseExtractMetaDocument(JsonElement root)
    {
        var medium = ReadOptionalString(root, "visual_medium");
        var style = ReadOptionalString(root, "render_style_lock");
        var source = ReadOptionalString(root, "medium_source");
        var notes = ReadExtractNotes(root);
        if (!IsDecidedMedium(medium))
            return null;

        var med = NormalizeMedium(medium);
        return new Document
        {
            VisualMedium = med,
            RenderStyleLock = string.IsNullOrWhiteSpace(style) ? DefaultStyleLock(med) : style.Trim(),
            PerformanceLock = ReadOptionalString(root, "performance_lock"),
            DecidedBy = ResolveExtractDecidedBy(source),
            Notes = notes,
            DecidedAt = ReadOptionalString(root, "prepared_at"),
        };
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static string? ReadExtractNotes(JsonElement root)
    {
        if (!root.TryGetProperty("notes", out var n)) return null;
        if (n.ValueKind == JsonValueKind.String) return n.GetString();
        if (n.ValueKind != JsonValueKind.Array) return null;
        return string.Join("; ", n.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string ResolveExtractDecidedBy(string? source) =>
        source switch
        {
            Adaptation or "adaptation_llm" => Adaptation,
            "cast_extract" => "cast_extract",
            _ => "import",
        };

    public static void Write(string projectDir, Document doc)
    {
        Directory.CreateDirectory(Path.Combine(projectDir, "source"));
        doc.SchemaVersion = CurrentSchemaVersion;
        doc.VisualMedium = NormalizeMedium(doc.VisualMedium);
        doc.DecidedAt = DateTimeOffset.UtcNow.ToString("o");
        if (IsDecidedMedium(doc.VisualMedium) && string.IsNullOrWhiteSpace(doc.RenderStyleLock))
            doc.RenderStyleLock = DefaultStyleLock(doc.VisualMedium);

        // Keep import sidecar as the shared home (merge into extract_meta when present).
        MergeIntoExtractMeta(projectDir, doc);

        // Also write vision_meta for callers/tests that look for the thin file.
        var path = GetPath(projectDir);
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(path, json);
    }

    static void MergeIntoExtractMeta(string projectDir, Document doc)
    {
        var path = GetExtractMetaPath(projectDir);
        Dictionary<string, object?> root;
        if (File.Exists(path))
        {
            try
            {
                root = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema_version"] = "extract_meta.v1",
                ["prepared_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            };
        }

        root["visual_medium"] = doc.VisualMedium;
        root["render_style_lock"] = doc.RenderStyleLock;
        root["medium_source"] = doc.DecidedBy == Adaptation ? Adaptation : (doc.DecidedBy ?? "import");
        root["medium_decided_at"] = doc.DecidedAt;
        if (!string.IsNullOrWhiteSpace(doc.Notes))
            root["medium_notes"] = doc.Notes;
        if (!string.IsNullOrWhiteSpace(doc.PerformanceLock))
            root["performance_lock"] = doc.PerformanceLock;

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    public static string NormalizeMedium(string? raw) =>
        VisualMediumStyles.NormalizeMedium(raw, allowAuto: true, mapMixedToPhotoreal: false);

    /// <summary>
    /// Split a model response that may include a VISION_META trailer, mapping the trailer's vision
    /// fields to <see cref="Document"/>. Delegates the pure split to
    /// <see cref="PageToMovie.Adaptation.Conversion.BookToFountainConverter.SplitVisionMetaTrailer"/>.
    /// </summary>
    public static (string Fountain, Document? Vision) SplitVisionMetaTrailer(string? text)
    {
        var (fountain, vision) = AdaptationConverter.SplitVisionMetaTrailer(text);
        return (fountain, MapVision(vision));
    }

    /// <summary>Maps Adaptation's transport vision DTO onto the project-shaped <see cref="Document"/>.</summary>
    public static Document? MapVision(AdaptationVisionMeta? v)
    {
        if (v is null) return null;
        return new Document
        {
            SchemaVersion = string.IsNullOrWhiteSpace(v.SchemaVersion)
                ? CurrentSchemaVersion
                : v.SchemaVersion,
            VisualMedium = NormalizeMedium(v.VisualMedium),
            RenderStyleLock = v.RenderStyleLock,
            PerformanceLock = v.PerformanceLock,
            DecidedBy = string.IsNullOrWhiteSpace(v.DecidedBy) ? Adaptation : v.DecidedBy,
            DecidedAt = v.DecidedAt,
            Notes = v.Notes,
        };
    }

    /// <summary>
    /// Canonical <see cref="AdaptationVisionMetaStatus"/> → <see cref="ProjectVisionMetaStatus"/> map.
    /// Public so production (ScreenplayService) and tests share one mapping instead of copies.
    /// </summary>
    public static ProjectVisionMetaStatus MapStatus(AdaptationVisionMetaStatus s) => s switch
    {
        AdaptationVisionMetaStatus.PrimaryResponse => ProjectVisionMetaStatus.PrimaryResponse,
        AdaptationVisionMetaStatus.RepairResponse => ProjectVisionMetaStatus.RepairResponse,
        AdaptationVisionMetaStatus.Missing => ProjectVisionMetaStatus.Missing,
        AdaptationVisionMetaStatus.Malformed => ProjectVisionMetaStatus.Malformed,
        AdaptationVisionMetaStatus.InvalidValue => ProjectVisionMetaStatus.InvalidValue,
        _ => ProjectVisionMetaStatus.Missing,
    };

    /// <summary>
    /// User/UI preference for Stage‑1 MEDIUM DIRECTIVE. <see cref="MediumAuto"/> = model infers.
    /// Stored on the vision document so convert can lock style before first Fountain.
    /// </summary>
    public static string GetAdaptationMediumPreference(string projectDir)
    {
        var doc = TryRead(projectDir);
        if (doc is null || string.IsNullOrWhiteSpace(doc.VisualMedium))
            return MediumAuto;
        return NormalizeMedium(doc.VisualMedium);
    }

    public static Document SetAdaptationMediumPreference(string projectDir, string? medium, string? decidedBy = "user")
    {
        var med = NormalizeMedium(medium);
        var doc = TryRead(projectDir) ?? new Document
        {
            SchemaVersion = CurrentSchemaVersion,
            DecidedAt = DateTime.UtcNow.ToString("o"),
        };
        doc.VisualMedium = med;
        doc.DecidedBy = string.IsNullOrWhiteSpace(decidedBy) ? "user" : decidedBy;
        doc.DecidedAt = DateTime.UtcNow.ToString("o");
        if (med is MediumAuto)
        {
            doc.RenderStyleLock = null;
            doc.Notes = "User preference: auto — Stage‑1 will infer medium from the book.";
        }
        else
        {
            doc.RenderStyleLock = DefaultStyleLock(med);
            doc.Notes = $"User preference: lock medium to {med} for Stage‑1.";
        }
        Write(projectDir, doc);
        return doc;
    }

    public static bool PrefersIllustrated(string? visualMedium) =>
        VisualMediumStyles.PrefersIllustrated(visualMedium);

    public static string DefaultStyleLock(string visualMedium) =>
        VisualMediumStyles.StyleLockFor(NormalizeMedium(visualMedium));

    public static string DefaultAspectRatio(string? visualMedium) =>
        VisualMediumStyles.DefaultAspectRatioFor(visualMedium);

    /// <summary>
    /// Persist a decided adaptation medium. Fills <c>render_style_lock</c> from
    /// <see cref="VisualMediumStyles.StyleLockFor"/> when the trailer omitted it.
    /// Returns null when <paramref name="fromScript"/> is not a decided medium (caller must not invent).
    /// </summary>
    public static Document? PersistAdaptationDecision(string projectDir, Document? fromScript)
    {
        if (fromScript is null || !IsDecidedMedium(fromScript.VisualMedium))
            return null;
        fromScript.DecidedBy = string.IsNullOrWhiteSpace(fromScript.DecidedBy)
            ? Adaptation
            : fromScript.DecidedBy;
        Write(projectDir, fromScript);
        return fromScript;
    }

    /// <summary>
    /// Ask the planning model once at adaptation time for structured medium metadata.
    /// Fountain prose is not parsed; the model returns JSON only.
    /// </summary>
    public static async Task<Document> DecideAtAdaptationAsync(
        string projectDir,
        string title,
        string bookText,
        string fountainText,
        ChatCall chat)
    {
        chat.Report("Deciding film visual medium (structured metadata)…");

        var bookSample = bookText.Length > 6_000 ? bookText[..6_000] + "\n[[truncated]]" : bookText;
        var fountainSample = fountainText.Length > 4_000 ? fountainText[..4_000] + "\n[[truncated]]" : fountainText;

        var system =
            "You classify the visual medium for a film adaptation. Return JSON only (no markdown).\n" +
            "Schema:\n" +
            "{\n" +
            "  \"visual_medium\": \"photoreal_live_action\" | \"illustrated_picture_book\" | \"stylized_3d_animated\" | \"other\",\n" +
            "  \"render_style_lock\": \"STYLE LOCK: … one sentence medium for portraits and clips …\",\n" +
            "  \"notes\": \"optional short rationale\"\n" +
            "}\n" +
            "Rules:\n" +
            "- Decide from STORY CONTENT (genre, illustrated children's book vs literary prose vs live-action drama).\n" +
            "- Do NOT use file type. Classic short stories / gothic / period literary → photoreal_live_action.\n" +
            "- Animal picture books / painted children's stories → illustrated_picture_book.\n" +
            "- One medium for the whole film.";

        var user =
            $"Title: {title}\n\n--- BOOK (sample) ---\n{bookSample}\n\n--- SCREENPLAY (sample) ---\n{fountainSample}\n";

        var raw = await chat.Chat.CompleteAsync(
            system,
            user,
            model: chat.Model,
            ct: chat.Ct,
            mode: ChatCallModes.VisionMetaAdaptation).ConfigureAwait(false);

        var doc = ParseModelJson(raw);
        if (doc is null || !IsDecidedMedium(doc.VisualMedium))
        {
            throw new InvalidOperationException(
                "Adaptation could not decide visual medium. " + MissingMediumMessage);
        }
        doc.DecidedBy = Adaptation;
        Write(projectDir, doc);
        chat.Report($"Visual medium: {doc.VisualMedium}");
        return doc;
    }

    public static Document? ParseModelJson(string? raw)
    {
        if (VisualMediumStyles.ParseVisionFields(raw) is not { } fields) return null;
        var (medium, style, notes) = fields;
        var med = NormalizeMedium(medium);
        return new Document
        {
            VisualMedium = med,
            RenderStyleLock = string.IsNullOrWhiteSpace(style) ? DefaultStyleLock(med) : style.Trim(),
            Notes = notes,
        };
    }

}
