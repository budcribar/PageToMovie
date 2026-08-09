using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminConfig
{

    private RuntimeConfigDto? _cfg;
    private string? _error;
    private string? _message;
    private bool _busy;
    private bool _masonryPending;

    private int _maxVideo = 4;
    private int _maxVideoPerUser = 2;
    private int _maxQueue = 5;
    private bool _useFakes;
    private string _videoMode = "MergeRealistic";
    private int _videoDelayMs = 200;
    private double _failRate;
    private int _rateLimitEveryN;
    private double _chargeMultiplier = 1.0;

    private bool _started;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;

            await Session.EnsureHydratedAsync();
            if (!Session.IsAdmin)
            {
                Nav.NavigateTo("/admin/login", forceLoad: true);
                return;
            }

            await LoadAsync();
            _masonryPending = true;
            StateHasChanged();
            return;
        }

        if (_masonryPending && _cfg is not null)
        {
            _masonryPending = false;
            try
            {
                await Js.InvokeVoidAsync("ptmMasonry.refresh", "#admin-config-masonry");
            }
            catch
            {
                // CDN / offline — cards still stack as a normal Bootstrap row
            }
        }
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            _cfg = await Api.GetAdminConfigAsync();
            if (_cfg is not null)
            {
                _maxVideo = _cfg.Capacity?.MaxVideoInFlight ?? 4;
                _maxVideoPerUser = _cfg.Capacity?.MaxVideoInFlightPerUser ?? 2;
                _maxQueue = _cfg.Capacity?.MaxQueuePerUser ?? 5;
                _useFakes = _cfg.UseFakes;
                _videoMode = _cfg.Fakes?.VideoMode ?? "MergeRealistic";
                _videoDelayMs = _cfg.Fakes?.VideoDelayMs ?? 200;
                _failRate = _cfg.Fakes?.FailRate ?? 0;
                _rateLimitEveryN = _cfg.Fakes?.RateLimitEveryN ?? 0;
                _chargeMultiplier = _cfg.ChargeMultiplier > 0 ? _cfg.ChargeMultiplier : 1.0;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }

    private async Task SaveAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            _cfg = await Api.SaveAdminConfigAsync(new RuntimeConfigUpdateRequest
            {
                Capacity = new CapacityRuntimeDto
                {
                    MaxVideoInFlight = _maxVideo,
                    MaxVideoInFlightPerUser = _maxVideoPerUser,
                    MaxQueuePerUser = _maxQueue,
                },
                Fakes = new FakesRuntimeDto
                {
                    VideoMode = _videoMode,
                    VideoDelayMs = _videoDelayMs,
                    FailRate = _failRate,
                    RateLimitEveryN = _rateLimitEveryN,
                },
                UseFakes = _useFakes,
                ChargeMultiplier = _chargeMultiplier,
            });
            _message = "Saved and applied (capacity + charge multiplier hot; UseFakes may need restart for full DI effect).";
            _masonryPending = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }
}
