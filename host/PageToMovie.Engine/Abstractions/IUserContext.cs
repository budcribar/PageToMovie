namespace PageToMovie.Engine.Abstractions;

/// <summary>Current caller identity (HTTP request or job scope).</summary>
public interface IUserContext
{
    string UserId { get; }
    bool IsAdmin { get; }
    IReadOnlyList<string> Roles { get; }
    /// <summary>True when the request has a validated JWT (not merely X-User-Id spoof).</summary>
    bool IsAuthenticated { get; }
    /// <summary>Optional per-request API key override (X-Api-Key header) — treated as xAI/Grok.</summary>
    string? RequestApiKey { get; }
}

/// <summary>Resolve provider API keys for a user (personal DB key, then process env).</summary>
public interface IUserApiKeyProvider
{
    Task<string?> GetKeyAsync(string? userId, CancellationToken ct = default) =>
        GetKeyAsync(userId, PageToMovie.Core.Models.SupportedModelCatalog.ProviderIdForApiBase(
            PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase), ct);

    Task<string?> GetKeyAsync(string? userId, string providerId, CancellationToken ct = default);

    Task<bool> HasKeyAsync(string? userId, CancellationToken ct = default) =>
        HasKeyAsync(userId, PageToMovie.Core.Models.SupportedModelCatalog.ProviderIdForApiBase(
            PageToMovie.Core.Models.SupportedModelCatalog.XaiApiBase), ct);

    async Task<bool> HasKeyAsync(string? userId, string providerId, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await GetKeyAsync(userId, providerId, ct).ConfigureAwait(false));
}

/// <summary>
/// Ambient multi-provider API keys for HTTP clients (flows with AsyncLocal across job Task.Run).
/// <see cref="Current"/> remains the xAI/Grok key for existing Grok clients. Backed by a
/// provider-id-keyed dictionary rather than one positional param per provider — that shape
/// stopped scaling once a 5th/6th provider (Suno, AIMusicAPI) showed up; named <c>Current*</c>
/// properties below stay as the ergonomic per-client accessor, just backed by the dictionary.
/// </summary>
public static class ApiKeyScope
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string?>?> CurrentKeys = new();

    /// <summary>xAI / Grok ambient key (same as <see cref="Get"/>("grok")).</summary>
    public static string? Current => Get("grok");

    public static string? CurrentGemini => Get("gemini");

    public static string? CurrentAnthropic => Get("anthropic");

    public static string? CurrentFal => Get("fal");

    public static string? CurrentSuno => Get("suno");

    public static string? CurrentAiMusicApi => Get("aimusicapi");

    public static string? CurrentElevenLabs => Get("elevenlabs");

    public static string? Get(string providerId)
    {
        var k = CurrentKeys.Value;
        if (k is null) return null;
        var norm = NormalizeProvider(providerId);
        return k.TryGetValue(norm, out var v) ? v : null;
    }

    /// <summary>Push xAI-only key (legacy). Other provider slots stay null.</summary>
    public static IDisposable Push(string? xaiApiKey) =>
        Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["grok"] = xaiApiKey });

    public static IDisposable Push(IReadOnlyDictionary<string, string?> keysByProviderId)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in keysByProviderId)
            normalized[NormalizeProvider(k)] = v;

        var prev = CurrentKeys.Value;
        CurrentKeys.Value = normalized;
        return new Pop(prev);
    }

    public static string NormalizeProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "";
        var p = providerId.Trim().ToLowerInvariant();
        return p switch
        {
            "xai" or "grok" => "grok",
            "google" or "gemini" => "gemini",
            "claude" or "anthropic" => "anthropic",
            "fal" => "fal",
            "suno" => "suno",
            "aimusicapi" or "aimusicapi.ai" => "aimusicapi",
            "elevenlabs" or "eleven" => "elevenlabs",
            _ => p,
        };
    }

    private sealed class Pop : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?>? _prev;
        private bool _done;
        public Pop(IReadOnlyDictionary<string, string?>? prev) => _prev = prev;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            CurrentKeys.Value = _prev;
        }
    }
}

/// <summary>
/// Ambient user id for API-call cost logging (AsyncLocal — flows with jobs like <see cref="ApiKeyScope"/>).
/// </summary>
public static class UserApiCallScope
{
    private static readonly System.Threading.AsyncLocal<string?> CurrentUser = new();

    public static string? UserId =>
        string.IsNullOrWhiteSpace(CurrentUser.Value) ? null : CurrentUser.Value.Trim();

    public static IDisposable Push(string? userId)
    {
        var prev = CurrentUser.Value;
        CurrentUser.Value = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        return new Pop(prev);
    }

    private sealed class Pop : IDisposable
    {
        private readonly string? _prev;
        private bool _done;
        public Pop(string? prev) => _prev = prev;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            CurrentUser.Value = _prev;
        }
    }
}
