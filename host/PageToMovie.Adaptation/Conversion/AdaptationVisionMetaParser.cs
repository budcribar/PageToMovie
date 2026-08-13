using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>Pure parse of model VISION_META JSON into <see cref="AdaptationVisionMeta"/>.</summary>
public static class AdaptationVisionMetaParser
{
    public const string MediumPhotoreal = VisualMediumStyles.MediumPhotoreal;
    public const string MediumIllustrated = VisualMediumStyles.MediumIllustrated;
    public const string MediumStylized3d = VisualMediumStyles.MediumStylized3d;
    public const string MediumOther = VisualMediumStyles.MediumOther;

    public static AdaptationVisionMeta? ParseModelJson(string? raw)
    {
        if (VisualMediumStyles.ParseVisionFields(raw) is not { } fields) return null;
        var (medium, style, notes) = fields;
        var med = NormalizeMedium(medium);
        return new AdaptationVisionMeta
        {
            VisualMedium = med,
            RenderStyleLock = string.IsNullOrWhiteSpace(style) ? DefaultStyleLock(med) : style.Trim(),
            Notes = notes,
            DecidedBy = "adaptation",
        };
    }

    public static string NormalizeMedium(string? raw) =>
        VisualMediumStyles.NormalizeMedium(raw, allowAuto: false, mapMixedToPhotoreal: true);

    public static string DefaultStyleLock(string visualMedium) =>
        VisualMediumStyles.StyleLockFor(NormalizeMedium(visualMedium));
}
