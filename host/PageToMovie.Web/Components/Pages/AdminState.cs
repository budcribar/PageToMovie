using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin
{
    /// <summary>Core admin state / poll / hub loop domain.</summary>
    public sealed class AdminState
    {
        private readonly Admin S;
        public AdminState(Admin host) => S = host;

        internal AdminStateDto? _state;
        internal int _apiInFlight;
        internal int _capacityRejects;
        internal int _lockConflicts;
        internal PeriodicTimer? _timer;
        internal CancellationTokenSource? _pollCts;

        internal async Task ConnectHubAsync()
        {
            try
            {
                await S.Hub.StartAsync();
                S.Jobs._hubLive = S.Hub.IsConnected;
                await S.InvokeAsync(S.StateHasChanged);
            }
            catch
            {
                S.Jobs._hubLive = false;
            }
        }

        internal void OnAdminState(object? payload)
        {
            S.Jobs._hubLive = true;
            if (payload is not null && TryApplyAdminStatePayload(payload))
                return;
            _ = S.InvokeAsync(RefreshAsync);
        }

        private bool TryApplyAdminStatePayload(object payload)
        {
            try
            {
                var dto = DeserializeAdminStatePayload(payload);
                if (dto is null)
                    return false;
                ApplyAdminStateDto(dto);
                _ = S.InvokeAsync(async () =>
                {
                    await S.Telemetry.UpdateChartsAsync();
                    S.StateHasChanged();
                });
                return true;
            }
            catch { /* fallback to HTTP refresh if payload shape differs */ }
            return false;
        }

        private static AdminStateDto? DeserializeAdminStatePayload(object payload)
        {
            if (payload is System.Text.Json.JsonElement elem)
                return System.Text.Json.JsonSerializer.Deserialize<AdminStateDto>(elem.GetRawText(), EngineApiClient.JsonOpts);
            var json = System.Text.Json.JsonSerializer.Serialize(payload, EngineApiClient.JsonOpts);
            return System.Text.Json.JsonSerializer.Deserialize<AdminStateDto>(json, EngineApiClient.JsonOpts);
        }

        private void ApplyAdminStateDto(AdminStateDto dto)
        {
            _state = dto;
            if (dto.ApiInFlight > 0) _apiInFlight = dto.ApiInFlight;
            if (dto.CapacityRejects > 0) _capacityRejects = dto.CapacityRejects;
            if (dto.LockConflicts > 0) _lockConflicts = dto.LockConflicts;
            if (dto.Locks is { Count: > 0 }) S.Jobs._locks = dto.Locks;
            if (dto.LoadSim is not null) S.Telemetry._loadSim = dto.LoadSim;
            if (dto.ProcessHistory is { Count: > 0 }) S.Telemetry._processHistory = dto.ProcessHistory;
        }

        internal async Task PollLoopAsync(CancellationToken ct)
        {
            try
            {
                while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
                {
                    // Paused during an outage so many admin tabs do not hammer a booting container;
                    // the health probe owns retrying and the first tick after Up refreshes.
                    if (S.Health.IsDown) continue;
                    try
                    {
                        // Always refresh so Jobs (running/queued) stay current even when
                        // SignalR reports "live" but admin:ops is quiet or hubs are 502.
                        // Full refresh is intentional; admin state is the ops source of truth.
                        await RefreshAsync();
                        await S.InvokeAsync(S.StateHasChanged);
                    }
                    catch { /* keep polling */ }
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
        }

        internal async Task RefreshAsync()
        {
            if (!S.Session.IsAdmin) return;
            S._busy = true;
            try
            {
                _state = await S.Api.GetAdminStateAsync();
                if (_state is not null)
                {
                    _apiInFlight = _state.ApiInFlight;
                    _capacityRejects = _state.CapacityRejects;
                    _lockConflicts = _state.LockConflicts;
                    S.Jobs._locks = _state.Locks ?? new();
                    S.Telemetry._loadSim = _state.LoadSim;
                    S.Telemetry._processHistory = _state.ProcessHistory ?? new();
                }
                S.Telemetry._timingTelemetry = await S.Api.GetAdminTimingTelemetryTrendAsync();
                S._error = null;
                await S.Archive.RefreshProjectOptionsAsync();
                await S.Telemetry.UpdateChartsAsync();
                await S.Telemetry.RefreshGenerationErrorsAsync();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                // Only hard-logout on clear auth failures (not every message containing "admin")
                var msg = ex.Message;
                if (msg.Contains("403", StringComparison.Ordinal) ||
                    msg.Contains("401", StringComparison.Ordinal) ||
                    msg.Contains("admin role required", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    await S.Session.ClearAsync();
                    S.Nav.NavigateTo("/admin/login", forceLoad: true);
                }
            }
            finally
            {
                S._busy = false;
            }
        }

        internal void DisposePolling()
        {
            _pollCts?.Cancel();
            _timer?.Dispose();
            _pollCts?.Dispose();
            _timer = null;
            _pollCts = null;
        }

        internal static string GetDiskProgressBarClass(double pct) => pct switch
        {
            >= 90.0 => "bg-danger",
            >= 75.0 => "bg-warning",
            _ => "bg-success"
        };

        internal static string FormatUptime(long sec)
        {
            if (sec < 60) return $"{sec}s";
            if (sec < 3600) return $"{sec / 60}m {sec % 60}s";
            return $"{sec / 3600}h {(sec % 3600) / 60}m";
        }

        internal static string FormatAge(long? ms)
        {
            if (ms is null or < 0) return "—";
            var s = ms.Value / 1000.0;
            if (s < 60) return $"{s:0}s";
            if (s < 3600) return $"{s / 60:0.0}m";
            return $"{s / 3600:0.0}h";
        }
    }
}
