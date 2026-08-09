using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Configuration
{
    internal bool _busy;
    internal string? _error;
    internal string? _message;
    internal string? _saveStatus;
    internal bool _dirty;
    internal CancellationTokenSource? _autoSaveCts;
    internal int _autoSaveEpoch;
    internal string _projectId = "";
    internal List<string> _projectIds = new();
    internal Dictionary<string, JsonElement>? _cfg;
    internal string? _projectDir;

    internal string _uiTheme = "dark";
    internal string _preferredVideoEditor = "ClipChamp";
    internal string _blueprintFile = "blueprint.clips.grok.json";
    internal string _modelName = "";
    internal string _imageModel = "";
    internal string _planningModel = "";
    internal string _visionModel = "";
    internal string _qualityModel = "";
    internal string _audioModel = "none";
    internal string _voiceModel = "none";
    /// <summary>Deep-link from gated features: music | voice | review | video | …</summary>
    internal string? _focusCapability;
    internal bool _enableBackgroundMusic = true;
    internal int _backgroundMusicVolumePercent = 20;
    internal string _aspect = "16:9";
    internal string _resolution = "480p";
    internal int _durationSeconds = 8;
    internal bool _useDurationDefaults = true;
    internal bool _smartContinuation = true;
    internal bool _mergeAfterClip = true;
    internal bool _qaRetry = true;
    internal bool _regenSilent = true;
    internal bool _rebuildWip = true;
    internal int _qaMaxRetries = 2;
    internal int _qaFrameCount = 4;
    internal double _audioGain = 6;
    internal string _wipPath = "assets/movie_wip.mp4";

    internal List<SupportedModelDto> _allModels = new();
    internal List<SupportedModelDto> _videoModels = new();
    internal List<SupportedModelDto> _imageModels = new();
    internal List<SupportedModelDto> _planningModels = new();
    internal List<SupportedModelDto> _visionModels = new();
    internal List<SupportedModelDto> _videoReviewModels = new();
    internal List<SupportedModelDto> _audioModels = new();
    internal List<SupportedModelDto> _voiceModels = new();

    protected override async Task OnInitializedAsync()
    {
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

    internal async Task OnProjectChangedAsync(ChangeEventArgs e)
    {
        var id = e.Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(id)) return;
        _projectId = id;
        ActiveProject.Set(id);
        await LoadAsync();
    }

    internal async Task LoadCatalogAsync()
    {
        // Catalog is the only source of truth — never invent models in UI code.
        _allModels = new();
        _videoModels = new();
        _imageModels = new();
        _planningModels = new();
        _visionModels = new();
        _videoReviewModels = new();
        _audioModels = new();
        _voiceModels = new();

        try
        {
            try
            {
                var raw = await Engine.GetModelsCatalogJsonAsync();
                if (!string.IsNullOrWhiteSpace(raw))
                    SupportedModelCatalog.TryLoadFromJson(raw);
            }
            catch (Exception ex)
            {
                _error = $"Could not load models catalog: {ex.Message}";
            }

            var list = await Engine.GetSupportedModelsAsync();
            _allModels = (list ?? Array.Empty<SupportedModelDto>())
                .Where(m => Session.IsAdmin || !m.LabMode)
                .ToList();
            // Hydrate from static catalog when the API list is empty/stale (WASM must still show music/voice).
            if (_allModels.Count == 0)
            {
                try
                {
                    _allModels = SupportedModelCatalog.ToDtoList(enabledOnly: true, includeLabModels: Session.IsAdmin)
                        .Where(m => Session.IsAdmin || !m.LabMode)
                        .ToList();
                }
                catch { /* catalog not loaded */ }
            }
            bool Cap(SupportedModelDto m, string c)
            {
                if (string.Equals(m.Capability, c, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Defensive: some payloads omit capability; resolve via catalog entry.
                try
                {
                    var entry = SupportedModelCatalog.Find(m.Id);
                    if (entry is null || !entry.Enabled) return false;
                    return string.Equals(entry.Capability.ToString(), c, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            _videoModels = _allModels.Where(m => Cap(m, "video")).ToList();
            _imageModels = _allModels.Where(m => Cap(m, "image")).ToList();
            _planningModels = _allModels.Where(m => Cap(m, "chat")).ToList();
            _visionModels = _allModels.Where(m => Cap(m, "vision")).ToList();
            _audioModels = _allModels.Where(m => Cap(m, "audio")).ToList();
            _voiceModels = _allModels.Where(m => Cap(m, "voice")).ToList();
            // Review: only models the catalog marks SupportsVideoReview, else chat/vision from catalog.
            _videoReviewModels = _allModels.Where(m => m.SupportsVideoReview).ToList();
            if (_videoReviewModels.Count == 0)
                _videoReviewModels = _allModels.Where(m => Cap(m, "chat") || Cap(m, "vision")).ToList();

            // Optional "off" rows are UI state, not providers — not catalog models.
            if (!_audioModels.Any(m => m.Id == "none"))
                _audioModels.Insert(0, new SupportedModelDto { Id = "none", DisplayName = "None / Disabled (No Background Music)", Provider = "None", ProviderId = "none" });
            _voiceModels = _voiceModels
                .OrderBy(m => string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.IsVoiceCloneStep ? 0 : 1)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!_voiceModels.Any(m => m.Id == "none"))
                _voiceModels.Insert(0, new SupportedModelDto { Id = "none", DisplayName = "None / Disabled (no voice clone)", Provider = "None", ProviderId = "none" });

            ApplyCatalogDefaultsIfEmpty();
        }
        catch (Exception ex)
        {
            _error = $"Models catalog unavailable: {ex.Message}";
        }
    }

    /// <summary>If project has not chosen models yet, use capability defaultModelId from the catalog only.</summary>
    internal void ApplyCatalogDefaultsIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(_modelName))
            _modelName = DefaultForCapability("video");
        if (string.IsNullOrWhiteSpace(_imageModel))
            _imageModel = DefaultForCapability("image");
        if (string.IsNullOrWhiteSpace(_planningModel))
            _planningModel = DefaultForCapability("chat");
        if (string.IsNullOrWhiteSpace(_visionModel))
            _visionModel = DefaultForCapability("vision");
        if (string.IsNullOrWhiteSpace(_qualityModel))
            _qualityModel = DefaultQualityModel();
        if (string.IsNullOrWhiteSpace(_audioModel))
            _audioModel = "none";
        if (string.IsNullOrWhiteSpace(_voiceModel))
            _voiceModel = "none";
    }

    internal static string DefaultForCapability(string capabilityId) =>
        SupportedModelCatalog.DefaultModelIdForCapability(capabilityId) ?? "";

    internal static string DefaultQualityModel() =>
        SupportedModelCatalog.DefaultModelIdForCapability("video-review")
        ?? SupportedModelCatalog.DefaultModelIdForCapability("chat")
        ?? "";


    internal void ParseFocusFromUri()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (q.TryGetValue("focus", out var f) && !string.IsNullOrWhiteSpace(f))
                _focusCapability = NormalizeFocus(f.ToString());
        }
        catch { _focusCapability = null; }
    }

    internal static string? NormalizeFocus(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "music" or "audio" or "bgm" => "music",
            "voice" or "voice_clone" or "clone" or "tts" => "voice",
            "review" or "qa" or "video_review" or "quality" => "review",
            "video" or "film" => "video",
            "image" or "portrait" or "characters" => "image",
            "planning" or "script" or "chat" or "screenplay" => "planning",
            "vision" or "ocr" => "vision",
            _ => string.IsNullOrWhiteSpace(s) ? null : s,
        };
    }

    internal bool FocusActive => !string.IsNullOrWhiteSpace(_focusCapability);





    internal UserSettingsDto? _userSettings;
    internal readonly Dictionary<string, string?> _keyInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grok"] = null,
        ["gemini"] = null,
        ["anthropic"] = null,
        ["fal"] = null,
    };
    internal readonly HashSet<string> _visibleKeyProviders = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsKeyVisible(string providerId) => _visibleKeyProviders.Contains(providerId);

    internal void ToggleKeyVisible(string providerId)
    {
        if (!IsKeyVisible(providerId)) _visibleKeyProviders.Add(providerId);
        else _visibleKeyProviders.Remove(providerId);
    }

    internal bool _apiKeySaving;
    internal string? _savingProviderId;
    internal string? _apiKeyFeedback;
    /// <summary>Coverage row open for just-in-time key entry.</summary>
    internal string? _coverageEditId;
    internal string? _coverageKeyProviderId;
    /// <summary>replace | add-key | add-provider</summary>
    internal string _coverageKeyMode = "add-provider";

    internal sealed class CoverageRow
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Hint { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string ProviderDisplay { get; set; } = "";
        public bool Required { get; set; } = true;
        public bool OptionalOff { get; set; }
        public bool KeyReady { get; set; }
    }

    internal IReadOnlyList<CoverageRow> BuildCoverageRows()
    {
        var rows = new List<CoverageRow>
        {
            MakeCoverage("video", "Video generation", "Clips / film", _modelName, "video", required: true),
            MakeCoverage("image", "Character portraits", "Image gen", _imageModel, "image", required: true),
            MakeCoverage("planning", "Script & planning", "Screenplay, cast, shot plan", _planningModel, "chat", required: true),
            MakeCoverage("vision", "Image vision / OCR", "Book pages & image understanding", _visionModel, "vision", required: true),
            MakeCoverage("review", "Video review (QA)", "Dialogue check & auto-review", _qualityModel, "chat", required: true, preferVideoReview: true),
            MakeCoverage("music", "Background music", "Optional scores", _audioModel, "audio", required: false),
            MakeCoverage("voice", "Voice clone & speech", "Clones your voice and speaks the dialogue (text-to-speech)", _voiceModel, "voice", required: false),
        };
        return rows;
    }

    internal CoverageRow MakeCoverage(
        string id,
        string label,
        string hint,
        string modelId,
        string capability,
        bool required,
        bool preferVideoReview = false,
        string? forceProviderId = null)
    {
        // Optional stage turned off (music "none", etc.).
        var off = string.IsNullOrWhiteSpace(modelId)
                  || modelId.Equals("none", StringComparison.OrdinalIgnoreCase)
                  || modelId.Equals("disabled", StringComparison.OrdinalIgnoreCase);


        string providerId;
        if (!string.IsNullOrWhiteSpace(forceProviderId))
            providerId = SupportedModelCatalog.NormalizeProviderId(forceProviderId);
        else if (off)
            providerId = "";
        else
            providerId = ResolveProviderIdForModel(modelId, capability, preferVideoReview);

        var providerRow = ProviderRows.FirstOrDefault(pr =>
            string.Equals(
                SupportedModelCatalog.NormalizeProviderId(pr.ProviderId),
                providerId,
                StringComparison.OrdinalIgnoreCase));
        var display = string.IsNullOrWhiteSpace(providerId) ? "" : FriendlyProviderLabel(providerId);
        var keyReady = off || string.IsNullOrWhiteSpace(providerId) || (providerRow?.IsConfigured ?? false);

        return new CoverageRow
        {
            Id = id,
            Label = label,
            Hint = hint,
            ModelId = off ? "Off" : modelId,
            ProviderId = providerId,
            ProviderDisplay = display,
            Required = required,
            OptionalOff = !required && off,
            // Optional + off counts as "ready" for the checklist (nothing missing).
            KeyReady = keyReady || (!required && off),
        };
    }

    internal string ResolveProviderIdForModel(string modelId, string capability, bool preferVideoReview)
    {
        if (string.IsNullOrWhiteSpace(modelId)
            || modelId.Equals("none", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            return "";

        // Only models that exist in the loaded catalog (or catalog static after hydrate).
        var m = _allModels.FirstOrDefault(x =>
            string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase) &&
            (preferVideoReview
                ? (x.SupportsVideoReview || string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase))
                : string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase)))
            ?? _allModels.FirstOrDefault(x => string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));

        if (m is not null)
            return ModelProviderId(m);

        // Server-side catalog (if WASM hydrated SupportedModelCatalog).
        var cap = capability.ToLowerInvariant() switch
        {
            "video" => ModelCapability.Video,
            "image" => ModelCapability.Image,
            "vision" => ModelCapability.Vision,
            "audio" => ModelCapability.Audio,
            "voice" => ModelCapability.Voice,
            _ => ModelCapability.Chat,
        };
        var entry = SupportedModelCatalog.Find(modelId, cap) ?? SupportedModelCatalog.Find(modelId);
        if (entry is null || !entry.Enabled)
            return ""; // not in catalog → not real
        return SupportedModelCatalog.NormalizeProviderId(entry.ProviderId);
    }

    internal List<string> CoverageUsesForProvider(string providerId)
    {
        return BuildCoverageRows()
            .Where(c => !c.OptionalOff
                        && !string.IsNullOrWhiteSpace(c.ProviderId)
                        && string.Equals(c.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Label)
            .Distinct()
            .ToList();
    }


    internal void BeginAddKey(string coverageId, string? preferredProviderId = null)
    {
        // Legacy entry — treat as add-provider unless a specific provider is required.
        if (!string.IsNullOrWhiteSpace(preferredProviderId))
            BeginAddKeyForProvider(coverageId, preferredProviderId);
        else
            BeginAddProvider(coverageId);
    }

    /// <summary>Replace key for one provider only (no multi-provider grid).</summary>
    internal void BeginReplaceKey(string coverageId, string providerId)
    {
        _coverageEditId = coverageId;
        _coverageKeyProviderId = SupportedModelCatalog.NormalizeProviderId(providerId);
        _coverageKeyMode = "replace";
        _apiKeyFeedback = null;
        _keyInputs[_coverageKeyProviderId] = null;
    }

    /// <summary>Add/paste key for the provider currently on this coverage row only.</summary>
    internal void BeginAddKeyForProvider(string coverageId, string providerId)
    {
        _coverageEditId = coverageId;
        _coverageKeyProviderId = SupportedModelCatalog.NormalizeProviderId(providerId);
        _coverageKeyMode = "add-key";
        _apiKeyFeedback = null;
        _keyInputs[_coverageKeyProviderId] = null;
    }

    /// <summary>Choose among all providers that can run this job.</summary>
    internal void BeginAddProvider(string coverageId)
    {
        _coverageEditId = coverageId;
        _coverageKeyMode = "add-provider";
        _apiKeyFeedback = null;
        var list = ProvidersForCoverage(coverageId).ToList();
        // Prefer first without a key so “Add provider” lands on something useful.
        _coverageKeyProviderId = list.FirstOrDefault(p => !p.IsConfigured)?.ProviderId
                                 ?? list.FirstOrDefault()?.ProviderId;
    }

    internal void CancelAddKey()
    {
        _coverageEditId = null;
        _coverageKeyProviderId = null;
        _coverageKeyMode = "add-provider";
    }

    /// <summary>
    /// Providers shown in the open key panel.
    /// Replace / Add key → only the target provider. Add provider → full list for the job.
    /// </summary>
    internal IEnumerable<ProviderKeyStatusDto> ProvidersForKeyPanel(string coverageId)
    {
        var all = ProvidersForCoverage(coverageId);
        if (_coverageKeyMode is "replace" or "add-key"
            && !string.IsNullOrWhiteSpace(_coverageKeyProviderId))
        {
            var only = all.Where(p =>
                string.Equals(p.ProviderId, _coverageKeyProviderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (only.Count > 0)
                return only;
            // Provider might not be in job list yet — synthesize a row from ProviderRows.
            var row = ProviderRows.FirstOrDefault(p =>
                string.Equals(p.ProviderId, _coverageKeyProviderId, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
                return new[] { row };
        }
        return all;
    }

    internal void SelectKeyProvider(string providerId) => _coverageKeyProviderId = providerId;

    /// <summary>
    /// Click on a provider card. If a personal key is already saved, use it and align the model.
    /// Otherwise open the paste form for that provider only.
    /// </summary>
    internal async Task ChooseProviderForCoverageAsync(string providerId, string coverageId)
    {
        var row = ProviderRows.FirstOrDefault(pr =>
            string.Equals(pr.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (row?.IsConfigured == true)
        {
            await UseSavedProviderForCoverageAsync(providerId, coverageId);
            return;
        }
        SelectKeyProvider(providerId);
    }

    /// <summary>
    /// User already saved this provider key — point the coverage model at it and close the panel.
    /// Fixes: model is aimusicapi-suno but Suno (sunoapi) key exists → switch to suno-v5-5.
    /// </summary>
    internal async Task UseSavedProviderForCoverageAsync(string providerId, string coverageId)
    {
        _error = null;
        _apiKeyFeedback = null;
        var label = FriendlyProviderLabel(providerId);

        if (string.Equals(coverageId, "music", StringComparison.OrdinalIgnoreCase))
            EnsureMusicModelForProvider(providerId);
        else if (string.Equals(coverageId, "voice", StringComparison.OrdinalIgnoreCase))
            EnsureVoiceModelForProvider(providerId);
        else
        {
            // Required stages (video, image, …): switch selected model to one from this provider if possible.
            AlignCoverageModelToProvider(coverageId, providerId);
        }

        if (!string.IsNullOrWhiteSpace(_projectId) && _cfg is not null)
        {
            try { await PersistProjectConfigAsync(); }
            catch (Exception ex) { _error = ex.Message; return; }
        }

        var ready = BuildCoverageRows().FirstOrDefault(c =>
            string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
        if (ready?.KeyReady == true)
        {
            _apiKeyFeedback = $"{label}: using your saved key · model set to match.";
            _message = _apiKeyFeedback;
            CancelAddKey();
        }
        else
        {
            // Still not ready — open paste (key missing or model couldn't align).
            SelectKeyProvider(providerId);
            _apiKeyFeedback =
                $"{label}: key is on file, but no catalog model was selected for this job. " +
                "Pick a provider/model in the dropdowns above (music & voice start as Off until you choose one).";
        }
    }

    internal void AlignCoverageModelToProvider(string coverageId, string providerId)
    {
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        var models = ModelsForCoverage(coverageId);
        static string PidOf(SupportedModelDto m) =>
            !string.IsNullOrWhiteSpace(m.ProviderId)
                ? SupportedModelCatalog.NormalizeProviderId(m.ProviderId)
                : SupportedModelCatalog.NormalizeProviderId(m.Provider);

        var pick = models.FirstOrDefault(m =>
            !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
            && string.Equals(PidOf(m), pid, StringComparison.OrdinalIgnoreCase));
        if (pick is not null)
            SetCoverageModelId(coverageId, pick.Id);
    }

    internal async Task SaveCoverageKeyAsync(string providerId, string coverageId)
    {
        await SaveProviderKeyAsync(providerId);
        var row = BuildCoverageRows().FirstOrDefault(c => string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
        if (row?.KeyReady == true)
            CancelAddKey();
    }

    internal string GetCoverageModelId(string coverageId) => coverageId switch
    {
        "video" => _modelName,
        "image" => _imageModel,
        "planning" => _planningModel,
        "vision" => _visionModel,
        "review" => _qualityModel,
        "music" => _audioModel,
        "voice" => _voiceModel,
        _ => "",
    };

    internal void SetCoverageModelId(string coverageId, string modelId)
    {
        switch (coverageId)
        {
            case "video": _modelName = modelId; break;
            case "image": _imageModel = modelId; break;
            case "planning": _planningModel = modelId; break;
            case "vision": _visionModel = modelId; break;
            case "review": _qualityModel = modelId; break;
            case "music": _audioModel = modelId; break;
            case "voice": _voiceModel = modelId; break;
        }
    }

    internal IReadOnlyList<SupportedModelDto> ModelsForCoverage(string coverageId) => coverageId switch
    {
        "video" => _videoModels,
        "image" => _imageModels,
        "planning" => _planningModels,
        "vision" => _visionModels,
        "review" => _videoReviewModels,
        "music" => _audioModels,
        "voice" => _voiceModels,
        _ => Array.Empty<SupportedModelDto>(),
    };

    internal static string ModelProviderId(SupportedModelDto m)
    {
        // Catalog fields only (providerId / provider). Never guess from model id.
        if (!string.IsNullOrWhiteSpace(m.ProviderId))
            return SupportedModelCatalog.NormalizeProviderId(m.ProviderId);
        if (!string.IsNullOrWhiteSpace(m.Provider))
            return SupportedModelCatalog.NormalizeProviderId(m.Provider);
        return "";
    }

    /// <summary>Models for a coverage job limited to one provider (second-stage dropdown).</summary>
    internal IReadOnlyList<SupportedModelDto> ModelsForCoverageProvider(string coverageId, string? providerId)
    {
        var all = ModelsForCoverage(coverageId);
        if (string.IsNullOrWhiteSpace(providerId))
            return Array.Empty<SupportedModelDto>();
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        var list = all
            .Where(m => !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase))
            .Where(m => string.Equals(ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.IsVoiceCloneStep ? 0 : 1)
            .ThenBy(m => ModelOptionLabel(m), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list;
    }

    internal async Task OnCoverageProviderChangedAsync(string coverageId, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        var models = ModelsForCoverageProvider(coverageId, pid);
        var current = GetCoverageModelId(coverageId);
        var stillValid = models.Any(m => string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase));
        if (!stillValid)
        {
            // Prefer clone-step for voice; otherwise first model for this provider.
            var pick = models.FirstOrDefault(m => m.IsVoiceCloneStep) ?? models.FirstOrDefault();
            if (pick is not null)
                SetCoverageModelId(coverageId, pick.Id);
            else if (coverageId is "music" or "voice")
                SetCoverageModelId(coverageId, "none");
        }

        _coverageEditId = null;
        _message = null;
        if (!string.IsNullOrEmpty(_projectId) && _cfg is not null)
        {
            try
            {
                await SaveAsync();
                _message = $"Provider set to {FriendlyProviderLabel(pid)}.";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }

        var row = BuildCoverageRows().FirstOrDefault(c =>
            string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
        if (row is { KeyReady: false, OptionalOff: false } && !string.IsNullOrWhiteSpace(row.ProviderId))
            BeginAddKey(coverageId, row.ProviderId);
    }

    internal IEnumerable<ProviderKeyStatusDto> ProvidersForCoverage(string coverageId)
    {
        var models = ModelsForCoverage(coverageId);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            if (string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)) continue;
            var pid = ModelProviderId(m);
            if (!string.IsNullOrWhiteSpace(pid) && !pid.Equals("none", StringComparison.OrdinalIgnoreCase))
                ids.Add(pid);
        }

        if (ids.Count == 0)
        {
            foreach (var pr in ProviderRows)
            {
                var ok = coverageId switch
                {
                    "video" => pr.SupportsVideoGen || pr.SupportsVideo,
                    "image" => pr.SupportsImageGen || pr.SupportsImage,
                    "planning" => pr.SupportsScriptPlanning || pr.SupportsChat,
                    "vision" => pr.SupportsImageVision || pr.SupportsVision,
                    "review" => pr.SupportsVideoReview || pr.SupportsChat,
                    "music" => string.Equals(pr.ProviderId, "fal", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(pr.ProviderId, "suno", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(pr.ProviderId, "aimusicapi", StringComparison.OrdinalIgnoreCase),
                    "voice" => string.Equals(pr.ProviderId, "elevenlabs", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(pr.ProviderId, "fal", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                if (ok) ids.Add(pr.ProviderId);
            }
        }

        // Include any provider id we discovered from models even if ProviderRows lacks it (e.g. OpenAI).
        var rows = ProviderRows
            .Select(pr => new ProviderKeyStatusDto
            {
                ProviderId = SupportedModelCatalog.NormalizeProviderId(pr.ProviderId),
                DisplayName = pr.DisplayName,
                Family = pr.Family,
                HasPersonalKey = pr.HasPersonalKey,
                MaskedPersonalKey = pr.MaskedPersonalKey,
                HasServerKey = pr.HasServerKey,
                ActiveSource = pr.ActiveSource,
                CapabilitiesSummary = pr.CapabilitiesSummary,
                SupportsVideo = pr.SupportsVideo,
                SupportsImage = pr.SupportsImage,
                SupportsChat = pr.SupportsChat,
                SupportsVision = pr.SupportsVision,
                SupportsVideoGen = pr.SupportsVideoGen,
                SupportsVideoReview = pr.SupportsVideoReview,
                SupportsImageGen = pr.SupportsImageGen,
                SupportsScriptPlanning = pr.SupportsScriptPlanning,
                SupportsImageVision = pr.SupportsImageVision,
                RequiredEnvKeys = pr.RequiredEnvKeys,
                Notes = pr.Notes,
            })
            .Where(pr => ids.Contains(pr.ProviderId))
            .GroupBy(pr => pr.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var id in ids)
        {
            if (rows.Any(r => string.Equals(r.ProviderId, id, StringComparison.OrdinalIgnoreCase)))
                continue;
            rows.Add(new ProviderKeyStatusDto
            {
                ProviderId = id,
                DisplayName = FriendlyProviderLabel(id),
                ActiveSource = "none",
                CapabilitiesSummary = "—",
                RequiredEnvKeys = new List<string>(),
            });
        }

        // Selected provider (from model) first so the <select> cannot fall back to a random first option.
        var currentModelId = GetCoverageModelId(coverageId);
        var currentPid = "";
        if (!string.IsNullOrWhiteSpace(currentModelId)
            && !string.Equals(currentModelId, "none", StringComparison.OrdinalIgnoreCase))
        {
            var cap = coverageId switch
            {
                "video" => "video",
                "image" => "image",
                "planning" => "chat",
                "vision" => "vision",
                "review" => "chat",
                "music" => "audio",
                "voice" => "voice",
                _ => "chat",
            };
            currentPid = ResolveProviderIdForModel(currentModelId, cap, preferVideoReview: coverageId == "review");
            if (!string.IsNullOrWhiteSpace(currentPid)
                && !rows.Any(r => string.Equals(r.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Insert(0, new ProviderKeyStatusDto
                {
                    ProviderId = currentPid,
                    DisplayName = FriendlyProviderLabel(currentPid),
                    ActiveSource = "none",
                    CapabilitiesSummary = "—",
                    RequiredEnvKeys = new List<string>(),
                });
            }
        }

        return rows
            .OrderBy(pr => string.Equals(pr.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(pr => pr.IsConfigured ? 0 : 1)
            .ThenBy(pr => FriendlyProviderLabel(pr.ProviderId));
    }

    internal async Task OnCoverageModelChangedAsync(string coverageId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        SetCoverageModelId(coverageId, modelId);
        _coverageEditId = null;
        _message = null;
        if (!string.IsNullOrEmpty(_projectId) && _cfg is not null)
        {
            try
            {
                await SaveAsync();
                _message = "Model saved.";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }
        var row = BuildCoverageRows().FirstOrDefault(c => string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
        if (row is { KeyReady: false, OptionalOff: false } && !string.IsNullOrWhiteSpace(row.ProviderId))
            BeginAddKey(coverageId, row.ProviderId);
    }

    internal async Task TurnOffOptionalAsync(string coverageId)
    {
        if (coverageId == "music")
        {
            _audioModel = "none";
            _enableBackgroundMusic = false;
        }
        else if (coverageId == "voice")
        {
            _voiceModel = "none";
        }
        CancelAddKey();
        if (!string.IsNullOrEmpty(_projectId) && _cfg is not null)
        {
            try { await SaveAsync(); }
            catch (Exception ex) { _error = ex.Message; }
        }
    }

    internal IReadOnlyList<ProviderKeyStatusDto> ProviderRows
    {
        get
        {
            // Prefer server DTO (includes personal/server key status). Fall back to catalog so
            // Settings always shows key slots even if the settings API fails or is offline.
            if (_userSettings?.Providers is { Count: > 0 } list)
                return list;
            try { return SupportedModelCatalog.BuildProviderKeyRows(); }
            catch { return Array.Empty<ProviderKeyStatusDto>(); }
        }
    }

    internal static string ProviderPlaceholder(string providerId) => providerId.ToLowerInvariant() switch
    {
        "grok" => "xai-... (XAI_API_KEY)",
        "gemini" => "AIza... (GEMINI_API_KEY)",
        "fal" => "key... (FAL_KEY)",
        "anthropic" => "sk-ant-... (ANTHROPIC_API_KEY)",
        "openai" => "sk-... (OPENAI_API_KEY)",
        "suno" => "SUNO_API_KEY",
        "aimusicapi" => "AIMUSICAPI_API_KEY",
        "elevenlabs" => "Paste your voice API key",
        _ => "api key",
    };

    internal string? GetKeyInput(string providerId) =>
        _keyInputs.TryGetValue(providerId, out var v) ? v : null;

    internal void SetKeyInput(string providerId, string? value) =>
        _keyInputs[providerId] = value;

    /// <summary>Provider UI label — catalog providers[] only.</summary>
    internal static string FriendlyProviderLabel(string? providerId) =>
        SupportedModelCatalog.ProviderLabelFor(providerId);

    /// <summary>Model product name from catalog displayName only.</summary>
    internal static string ModelOptionLabel(SupportedModelDto m)
    {
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName.Trim();
        return m.LabMode ? $"{name} [LAB]" : name;
    }


    internal string VendorLabel(string modelId, string capability)
    {
        var m = _allModels.FirstOrDefault(x =>
            string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase))
            ?? _allModels.FirstOrDefault(x =>
                string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (m is null)
        {
            var cap = capability.Equals("video", StringComparison.OrdinalIgnoreCase)
                ? ModelCapability.Video
                : capability.Equals("image", StringComparison.OrdinalIgnoreCase)
                    ? ModelCapability.Image
                    : ModelCapability.Chat;
            return SupportedModelCatalog.ProviderIdFor(modelId, cap);
        }
        return string.IsNullOrWhiteSpace(m.ProviderId) ? m.Provider : m.ProviderId!;
    }

    internal double CatalogVideoRate(string resolution)
    {
        try
        {
            var entry = SupportedModelCatalog.ResolveOrDefault(_modelName, ModelCapability.Video);
            var table = entry.VideoCostPerSecondByResolution;
            if (table is not null && table.TryGetValue(resolution, out var exact))
                return exact;
            // Same nearest-tier fill as CostReportService for missing res (e.g. Veo has no 480p).
            if (table is { Count: > 0 })
            {
                foreach (var prefer in new[] { "720p", "1080p", "480p" })
                {
                    if (table.TryGetValue(prefer, out var v))
                        return v;
                }
                return table.Values.Min();
            }
        }
        catch (Exception)
        {
            // Catalog not loaded (rare after embedded fallback) — use list defaults.
        }
        return 0;
    }

    internal double CatalogImageRate()
    {
        try
        {
            var entry = SupportedModelCatalog.Find(_imageModel, ModelCapability.Image)
                        ?? SupportedModelCatalog.ResolveOrDefault(_imageModel, ModelCapability.Image);
            return entry.ImageCostPerImage ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static string UsdRate(double v) =>
        v < 0.01 ? $"${v:0.####}" : $"${v:0.##}";

    internal async Task SaveProviderKeyAsync(string providerId)
    {
        var value = GetKeyInput(providerId);
        if (string.IsNullOrWhiteSpace(value)) return;
        await PersistProviderKeyAsync(providerId, value.Trim(), clearing: false);
    }

    internal async Task ClearProviderKeyAsync(string providerId) =>
        await PersistProviderKeyAsync(providerId, "", clearing: true);

    internal async Task PersistProviderKeyAsync(string providerId, string keyValue, bool clearing)
    {
        try
        {
            _busy = true;
            _apiKeySaving = true;
            _savingProviderId = providerId;
            _apiKeyFeedback = null;
            _error = null;

            _userSettings = await Engine.UpdateUserSettingsAsync(
                new Dictionary<string, string?> { [providerId] = keyValue });

            _keyInputs[providerId] = null;

            var label = ProviderRows.FirstOrDefault(p =>
                string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? providerId;

            if (clearing)
            {
                _apiKeyFeedback = $"{label}: personal key removed.";
            }
            else
            {
                _apiKeyFeedback = $"{label}: personal key encrypted and saved.";
                // Fill empty required slots with this provider's first catalog models (if any).
                if (!string.IsNullOrWhiteSpace(_projectId) && _cfg is not null)
                {
                    ApplyProviderModelDefaults(providerId);
                    try { await PersistProjectConfigAsync(); } catch { /* optional */ }
                }
                // Music key: if coverage model is none/unknown, pick a model for this provider so Ready lights up.
                if (providerId is "fal" or "suno" or "aimusicapi")
                {
                    EnsureMusicModelForProvider(providerId);
                }
                // Voice key (ElevenLabs) or Fal while adding voice coverage: pick clone-step model, not TTS-only.
                if (providerId is "elevenlabs"
                    || (providerId is "fal" && string.Equals(_coverageEditId, "voice", StringComparison.OrdinalIgnoreCase)))
                {
                    EnsureVoiceModelForProvider(providerId);
                }
                if ((providerId is "fal" or "suno" or "aimusicapi" or "elevenlabs")
                    && !string.IsNullOrWhiteSpace(_projectId) && _cfg is not null)
                {
                    try { await PersistProjectConfigAsync(); } catch { /* optional */ }
                }
            }

            _message = _apiKeyFeedback;
            await ActiveProject.RefreshReadinessAsync(Engine);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
            _apiKeySaving = false;
            _savingProviderId = null;
        }
    }



    /// <summary>After saving a voice-provider key, pick a clone-step model from the catalog for that provider.</summary>
    internal void EnsureVoiceModelForProvider(string providerId)
    {
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        var currentProvider = ResolveProviderIdForModel(_voiceModel, "voice", preferVideoReview: false);
        var off = string.IsNullOrWhiteSpace(_voiceModel)
                  || _voiceModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                  || _voiceModel.Equals("disabled", StringComparison.OrdinalIgnoreCase);

        if (!off && string.Equals(currentProvider, pid, StringComparison.OrdinalIgnoreCase))
        {
            var cur = _voiceModels.FirstOrDefault(m => string.Equals(m.Id, _voiceModel, StringComparison.OrdinalIgnoreCase));
            if (cur is { IsVoiceCloneStep: true })
                return;
            // Prefer clone-step over TTS-only when switching within same provider.
        }

        var pick = _voiceModels.FirstOrDefault(m =>
            !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
            && m.IsVoiceCloneStep
            && string.Equals(ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase))
            ?? _voiceModels.FirstOrDefault(m =>
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));

        if (pick is null)
        {
            try
            {
                var fromCatalog = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                    .Where(e =>
                        e.Enabled &&
                        string.Equals(
                            SupportedModelCatalog.NormalizeProviderId(e.ProviderId),
                            pid,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.IsVoiceCloneStep)
                    .FirstOrDefault();
                if (fromCatalog is not null)
                {
                    pick = SupportedModelCatalog.ToDto(fromCatalog);
                    if (!_voiceModels.Any(m => string.Equals(m.Id, pick.Id, StringComparison.OrdinalIgnoreCase)))
                        _voiceModels.Add(pick);
                }
            }
            catch { /* ignore */ }
        }

        if (pick is not null)
            _voiceModel = pick.Id;
        // else leave as-is — no invented model id
    }

    /// <summary>After saving a music-provider key, pick a matching music model from the catalog.</summary>
    internal void EnsureMusicModelForProvider(string providerId)
    {
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        var currentProvider = ResolveProviderIdForModel(_audioModel, "audio", preferVideoReview: false);
        var off = string.IsNullOrWhiteSpace(_audioModel)
                  || _audioModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                  || _audioModel.Equals("disabled", StringComparison.OrdinalIgnoreCase);
        if (!off && string.Equals(currentProvider, pid, StringComparison.OrdinalIgnoreCase))
            return;

        var pick = _audioModels.FirstOrDefault(m =>
            !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));
        if (pick is null)
        {
            // Last resort: catalog static (provider must still exist in models_catalog.json).
            try
            {
                var fromCatalog = SupportedModelCatalog.ForCapability(ModelCapability.Audio)
                    .FirstOrDefault(e =>
                        e.Enabled &&
                        string.Equals(
                            SupportedModelCatalog.NormalizeProviderId(e.ProviderId),
                            pid,
                            StringComparison.OrdinalIgnoreCase));
                if (fromCatalog is not null)
                {
                    pick = SupportedModelCatalog.ToDto(fromCatalog);
                    if (!_audioModels.Any(m => string.Equals(m.Id, pick.Id, StringComparison.OrdinalIgnoreCase)))
                        _audioModels.Add(pick);
                }
            }
            catch { /* ignore */ }
        }
        if (pick is not null)
        {
            _audioModel = pick.Id;
            _enableBackgroundMusic = true;
        }
    }

    /// <summary>After attaching a provider key, set empty required slots to that provider's first catalog model.</summary>
    internal void ApplyProviderModelDefaults(string providerId)
    {
        var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
        void Prefer(ref string slot, IReadOnlyList<SupportedModelDto> models)
        {
            if (!string.IsNullOrWhiteSpace(slot) && !slot.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;
            var hit = models.FirstOrDefault(m =>
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) slot = hit.Id;
        }
        Prefer(ref _modelName, _videoModels);
        Prefer(ref _imageModel, _imageModels);
        Prefer(ref _planningModel, _planningModels);
        Prefer(ref _visionModel, _visionModels);
        Prefer(ref _qualityModel, _videoReviewModels);
    }

    internal async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            try { _userSettings = await Engine.GetUserSettingsAsync(); } catch { }

            if (string.IsNullOrWhiteSpace(_projectId))
            {
                _cfg = null;
                return;
            }

            var dto = await Engine.GetConfigAsync(_projectId);
            _cfg = dto?.Config;
            _projectDir = dto?.ProjectDir ?? $"projects/{_projectId}";
            if (_cfg is null) return;
            _uiTheme = ThemeState.Normalize(GetStr("ui_theme", _uiTheme));
            _preferredVideoEditor = GetStr("preferred_video_editor", "ClipChamp");
            _blueprintFile = GetStr("blueprint_file", _blueprintFile);
            _modelName = GetStr("model_name", _modelName);
            _imageModel = GetStr("image_model_name", _imageModel);
            _planningModel = GetStr("planning_model_name", _planningModel);
            _visionModel = GetStr("vision_model_name", _visionModel);
            _qualityModel = GetStr("quality_model_name", _qualityModel);
            _audioModel = GetStr("audio_model_name", _audioModel);
            _voiceModel = GetStr("voice_model_name", _voiceModel);
            // Drop ids that are not in the catalog (stale project config).
            if (!string.IsNullOrWhiteSpace(_modelName) && !_videoModels.Any(m => string.Equals(m.Id, _modelName, StringComparison.OrdinalIgnoreCase)))
                _modelName = DefaultForCapability("video");
            if (!string.IsNullOrWhiteSpace(_imageModel) && !_imageModels.Any(m => string.Equals(m.Id, _imageModel, StringComparison.OrdinalIgnoreCase)))
                _imageModel = DefaultForCapability("image");
            if (!string.IsNullOrWhiteSpace(_planningModel) && !_planningModels.Any(m => string.Equals(m.Id, _planningModel, StringComparison.OrdinalIgnoreCase)))
                _planningModel = DefaultForCapability("chat");
            if (!string.IsNullOrWhiteSpace(_visionModel) && !_visionModels.Any(m => string.Equals(m.Id, _visionModel, StringComparison.OrdinalIgnoreCase)))
                _visionModel = DefaultForCapability("vision");
            if (!string.IsNullOrWhiteSpace(_qualityModel) && !_videoReviewModels.Any(m => string.Equals(m.Id, _qualityModel, StringComparison.OrdinalIgnoreCase)))
                _qualityModel = DefaultQualityModel();
            if (!string.IsNullOrWhiteSpace(_audioModel)
                && !_audioModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !_audioModels.Any(m => string.Equals(m.Id, _audioModel, StringComparison.OrdinalIgnoreCase)))
                _audioModel = "none";
            if (!string.IsNullOrWhiteSpace(_voiceModel)
                && !_voiceModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !_voiceModels.Any(m => string.Equals(m.Id, _voiceModel, StringComparison.OrdinalIgnoreCase)))
                _voiceModel = "none";
            _enableBackgroundMusic = GetBool("enable_background_music", _enableBackgroundMusic);
            _backgroundMusicVolumePercent = GetInt("background_music_volume_percent", _backgroundMusicVolumePercent);
            _aspect = GetStr("aspect_ratio", _aspect);
            _resolution = GetStr("resolution", _resolution);
            _durationSeconds = GetInt("duration_seconds", _durationSeconds);
            _useDurationDefaults = GetBool("use_duration_defaults", _useDurationDefaults);
            _smartContinuation = GetBool("smart_continuation", _smartContinuation);
            _mergeAfterClip = GetBool("merge_scene_after_each_clip", _mergeAfterClip);
            _qaRetry = GetBool("qa_retry_on_fail", _qaRetry);
            _regenSilent = GetBool("regenerate_silent_clips", _regenSilent);
            _rebuildWip = GetBool("rebuild_wip_movie_after_scene", _rebuildWip);
            _qaMaxRetries = GetInt("qa_max_retries", _qaMaxRetries);
            _qaFrameCount = GetInt("qa_frame_count", _qaFrameCount);
            _audioGain = GetDouble("composite_audio_gain_db", _audioGain);
            _wipPath = GetStr("wip_movie_path", _wipPath);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _cfg = null;
        }
        finally { _busy = false; }
    }

    internal async Task SaveAsync()
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_projectId))
            {
                _error = "Choose a project first.";
                return;
            }

            await PersistProjectConfigAsync();
            _message = $"Settings saved for {_projectId}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally { _busy = false; }
    }

    /// <summary>Write current form fields to the project config (no busy flag / reload).</summary>
    internal async Task PersistProjectConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId))
            throw new InvalidOperationException("Choose a project first.");

        var videoProvider = SupportedModelCatalog.ProviderIdFor(_modelName, ModelCapability.Video);
        var imageProvider = SupportedModelCatalog.ProviderIdFor(_imageModel, ModelCapability.Image);
        var planningProvider = SupportedModelCatalog.ProviderIdFor(_planningModel, ModelCapability.Chat);
        var visionProvider = SupportedModelCatalog.ProviderIdFor(_visionModel, ModelCapability.Vision);
        var qualityProvider = SupportedModelCatalog.ProviderIdFor(_qualityModel, ModelCapability.Chat);

        var updates = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["ui_theme"] = _uiTheme,
            ["preferred_video_editor"] = _preferredVideoEditor,
            ["blueprint_file"] = _blueprintFile,
            ["video_provider"] = videoProvider,
            ["character_design_provider"] = imageProvider,
            ["image_provider"] = imageProvider,
            ["planning_provider"] = planningProvider,
            ["vision_provider"] = visionProvider,
            ["quality_provider"] = qualityProvider,
            ["model_name"] = _modelName,
            ["image_model_name"] = _imageModel,
            ["planning_model_name"] = _planningModel,
            ["vision_model_name"] = _visionModel,
            ["quality_model_name"] = _qualityModel,
            ["audio_model_name"] = _audioModel,
            ["voice_model_name"] = _voiceModel,
            ["enable_background_music"] = _enableBackgroundMusic,
            ["background_music_volume_percent"] = _backgroundMusicVolumePercent,
            ["model_selections"] = new Dictionary<string, string>
            {
                ["video"] = _modelName,
                ["image"] = _imageModel,
                ["chat"] = _planningModel,
                ["vision"] = _visionModel,
                ["video-review"] = _qualityModel,
                ["audio"] = _audioModel,
            },
            ["aspect_ratio"] = _aspect,
            ["resolution"] = _resolution,
            ["duration_seconds"] = _durationSeconds,
            ["use_duration_defaults"] = _useDurationDefaults,
            ["smart_continuation"] = _smartContinuation,
            ["merge_scene_after_each_clip"] = _mergeAfterClip,
            ["qa_retry_on_fail"] = _qaRetry,
            ["regenerate_silent_clips"] = _regenSilent,
            ["rebuild_wip_movie_after_scene"] = _rebuildWip,
            ["qa_max_retries"] = _qaMaxRetries,
            ["qa_frame_count"] = _qaFrameCount,
            ["composite_audio_gain_db"] = _audioGain,
            ["wip_movie_path"] = _wipPath,
            // Snapshot vendor rates for transparency; runtime CostReportService always re-derives
            // from model_name / image_model_name via SupportedModelCatalog.
            ["cost_estimates"] = BuildVendorCostEstimatesSnapshot(),
        };

        await Engine.SaveConfigAsync(_projectId, updates);
    }

    internal Dictionary<string, object?> BuildVendorCostEstimatesSnapshot()
    {
        var video = SupportedModelCatalog.ResolveOrDefault(_modelName, ModelCapability.Video);
        var image = SupportedModelCatalog.Find(_imageModel, ModelCapability.Image)
                    ?? SupportedModelCatalog.ResolveOrDefault(_imageModel, ModelCapability.Image);
        var table = new Dictionary<string, object?>
        {
            ["480p"] = CatalogVideoRate("480p"),
            ["720p"] = CatalogVideoRate("720p"),
            ["1080p"] = CatalogVideoRate("1080p"),
        };

        // Preserve planning knobs (retries, etc.) if present; drop manual rate tables.
        double assumeRetries = 0;
        bool assumeRef = true;
        if (_cfg is not null &&
            _cfg.TryGetValue("cost_estimates", out var oldCe) &&
            oldCe.ValueKind == JsonValueKind.Object)
        {
            if (oldCe.TryGetProperty("assume_avg_retries", out var ar) && ar.TryGetDouble(out var r))
                assumeRetries = r;
            if (oldCe.TryGetProperty("assume_ref_image_per_clip", out var rf) &&
                rf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                assumeRef = rf.GetBoolean();
        }

        return new Dictionary<string, object?>
        {
            ["currency"] = "USD",
            ["source"] = "model_catalog",
            ["video_model"] = video.Id,
            ["video_provider"] = video.ProviderId,
            ["image_model"] = image.Id,
            ["image_provider"] = image.ProviderId,
            ["video_output_per_sec"] = table,
            ["image_output_quality"] = image.ImageCostPerImage ?? 0,
            ["assume_avg_retries"] = assumeRetries,
            ["assume_ref_image_per_clip"] = assumeRef,
            ["notes"] =
                "List rates from SupportedModelCatalog for the selected models. Not invoices. " +
                "Runtime always re-reads catalog from model_name / image_model_name.",
        };
    }

    /// <summary>Live-apply the theme pick to this browser tab so it's visible before Save.
    /// Only touches the DOM when editing the currently-active project — previewing a theme
    /// for some other project you happen to have selected here shouldn't repaint the whole app.</summary>
    internal async Task PreviewThemeAsync()
    {
        if (!string.Equals(_projectId, ActiveProject.ProjectId, StringComparison.OrdinalIgnoreCase))
            return;
        Theme.Set(_uiTheme);
        try { await Js.InvokeVoidAsync("fsTheme.apply", _uiTheme); }
        catch { /* ignore */ }
    }

    internal string GetStr(string key, string fallback) =>
        _cfg is not null && _cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    internal int GetInt(string key, int fallback) =>
        _cfg is not null && _cfg.TryGetValue(key, out var el) && el.TryGetInt32(out var v)
            ? v
            : fallback;

    internal double GetDouble(string key, double fallback) =>
        _cfg is not null && _cfg.TryGetValue(key, out var el) && el.TryGetDouble(out var v)
            ? v
            : fallback;

    internal bool GetBool(string key, bool fallback) =>
        _cfg is not null && _cfg.TryGetValue(key, out var el) &&
        (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;

    protected override void OnInitialized()
    {
        MediaFolder.Changed += OnMediaFolderChanged;
    }

    internal void OnMediaFolderChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    internal async Task OnThemeChangedAsync()
    {
        await PreviewThemeAsync();
        await ScheduleAutoSaveAsync();
    }

    /// <summary>Debounced project-settings save — no bottom Save button.</summary>
    internal async Task ScheduleAutoSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _cfg is null)
            return;

        _dirty = true;
        var epoch = ++_autoSaveEpoch;
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;

        _saveStatus = "Saving…";
        try { await InvokeAsync(StateHasChanged); } catch { /* ignore */ }

        try
        {
            await Task.Delay(450, token);
            if (token.IsCancellationRequested || epoch != _autoSaveEpoch)
                return;

            await PersistProjectConfigAsync();
            if (token.IsCancellationRequested || epoch != _autoSaveEpoch)
                return;

            _dirty = false;
            _saveStatus = "Saved";
            try { await InvokeAsync(StateHasChanged); } catch { /* ignore */ }

            // Fade the Saved label after a moment.
            _ = ClearSaveStatusLaterAsync(epoch);
        }
        catch (TaskCanceledException)
        {
            // newer edit superseded this save
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _saveStatus = "Save failed";
            try { await InvokeAsync(StateHasChanged); } catch { /* ignore */ }
        }
    }

    internal async Task ClearSaveStatusLaterAsync(int epoch)
    {
        try
        {
            await Task.Delay(2000);
            if (epoch == _autoSaveEpoch && _saveStatus == "Saved")
            {
                _saveStatus = null;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch { /* ignore */ }
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

    internal async Task ConnectMediaFolderAsync()
    {
        _error = null;
        _message = null;
        try
        {
            var ok = await MediaFolder.ConnectFolderAsync();
            if (!ok)
            {
                _error = MediaFolder.LastStatus
                         ?? "Could not open the folder picker. Use Chrome or Edge and allow folder access.";
                return;
            }
            _message = $"Media folder set to “{MediaFolder.FolderName ?? "selected folder"}”.";
            if (!string.IsNullOrWhiteSpace(_projectId))
                await MediaFolder.SyncProjectMediaToClientAsync(_projectId);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    internal async Task ReconnectMediaFolderAsync()
    {
        _error = null;
        _message = null;
        try
        {
            var ok = await MediaFolder.ReconnectAsync();
            if (!ok)
            {
                _error = MediaFolder.LastStatus
                         ?? "Could not reconnect. Use Select folder to pick again.";
                return;
            }
            _message = $"Reconnected “{MediaFolder.FolderName ?? "folder"}”.";
            if (!string.IsNullOrWhiteSpace(_projectId))
                await MediaFolder.SyncProjectMediaToClientAsync(_projectId);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }
}

