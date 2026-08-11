namespace PageToMovie.Core.Models;

/// <summary>
/// G4 — budget/draft vs full production as a <b>mode on the same project</b>, not a separate app path.
/// Config key: <c>production_mode</c> = <see cref="Draft"/> | <see cref="Full"/> (default full).
/// Draft softens first-watch cast plates (G3) so operators can generate without locking every portrait.
/// </summary>
public static class ProductionModes
{
    public const string ConfigKey = "production_mode";

    /// <summary>Cheaper / faster first watch — cast plates optional, same Film hub.</summary>
    public const string Draft = "draft";

    /// <summary>Identity-locked production path — cast plates required before video spend.</summary>
    public const string Full = "full";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Full;
        var v = value.Trim().ToLowerInvariant();
        return v is "draft" or "budget" or "cheap" ? Draft : Full;
    }

    public static bool IsDraft(string? value) =>
        string.Equals(Normalize(value), Draft, StringComparison.Ordinal);

    public static bool IsFull(string? value) => !IsDraft(value);

    /// <summary>Read from a loose config dictionary (pipeline_config JSON).</summary>
    public static string FromConfig(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? cfg)
    {
        if (cfg is null) return Full;
        if (!cfg.TryGetValue(ConfigKey, out var el)) return Full;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => Normalize(el.GetString()),
            _ => Full,
        };
    }

    public static bool IsDraftConfig(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? cfg) =>
        IsDraft(FromConfig(cfg));

    public static string Label(string? value) =>
        IsDraft(value) ? "Draft (plates optional)" : "Full (locked plates)";
}
