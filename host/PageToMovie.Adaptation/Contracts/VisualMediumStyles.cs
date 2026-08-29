using System.Text.Json;

namespace PageToMovie.Adaptation.Contracts;

/// <summary>
/// Single source of truth for the visual-medium tokens, their default STYLE LOCK prose, and the
/// shared VISION_META JSON parse prologue. Referenced by both
/// <see cref="Conversion.AdaptationVisionMetaParser"/> (Adaptation) and <c>ProjectVisionMeta</c>
/// (Engine, which references Adaptation).
///
/// Note: medium *normalization* is intentionally NOT shared — the two callers diverge
/// (the adaptation parser maps "mixed"→photoreal with no "auto"; the Engine store recognizes
/// "auto"/empty and has no "mixed"), so each keeps its own <c>NormalizeMedium</c>.
/// </summary>
public static class VisualMediumStyles
{
    public const string MediumPhotoreal = "photoreal_live_action";
    public const string MediumIllustrated = "illustrated_picture_book";
    public const string MediumStylized3d = "stylized_3d_animated";
    public const string MediumOther = "other";

    public const string PhotorealStyleLock =
        "STYLE LOCK: photoreal live-action continuity portrait — naturalistic face and wardrobe, " +
        "period-appropriate when the story implies it. NOT cartoon, NOT illustration, NOT anime";

    public const string IllustratedStyleLock =
        "STYLE LOCK: stylized animated children's picture-book look for ALL on-screen cast " +
        "(animals and humans share the same medium) -- not photoreal, not live-action";

    public const string Stylized3dStyleLock =
        "STYLE LOCK: stylized 3D animated children's feature look — coherent CG medium for all cast; " +
        "not photoreal live-action, not flat 2D doodle";

    public const string LabelAuto = "Auto (infer from book)";
    public const string LabelPhotoreal = "Photoreal / live action";
    public const string LabelIllustrated = "Picture book / illustrated";
    public const string LabelStylized3d = "Stylized 3D animation";
    public const string LabelOther = "Other / stylized";

    /// <summary>Operator-facing Look label for a visual-medium token (canonical or alias).</summary>
    public static string DisplayLabel(string? medium)
    {
        var s = (medium ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return "";
        var key = s.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (IsAutoToken(key)) return LabelAuto;
        return NormalizeMedium(s) switch
        {
            MediumPhotoreal => LabelPhotoreal,
            MediumIllustrated => LabelIllustrated,
            MediumStylized3d => LabelStylized3d,
            MediumOther => LabelOther,
            _ => s,
        };
    }

    /// <summary>Default STYLE LOCK prose for an already-normalized medium token.</summary>
    public static string StyleLockFor(string normalizedMedium) => normalizedMedium switch
    {
        MediumIllustrated => IllustratedStyleLock,
        MediumStylized3d => Stylized3dStyleLock,
        _ => PhotorealStyleLock,
    };

    /// <summary>
    /// Negative tokens that keep video/image gens from flipping to the opposite medium family.
    /// Empty for <see cref="MediumOther"/> — no invented opposite when the book did not pick a family.
    /// </summary>
    public static string NegativeFor(string normalizedMedium) => normalizedMedium switch
    {
        MediumIllustrated => "photoreal, live-action, CGI realism, 3D render",
        MediumPhotoreal => "watercolor, flat illustration, cartoon",
        MediumStylized3d => "photoreal live-action, flat 2D doodle, watercolor illustration",
        _ => "",
    };

    /// <summary>Short establishing clause for location plates (no people) in this medium.</summary>
    public static string LocationPlateClause(string normalizedMedium) => normalizedMedium switch
    {
        MediumIllustrated =>
            "Picture-book / illustrated establishing still — painted or watercolor set, not photoreal, not live-action, not 3D CGI. ",
        MediumStylized3d =>
            "Stylized 3D animated establishing still — coherent CG set, not photoreal live-action, not flat 2D doodle. ",
        MediumPhotoreal =>
            "Photoreal live-action establishing still — naturalistic set, not cartoon, not illustration. ",
        _ =>
            "Cinematic establishing still matching the project's visual medium. ",
    };

    /// <summary>
    /// Identity-reinforce ban list. Illustration/cartoon are only banned when the film is photoreal.
    /// </summary>
    public static string IdentityMediumDriftClause(string normalizedMedium) => normalizedMedium switch
    {
        MediumPhotoreal =>
            "do not drift to illustration, anime, cartoon, or a different face/wardrobe",
        MediumIllustrated =>
            "do not drift to photoreal, live-action, CGI realism, or a different face/wardrobe",
        MediumStylized3d =>
            "do not drift to photoreal live-action, flat 2D illustration, or a different face/wardrobe",
        _ =>
            "do not drift to a different art medium, face, or wardrobe",
    };

    /// <summary>True when this medium is a drawn / picture-book family (image-edit illustrated flag).</summary>
    public static bool PrefersIllustrated(string? visualMedium)
    {
        var n = NormalizeMedium(visualMedium);
        return n is MediumIllustrated or MediumStylized3d;
    }

    /// <summary>
    /// Compared on the leading clause, where the medium is named — descriptive tails may be
    /// reworded without meaning a different lock.
    /// </summary>
    public static bool StyleLocksAgree(string? planned, string? current)
    {
        var a = StripStyleLockLabel(planned);
        var b = StripStyleLockLabel(current);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            return true;
        const int clause = 40;
        var headA = a.Length <= clause ? a : a[..clause];
        var headB = b.Length <= clause ? b : b[..clause];
        return string.Equals(headA, headB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The live style head is prose and may or may not lead with the label.</summary>
    public static string StripStyleLockLabel(string? value)
    {
        var t = (value ?? "").Trim();
        if (t.Length == 0)
            return "";
        const string label = "STYLE LOCK:";
        if (t.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            t = t[label.Length..].Trim();
        else if (t.StartsWith("STYLE LOCK (hard):", StringComparison.OrdinalIgnoreCase))
        {
            var colon = t.IndexOf(':');
            if (colon >= 0)
                t = t[(colon + 1)..].Trim();
        }
        return t;
    }

    /// <summary>Default target aspect ratio for a visual medium (4:3 for illustrated picture books, 16:9 for photoreal).</summary>
    public static string DefaultAspectRatioFor(string? normalizedMedium) =>
        NormalizeMedium(normalizedMedium) == MediumIllustrated ? "4:3" : "16:9";

    /// <summary>
    /// Shared visual medium token normalization for Stage 1 adaptation and project config stores.
    /// </summary>
    public static string NormalizeMedium(string? raw, bool allowAuto = false, bool mapMixedToPhotoreal = false)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (allowAuto && IsAutoToken(s))
            return "auto";
        if (IsPhotorealAlias(s, mapMixedToPhotoreal))
            return MediumPhotoreal;
        if (IsIllustratedAlias(s))
            return MediumIllustrated;
        if (IsStylized3dAlias(s))
            return MediumStylized3d;
        if (IsCanonicalMediumToken(s))
            return s;
        if (LooksIllustrated(s))
            return MediumIllustrated;
        if (LooksPhotoreal(s))
            return MediumPhotoreal;
        return MediumOther;
    }

    private static bool IsAutoToken(string s) =>
        string.IsNullOrEmpty(s) || s is "auto" or "infer" or "default";

    private static bool IsPhotorealAlias(string s, bool mapMixedToPhotoreal) =>
        s is "photoreal" or "photo_real" or "live_action" or "liveaction" or "photoreal_live_action"
            or "period_drama" or "gothic_live_action"
        || (mapMixedToPhotoreal && s == "mixed");

    private static bool IsIllustratedAlias(string s) =>
        s is "illustrated" or "picture_book" or "picturebook" or "illustration"
            or "illustrated_picture_book" or "childrens_book" or "storybook";

    private static bool IsStylized3dAlias(string s) =>
        s is "stylized_3d" or "stylized_3d_animated" or "cg_animated" or "pixar" or "3d_animated";

    private static bool IsCanonicalMediumToken(string s) =>
        s is MediumPhotoreal or MediumIllustrated or MediumStylized3d or MediumOther;

    private static bool LooksIllustrated(string s) =>
        s.Contains("picture") || s.Contains("illustrat") || s.Contains("cartoon") || s.Contains("storybook");

    private static bool LooksPhotoreal(string s) =>
        s.Contains("photoreal") || s.Contains("live_action") || s.Contains("live action") || s.Contains("period");

    /// <summary>Strips a leading/trailing ``` (optionally ```json) code fence from a model reply.</summary>
    public static string StripJsonFence(string trimmed)
    {
        var t = trimmed;
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl > 0) t = t[(firstNl + 1)..];
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) t = t[..fence];
            t = t.Trim();
        }
        return t;
    }

    /// <summary>
    /// Shared VISION_META parse prologue: trims/fence-strips <paramref name="raw"/>, parses the JSON,
    /// and reads the <c>visual_medium</c> / <c>render_style_lock</c> / <c>notes</c> fields. Returns null
    /// for blank input, unparseable JSON, or when both medium and style are absent — exactly matching the
    /// callers' original guard. Each caller applies its own medium normalization and result type.
    /// </summary>
    public static (string? Medium, string? Style, string? Notes)? ParseVisionFields(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = StripJsonFence(raw.Trim());
        try
        {
            using var jd = JsonDocument.Parse(t);
            var root = jd.RootElement;
            var medium = root.TryGetProperty("visual_medium", out var m) ? m.GetString() : null;
            var style = root.TryGetProperty("render_style_lock", out var s) ? s.GetString() : null;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(medium) && string.IsNullOrWhiteSpace(style))
                return null;
            return (medium, style, notes);
        }
        catch
        {
            return null;
        }
    }
}
