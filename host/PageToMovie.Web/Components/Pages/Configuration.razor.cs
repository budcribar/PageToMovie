using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Configuration
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ConfigurationCatalog? _catalog;
    internal ConfigurationCatalog Catalog => _catalog ??= new ConfigurationCatalog(this);
    private ConfigurationKeys? _keys;
    internal ConfigurationKeys Keys => _keys ??= new ConfigurationKeys(this);
    private ConfigurationCoverage? _coverage;
    internal ConfigurationCoverage Coverage => _coverage ??= new ConfigurationCoverage(this);
    private ConfigurationProjectForm? _form;
    internal ConfigurationProjectForm Form => _form ??= new ConfigurationProjectForm(this);
    private ConfigurationMediaTheme? _media;
    internal ConfigurationMediaTheme Media => _media ??= new ConfigurationMediaTheme(this);

    internal void EnsureDomains()
    {
        _ = Catalog; _ = Keys; _ = Coverage; _ = Form; _ = Media;
    }


    internal bool _busy;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";

    internal List<string> _projectIds = new();

    internal Dictionary<string, JsonElement>? _cfg;


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        ParseFocusFromUri();
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
            if (!Session.IsLoggedIn)
            {
                _error = "Sign in required to view or save API keys.";
                Nav.NavigateTo("/login?returnUrl=/configuration");
                return;
            }

            try { _userSettings = await Engine.GetUserSettingsAsync(); } catch { }
            await LoadCatalogAsync();

            var projs = await Engine.GetProjectsAsync();
            _projectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                          ?? new List<string>();

            var candidateId = ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(candidateId))
                candidateId = projs?.Active?.Id;

            if (!string.IsNullOrWhiteSpace(candidateId))
            {
                _projectId = candidateId;
                if (!_projectIds.Contains(_projectId, StringComparer.OrdinalIgnoreCase))
                    _projectIds.Insert(0, _projectId);
            }
            else if (_projectIds.Count > 0)
            {
                _projectId = _projectIds[0];
            }
            else
            {
                _projectId = "";
            }

            if (!string.IsNullOrEmpty(_projectId))
                await LoadAsync();

            // Deep-link ?focus=voice|music|… opens just-in-time key panel for that coverage row.
            if (FocusActive)
                BeginAddKey(_focusCapability!);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }


    protected override void OnInitialized()
    {
        MediaFolder.Changed += OnMediaFolderChanged;
    }


    public void Dispose()
    {
        MediaFolder.Changed -= OnMediaFolderChanged;
        try { _autoSaveCts?.Cancel(); } catch { /* ignore */ }
        _autoSaveCts?.Dispose();
    }


    public async ValueTask DisposeAsync()
    {
        MediaFolder.Changed -= OnMediaFolderChanged;
        try { _autoSaveCts?.Cancel(); } catch { /* ignore */ }
        _autoSaveCts?.Dispose();
        // Flush any pending edits when leaving the page.
        if (_dirty && !string.IsNullOrWhiteSpace(_projectId) && _cfg is not null)
        {
            try { await PersistProjectConfigAsync(); }
            catch { /* best-effort on navigate away */ }
            _dirty = false;
        }
    }


    // ── Field forwarders (Host._x for markup children) ──
    internal List<SupportedModelDto> _allModels
    {
        get => Catalog._allModels;
        set => Catalog._allModels = value;
    }
    internal List<SupportedModelDto> _audioModels
    {
        get => Catalog._audioModels;
        set => Catalog._audioModels = value;
    }
    internal List<SupportedModelDto> _imageModels
    {
        get => Catalog._imageModels;
        set => Catalog._imageModels = value;
    }
    internal List<SupportedModelDto> _planningModels
    {
        get => Catalog._planningModels;
        set => Catalog._planningModels = value;
    }
    internal List<SupportedModelDto> _videoModels
    {
        get => Catalog._videoModels;
        set => Catalog._videoModels = value;
    }
    internal List<SupportedModelDto> _videoReviewModels
    {
        get => Catalog._videoReviewModels;
        set => Catalog._videoReviewModels = value;
    }
    internal List<SupportedModelDto> _visionModels
    {
        get => Catalog._visionModels;
        set => Catalog._visionModels = value;
    }
    internal List<SupportedModelDto> _voiceModels
    {
        get => Catalog._voiceModels;
        set => Catalog._voiceModels = value;
    }
    internal string? _apiKeyFeedback
    {
        get => Keys._apiKeyFeedback;
        set => Keys._apiKeyFeedback = value;
    }
    internal bool _apiKeySaving
    {
        get => Keys._apiKeySaving;
        set => Keys._apiKeySaving = value;
    }
    internal Dictionary<string, string?> _keyInputs => Keys._keyInputs;
    internal string? _savingProviderId
    {
        get => Keys._savingProviderId;
        set => Keys._savingProviderId = value;
    }
    internal UserSettingsDto? _userSettings
    {
        get => Keys._userSettings;
        set => Keys._userSettings = value;
    }
    internal HashSet<string> _visibleKeyProviders => Keys._visibleKeyProviders;
    internal string _audioModel
    {
        get => Coverage._audioModel;
        set => Coverage._audioModel = value;
    }
    internal int _backgroundMusicVolumePercent
    {
        get => Coverage._backgroundMusicVolumePercent;
        set => Coverage._backgroundMusicVolumePercent = value;
    }
    internal string? _coverageEditId
    {
        get => Coverage._coverageEditId;
        set => Coverage._coverageEditId = value;
    }
    internal string _coverageKeyMode
    {
        get => Coverage._coverageKeyMode;
        set => Coverage._coverageKeyMode = value;
    }
    internal string? _coverageKeyProviderId
    {
        get => Coverage._coverageKeyProviderId;
        set => Coverage._coverageKeyProviderId = value;
    }
    internal bool _enableBackgroundMusic
    {
        get => Coverage._enableBackgroundMusic;
        set => Coverage._enableBackgroundMusic = value;
    }
    internal string? _focusCapability
    {
        get => Coverage._focusCapability;
        set => Coverage._focusCapability = value;
    }
    internal string _imageModel
    {
        get => Coverage._imageModel;
        set => Coverage._imageModel = value;
    }
    internal string _modelName
    {
        get => Coverage._modelName;
        set => Coverage._modelName = value;
    }
    internal string _planningModel
    {
        get => Coverage._planningModel;
        set => Coverage._planningModel = value;
    }
    internal string _qualityModel
    {
        get => Coverage._qualityModel;
        set => Coverage._qualityModel = value;
    }
    internal string _visionModel
    {
        get => Coverage._visionModel;
        set => Coverage._visionModel = value;
    }
    internal string _voiceModel
    {
        get => Coverage._voiceModel;
        set => Coverage._voiceModel = value;
    }
    internal string _aspect
    {
        get => Form._aspect;
        set => Form._aspect = value;
    }
    internal double _audioGain
    {
        get => Form._audioGain;
        set => Form._audioGain = value;
    }
    internal CancellationTokenSource? _autoSaveCts
    {
        get => Form._autoSaveCts;
        set => Form._autoSaveCts = value;
    }
    internal int _autoSaveEpoch
    {
        get => Form._autoSaveEpoch;
        set => Form._autoSaveEpoch = value;
    }
    internal string _blueprintFile
    {
        get => Form._blueprintFile;
        set => Form._blueprintFile = value;
    }
    internal bool _dirty
    {
        get => Form._dirty;
        set => Form._dirty = value;
    }
    internal int _durationSeconds
    {
        get => Form._durationSeconds;
        set => Form._durationSeconds = value;
    }
    internal bool _mergeAfterClip
    {
        get => Form._mergeAfterClip;
        set => Form._mergeAfterClip = value;
    }
    internal string _preferredVideoEditor
    {
        get => Form._preferredVideoEditor;
        set => Form._preferredVideoEditor = value;
    }
    internal string? _projectDir
    {
        get => Form._projectDir;
        set => Form._projectDir = value;
    }
    internal int _qaFrameCount
    {
        get => Form._qaFrameCount;
        set => Form._qaFrameCount = value;
    }
    internal int _qaMaxRetries
    {
        get => Form._qaMaxRetries;
        set => Form._qaMaxRetries = value;
    }
    internal bool _qaRetry
    {
        get => Form._qaRetry;
        set => Form._qaRetry = value;
    }
    internal bool _rebuildWip
    {
        get => Form._rebuildWip;
        set => Form._rebuildWip = value;
    }
    internal bool _regenSilent
    {
        get => Form._regenSilent;
        set => Form._regenSilent = value;
    }
    internal string _resolution
    {
        get => Form._resolution;
        set => Form._resolution = value;
    }
    internal string? _saveStatus
    {
        get => Form._saveStatus;
        set => Form._saveStatus = value;
    }
    internal bool _smartContinuation
    {
        get => Form._smartContinuation;
        set => Form._smartContinuation = value;
    }
    internal bool _useDurationDefaults
    {
        get => Form._useDurationDefaults;
        set => Form._useDurationDefaults = value;
    }
    internal string _wipPath
    {
        get => Form._wipPath;
        set => Form._wipPath = value;
    }
    internal string _uiTheme
    {
        get => Media._uiTheme;
        set => Media._uiTheme = value;
    }
}
