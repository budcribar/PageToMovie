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
    internal string? _error;
    internal string? _message;
    internal bool _busy;
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

    private int? _maxSpeakingCast;
    private int? _maxDialogueWords;
    private int? _voMaxSentences;
    private int? _sceneCountMin;
    private int? _sceneCountMax;
    private int? _minAudioCuesPerScene;
    private int? _minAudioCuesAtPeak;
    private int? _bodyWordsPerMinute;

    private int _imageTimeoutSeconds = 300;
    private int _videoTimeoutSeconds = 900;
    private int _chatTimeoutSeconds = 1200;
    private int _audioTimeoutSeconds = 300;

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
                _maxSpeakingCast = _cfg.Adaptation?.MaxSpeakingCast;
                _maxDialogueWords = _cfg.Adaptation?.MaxDialogueWords;
                _voMaxSentences = _cfg.Adaptation?.VoMaxSentences;
                _sceneCountMin = _cfg.Adaptation?.SceneCountMin;
                _sceneCountMax = _cfg.Adaptation?.SceneCountMax;
                _minAudioCuesPerScene = _cfg.Adaptation?.MinAudioCuesPerScene;
                _minAudioCuesAtPeak = _cfg.Adaptation?.MinAudioCuesAtPeak;
                _bodyWordsPerMinute = _cfg.Adaptation?.BodyWordsPerMinute;
                _imageTimeoutSeconds = _cfg.Timeouts?.ImageTimeoutSeconds ?? 300;
                _videoTimeoutSeconds = _cfg.Timeouts?.VideoTimeoutSeconds ?? 900;
                _chatTimeoutSeconds = _cfg.Timeouts?.ChatTimeoutSeconds ?? 1200;
                _audioTimeoutSeconds = _cfg.Timeouts?.AudioTimeoutSeconds ?? 300;
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
                Adaptation = new AdaptationRuntimeDto
                {
                    MaxSpeakingCast = _maxSpeakingCast,
                    MaxDialogueWords = _maxDialogueWords,
                    VoMaxSentences = _voMaxSentences,
                    SceneCountMin = _sceneCountMin,
                    SceneCountMax = _sceneCountMax,
                    MinAudioCuesPerScene = _minAudioCuesPerScene,
                    MinAudioCuesAtPeak = _minAudioCuesAtPeak,
                    BodyWordsPerMinute = _bodyWordsPerMinute,
                },
                Timeouts = new TimeoutsRuntimeDto
                {
                    ImageTimeoutSeconds = _imageTimeoutSeconds,
                    VideoTimeoutSeconds = _videoTimeoutSeconds,
                    ChatTimeoutSeconds = _chatTimeoutSeconds,
                    AudioTimeoutSeconds = _audioTimeoutSeconds,
                },
                UseFakes = _useFakes,
                ChargeMultiplier = _chargeMultiplier,
            });
            _message = "Saved and applied (capacity + charge multiplier + timeouts hot; UseFakes may need restart for full DI effect).";
            _masonryPending = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }
}
