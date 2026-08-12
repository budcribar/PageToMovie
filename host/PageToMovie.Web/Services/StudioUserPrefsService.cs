using Microsoft.JSInterop;

namespace PageToMovie.Web.Services;

/// <summary>
/// G1/G2 — per-<b>user</b> DecisionCard preferences (path, edit focus, skip focus, last runtime).
/// Stored in browser localStorage under <c>ptm.userprefs.{userId}.*</c> — never project config.
/// Preferences bias defaults only; the shared project forecast is unchanged.
/// </summary>
public sealed class StudioUserPrefsService
{
    private readonly IJSRuntime _js;
    private readonly AdminSessionService _session;

    public StudioUserPrefsService(IJSRuntime js, AdminSessionService session)
    {
        _js = js;
        _session = session;
    }

    public string PreferPath { get; private set; } = "generate";
    public string? EditFocus { get; private set; }
    public bool SkipEditFocus { get; private set; }
    public int? LastRuntimeTargetMin { get; private set; }
    public bool Loaded { get; private set; }

    private string UserScope
    {
        get
        {
            var id = (_session.UserId ?? "local").Trim();
            if (id.Length == 0) id = "local";
            // Normalize email-style ids so storage is stable across tabs
            return id.ToLowerInvariant();
        }
    }

    private string Key(string name) => $"ptm.userprefs.{UserScope}.{name}";

    public async Task LoadAsync()
    {
        try
        {
            var path = await GetAsync("preferPath");
            if (string.Equals(path, "edit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "generate", StringComparison.OrdinalIgnoreCase))
                PreferPath = path.ToLowerInvariant();

            var focus = await GetAsync("editFocus");
            if (focus is "cost" or "duration" or "both" or "craft")
                EditFocus = focus;

            var skip = await GetAsync("skipEditFocus");
            SkipEditFocus = string.Equals(skip, "1", StringComparison.Ordinal)
                || string.Equals(skip, "true", StringComparison.OrdinalIgnoreCase);

            var runtime = await GetAsync("lastRuntimeTargetMin");
            if (int.TryParse(runtime, out var mins) && mins > 0 && mins < 24 * 60)
                LastRuntimeTargetMin = mins;
        }
        catch
        {
            /* localStorage optional (SSR / privacy) */
        }
        finally
        {
            Loaded = true;
        }
    }

    public async Task SetPreferPathAsync(string path)
    {
        PreferPath = path is "edit" or "generate" ? path : "generate";
        await SetAsync("preferPath", PreferPath);
    }

    public async Task SetEditFocusAsync(string? focus)
    {
        EditFocus = focus is "cost" or "duration" or "both" or "craft" ? focus : null;
        await SetAsync("editFocus", EditFocus);
    }

    public async Task SetSkipEditFocusAsync(bool skip)
    {
        SkipEditFocus = skip;
        await SetAsync("skipEditFocus", skip ? "1" : null);
    }

    public async Task SetLastRuntimeTargetMinAsync(int? minutes)
    {
        LastRuntimeTargetMin = minutes is > 0 ? minutes : null;
        await SetAsync("lastRuntimeTargetMin", LastRuntimeTargetMin?.ToString());
    }

    private async Task<string?> GetAsync(string name)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", Key(name));
        }
        catch
        {
            return null;
        }
    }

    private async Task SetAsync(string name, string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
                await _js.InvokeVoidAsync("localStorage.removeItem", Key(name));
            else
                await _js.InvokeVoidAsync("localStorage.setItem", Key(name), value);
        }
        catch { /* ignore */ }
    }
}
