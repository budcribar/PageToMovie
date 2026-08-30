namespace PageToMovie.Core.Billing;

/// <summary>
/// Vendor list rate → customer-facing display charge (admin multiplier).
/// <para>
/// <b>Storage rule:</b> databases and cost ledgers keep <b>list rates only</b>.
/// The multiplier is applied at read/display time (and optionally when debiting credits),
/// never written as a permanent "charged" cost row.
/// </para>
/// </summary>
public static class ChargePricing
{
    /// <summary>Clamp multiplier to a sane range. Non-finite or negative → 1.0 (pass-through).</summary>
    public static double ClampMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier < 0)
            return 1.0;
        return Math.Clamp(multiplier, 0, 100);
    }

    /// <summary>listUsd × multiplier, rounded to 6 dp.</summary>
    public static double ToCharge(double listUsd, double multiplier)
    {
        var list = Math.Max(0, listUsd);
        return Math.Round(list * ClampMultiplier(multiplier), 6);
    }

    /// <summary>Money display round (2 dp).</summary>
    public static double RoundMoney(double usd) => Math.Round(usd, 2);

    /// <summary>
    /// Recover vendor list rate from a ledger/API row.
    /// Prefer explicit <paramref name="listUsd"/>; if a legacy row only stored charged
    /// <paramref name="storedUsd"/> with a write-time multiplier, divide it back out.
    /// </summary>
    public static double ResolveListUsd(double storedUsd, double? listUsd, double? eventMultiplier)
    {
        if (listUsd is double lu && double.IsFinite(lu) && lu >= 0)
            return lu;
        if (eventMultiplier is double em && em > 0 && double.IsFinite(em) && double.IsFinite(storedUsd))
            return Math.Max(0, storedUsd / em);
        return Math.Max(0, storedUsd);
    }

    /// <summary>
    /// Customer-facing amount for UI: list rate × <paramref name="currentMultiplier"/>
    /// (always the admin setting now — not a frozen per-row charge).
    /// </summary>
    public static double DisplayCharge(
        double storedUsd,
        double? listUsd,
        double? eventMultiplier,
        double currentMultiplier)
    {
        var list = ResolveListUsd(storedUsd, listUsd, eventMultiplier);
        return ToCharge(list, currentMultiplier);
    }
}
