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
/// Structured visual medium decided at book→screenplay (adaptation) time.
/// Source of truth for photoreal vs illustrated — not regex over Fountain prose.
/// Primary store: import sidecar <c>source/extract_meta.json</c> (book_kind + visual_medium).
/// Optional overlay: <c>source/vision_meta.json</c>.
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

        /// <summary>Machine enum: photoreal_live_action | illustrated_picture_book | stylized_3d_animated | other</summary>
        [JsonPropertyName("visual_medium")]
        public string VisualMedium { get; set; } = MediumPhotoreal;

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

    public static Document? TryRead(string projectDir)
    {
        // 1) Optional overlay (tests / explicit writes)
        var overlay = TryReadVisionFile(GetPath(projectDir));
        if (overlay is not null &&
            string.Equals(overlay.DecidedBy, Adaptation, StringComparison.OrdinalIgnoreCase))
            return overlay;

        // 2) Import extract_meta.json (book_full + analysis at prepare time)
        var fromExtract = TryReadExtractMeta(projectDir);
        if (fromExtract is not null)
            return fromExtract;

        // 3) vision_meta.json fallback
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
        (medium, source) = ApplyLegacyBookKind(root, medium, source);

        if (string.IsNullOrWhiteSpace(medium) && string.IsNullOrWhiteSpace(style))
            return null;

        var med = NormalizeMedium(medium ?? MediumPhotoreal);
        return new Document
        {
            VisualMedium = med,
            RenderStyleLock = string.IsNullOrWhiteSpace(style) ? DefaultStyleLock(med) : style.Trim(),
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

    private static (string? Medium, string? Source) ApplyLegacyBookKind(
        JsonElement root, string? medium, string? source)
    {
        if (!string.IsNullOrWhiteSpace(medium) ||
            !root.TryGetProperty("book_kind", out var bk) ||
            bk.ValueKind != JsonValueKind.String)
            return (medium, source);

        medium = string.Equals(bk.GetString(), "picture_book", StringComparison.OrdinalIgnoreCase)
            ? MediumIllustrated
            : MediumPhotoreal;
        source ??= "import_book_kind";
        return (medium, source);
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
        if (string.IsNullOrWhiteSpace(doc.RenderStyleLock))
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
        NormalizeMedium(visualMedium) is MediumIllustrated or MediumStylized3d;

    public static string DefaultStyleLock(string visualMedium) =>
        VisualMediumStyles.StyleLockFor(NormalizeMedium(visualMedium));

    public static string DefaultAspectRatio(string? visualMedium) =>
        VisualMediumStyles.DefaultAspectRatioFor(visualMedium);

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

        var doc = ParseModelJson(raw) ?? new Document
        {
            VisualMedium = MediumPhotoreal,
            RenderStyleLock = DefaultStyleLock(MediumPhotoreal),
            Notes = "fallback: model JSON unparseable",
        };
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

    /// <summary>Upsert from cast extract when adaptation metadata is missing.</summary>
    public static void UpsertFromCast(string projectDir, string? renderStyleLock, string? performanceLock)
    {
        if (string.IsNullOrWhiteSpace(renderStyleLock) && string.IsNullOrWhiteSpace(performanceLock))
            return;
        var existing = TryRead(projectDir);
        // Do not overwrite adaptation decision with cast unless missing.
        if (existing is not null &&
            string.Equals(existing.DecidedBy, Adaptation, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(existing.RenderStyleLock))
        {
            if (!string.IsNullOrWhiteSpace(performanceLock) && string.IsNullOrWhiteSpace(existing.PerformanceLock))
            {
                existing.PerformanceLock = performanceLock.Trim();
                Write(projectDir, existing);
            }
            return;
        }

        var med = existing?.VisualMedium ?? MediumPhotoreal;
        if (!string.IsNullOrWhiteSpace(renderStyleLock))
        {
            var r = renderStyleLock;
            if (r.Contains("picture", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("illustrat", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("cartoon", StringComparison.OrdinalIgnoreCase))
                med = MediumIllustrated;
            else if (r.Contains("photoreal", StringComparison.OrdinalIgnoreCase) ||
                     r.Contains("live-action", StringComparison.OrdinalIgnoreCase) ||
                     r.Contains("live action", StringComparison.OrdinalIgnoreCase))
                med = MediumPhotoreal;
        }

        Write(projectDir, new Document
        {
            VisualMedium = med,
            RenderStyleLock = renderStyleLock?.Trim() ?? existing?.RenderStyleLock ?? DefaultStyleLock(med),
            PerformanceLock = performanceLock?.Trim() ?? existing?.PerformanceLock,
            DecidedBy = existing is null ? "cast_extract" : existing.DecidedBy,
            Notes = existing?.Notes,
        });
    }
}
