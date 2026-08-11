namespace PageToMovie.Core.Models;

/// <summary>
/// H1/H2 — durable take-event <c>take_kind</c> / trigger values for every billed video gen.
/// Used by cost ledger events and (later) expected_takes learning.
/// </summary>
public static class VideoTakeKinds
{
    public const string Initial = "initial";
    public const string UserRegen = "user_regen";
    public const string StaleRegen = "stale_regen";
    public const string QaAuto = "qa_auto";
    public const string FillHoles = "fill_holes";

    /// <summary>Normalize a free-form string to a known kind, or null if unknown/empty.</summary>
    public static string? NormalizeOptional(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim().ToLowerInvariant();
        return k switch
        {
            Initial or "first" or "new" => Initial,
            UserRegen or "regen" or "regenerate" or "force" => UserRegen,
            StaleRegen or "stale" => StaleRegen,
            QaAuto or "qa" or "qa_retry" or "auto_retry" => QaAuto,
            FillHoles or "fill" or "missing" or "only_missing" => FillHoles,
            _ => null,
        };
    }

    public static string Normalize(string? kind, string fallback = Initial) =>
        NormalizeOptional(kind) ?? fallback;

    /// <summary>
    /// Resolve the take kind for a clip gen.
    /// Explicit job trigger wins (except QA override should be passed as <paramref name="isQaRetry"/>).
    /// Otherwise: first successful clip → <see cref="Initial"/>; overwriting existing → <see cref="UserRegen"/>.
    /// </summary>
    public static string Resolve(string? explicitTrigger, bool clipHadVideoBefore, bool isQaRetry = false)
    {
        if (isQaRetry) return QaAuto;
        var t = NormalizeOptional(explicitTrigger);
        if (t is not null)
        {
            // Can't be "first pass" kinds if the clip already had media.
            if (clipHadVideoBefore && (t == Initial || t == FillHoles))
                return UserRegen;
            return t;
        }
        return clipHadVideoBefore ? UserRegen : Initial;
    }
}
