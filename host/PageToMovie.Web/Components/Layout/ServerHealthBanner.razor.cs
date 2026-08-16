using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Layout;

/// <summary>
/// Slim top strip shown only while <see cref="ServerHealthState"/> is not Up. Ticks once a
/// second while down so the elapsed time reads live; nothing runs while the server is up.
/// </summary>
public partial class ServerHealthBanner : IDisposable
{
    [Inject] internal ServerHealthState Health { get; set; } = default;
    [Inject] internal IAppLocalizer L { get; set; } = default;

    private System.Threading.Timer? _ticker;
    private bool _disposed;

    private string Message => Health.Health switch
    {
        ServerHealth.Recovering => L["ServerHealth.Recovering"],
        _ when Health.IsLongOutage => L["ServerHealth.StillDown"],
        _ => L["ServerHealth.Restarting"],
    };

    private string ElapsedText => FormatElapsed(Health.Elapsed);

    /// <summary>m:ss (or h:mm:ss past an hour) — short enough for a one-line strip.</summary>
    internal static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    protected override void OnInitialized()
    {
        Health.Changed += OnHealthChanged;
        SyncTicker();
    }

    private void OnHealthChanged()
    {
        _ = InvokeAsync(() =>
        {
            SyncTicker();
            StateHasChanged();
        });
    }

    private void SyncTicker()
    {
        if (Health.IsDown)
        {
            _ticker ??= new System.Threading.Timer(
                _ => InvokeAsync(StateHasChanged), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else
        {
            _ticker?.Dispose();
            _ticker = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            Health.Changed -= OnHealthChanged;
            _ticker?.Dispose();
            _ticker = null;
        }
    }
}
