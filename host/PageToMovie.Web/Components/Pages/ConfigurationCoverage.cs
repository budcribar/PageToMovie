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
    /// <summary>Coverage domain for the Configuration page. Owns related UI state and behavior.</summary>
    public sealed class ConfigurationCoverage
    {
        private readonly Configuration S;
        public ConfigurationCoverage(Configuration host) => S = host;

        private const string CapMusic = "music";
        private const string CapAudio = "audio";
        private const string CapVoice = "voice";
        private const string CapReview = "review";
        private const string CapVideo = "video";
        private const string CapVideoEdit = "video-edit";
        private const string CapImage = "image";
        private const string CapPlanning = "planning";
        private const string CapVision = "vision";
        private const string ModelDisabled = "disabled";

        internal string _audioModel = "none";

        internal int _backgroundMusicVolumePercent = 20;

        /// <summary>Coverage row open for just-in-time key entry.</summary>
            internal string? _coverageEditId;

        /// <summary>replace | add-key | add-provider</summary>
            internal string _coverageKeyMode = "add-provider";

        internal string? _coverageKeyProviderId;

        internal bool _enableBackgroundMusic = true;

        /// <summary>Deep-link from gated features: music | voice | review | video | …</summary>
            internal string? _focusCapability;

        internal string _imageModel = "";

        internal string _modelName = "";

        internal string _planningModel = "";

        internal string _qualityModel = "";

        internal string _visionModel = "";

        internal string _videoEditModel = "none";

        internal string _voiceModel = "none";



        internal void ParseFocusFromUri()
        {
            try
            {
                var uri = S.Nav.ToAbsoluteUri(S.Nav.Uri);
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
                CapMusic or CapAudio or "bgm" => CapMusic,
                CapVoice or "voice_clone" or "clone" or "tts" => CapVoice,
                CapReview or "qa" or "video_review" or "quality" => CapReview,
                CapVideoEdit or "edit" or "clip_edit" => CapVideoEdit,
                CapVideo or "film" => CapVideo,
                CapImage or "portrait" or "characters" => CapImage,
                CapPlanning or "script" or "chat" or "screenplay" => CapPlanning,
                CapVision or "ocr" => CapVision,
                _ => string.IsNullOrWhiteSpace(s) ? null : s,
            };
        }


        internal bool FocusActive => !string.IsNullOrWhiteSpace(_focusCapability);


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
                MakeCoverage(CapVideo, "Video generation", "Clips / film", _modelName, CapVideo, required: true),
                MakeCoverage(CapImage, "Character portraits", "Image gen", _imageModel, CapImage, required: true),
                MakeCoverage(CapPlanning, "Script & planning", "Screenplay, cast, shot plan", _planningModel, "chat", required: true),
                MakeCoverage(CapVision, "Image vision / OCR", "Book pages & image understanding", _visionModel, CapVision, required: true),
                // QA only — missing key must not block book→screenplay or film generation.
                MakeCoverage(CapReview, "Video review (QA)", "Optional: dialogue check & auto-review", _qualityModel, "chat", required: false, preferVideoReview: true),
                MakeCoverage(CapMusic, "Background music", "Optional scores", _audioModel, CapAudio, required: false),
                MakeCoverage(CapVoice, "Voice clone & speech", "Clones your voice and speaks the dialogue (text-to-speech)", _voiceModel, CapVoice, required: false),
                MakeCoverage(CapVideoEdit, "Clip editing", "Optional: re-render a clip from a written instruction", _videoEditModel, nameof(ModelCapability.VideoEdit), required: false),
            };
            return rows;
        }

        /// <summary>Keep Studio coverage <details> open across re-renders (add key, save, etc.).</summary>
        internal bool StudioCoverageOpen;

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
                      || modelId.Equals(ModelDisabled, StringComparison.OrdinalIgnoreCase);


            string providerId;
            if (!string.IsNullOrWhiteSpace(forceProviderId))
                providerId = SupportedModelCatalog.NormalizeProviderId(forceProviderId);
            else if (off)
                providerId = "";
            else
                providerId = ResolveProviderIdForModel(modelId, capability, preferVideoReview);

            var providerRow = S.Keys.ProviderRows.FirstOrDefault(pr =>
                string.Equals(
                    SupportedModelCatalog.NormalizeProviderId(pr.ProviderId),
                    providerId,
                    StringComparison.OrdinalIgnoreCase));
            var display = string.IsNullOrWhiteSpace(providerId) ? "" : ConfigurationCatalog.FriendlyProviderLabel(providerId);
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
                || modelId.Equals(ModelDisabled, StringComparison.OrdinalIgnoreCase))
                return "";

            // Only models that exist in the loaded catalog (or catalog static after hydrate).
            var m = S.Catalog._allModels.FirstOrDefault(x =>
                string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase) &&
                (preferVideoReview
                    ? (x.SupportsVideoReview || string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase))
                    : string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase)))
                ?? S.Catalog._allModels.FirstOrDefault(x => string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));

            if (m is not null)
                return ConfigurationCatalog.ModelProviderId(m);

            // Server-side catalog (if WASM hydrated SupportedModelCatalog).
            // Capability ids are the catalog's own ("video", "vision", "video-edit", …) — parse
            // them into the enum instead of maintaining a per-capability ladder here.
            var cap = Enum.TryParse<ModelCapability>(capability.Replace("-", ""), ignoreCase: true, out var parsed)
                ? parsed
                : ModelCapability.Chat;
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


        /// <summary>
        /// Click on a provider card. If a personal key is already saved, use it and align the model.
        /// Otherwise open the paste form for that provider only.
        /// </summary>
        internal async Task ChooseProviderForCoverageAsync(string providerId, string coverageId)
        {
            var row = S.Keys.ProviderRows.FirstOrDefault(pr =>
                string.Equals(pr.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (row?.IsConfigured == true)
            {
                await UseSavedProviderForCoverageAsync(providerId, coverageId);
                return;
            }
            S.Keys.SelectKeyProvider(providerId);
        }


        /// <summary>
        /// User already saved this provider key — point the coverage model at it and close the panel.
        /// Fixes: model is aimusicapi-suno but Suno (sunoapi) key exists → switch to suno-v5-5.
        /// </summary>
        internal async Task UseSavedProviderForCoverageAsync(string providerId, string coverageId)
        {
            S._error = null;
            S.Keys._apiKeyFeedback = null;
            var label = ConfigurationCatalog.FriendlyProviderLabel(providerId);

            if (string.Equals(coverageId, CapMusic, StringComparison.OrdinalIgnoreCase))
                EnsureMusicModelForProvider(providerId);
            else if (string.Equals(coverageId, CapVoice, StringComparison.OrdinalIgnoreCase))
                EnsureVoiceModelForProvider(providerId);
            else
            {
                // Required stages (video, image, …): switch selected model to one from this provider if possible.
                AlignCoverageModelToProvider(coverageId, providerId);
            }

            if (!string.IsNullOrWhiteSpace(S._projectId) && S._cfg is not null)
            {
                try { await S.Form.PersistProjectConfigAsync(); }
                catch (Exception ex) { S._error = ex.Message; return; }
            }

            var ready = BuildCoverageRows().FirstOrDefault(c =>
                string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
            if (ready?.KeyReady == true)
            {
                S.Keys._apiKeyFeedback = $"{label}: using your saved key · model set to match.";
                S._message = S.Keys._apiKeyFeedback;
                S.Keys.CancelAddKey();
            }
            else
            {
                // Still not ready — open paste (key missing or model couldn't align).
                S.Keys.SelectKeyProvider(providerId);
                S.Keys._apiKeyFeedback =
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


        internal string GetCoverageModelId(string coverageId) => coverageId switch
        {
            CapVideo => _modelName,
            CapImage => _imageModel,
            CapPlanning => _planningModel,
            CapVision => _visionModel,
            CapReview => _qualityModel,
            CapMusic => _audioModel,
            CapVoice => _voiceModel,
            CapVideoEdit => _videoEditModel,
            _ => "",
        };


        internal void SetCoverageModelId(string coverageId, string modelId)
        {
            switch (coverageId)
            {
                case CapVideo: _modelName = modelId; break;
                case CapImage: _imageModel = modelId; break;
                case CapPlanning: _planningModel = modelId; break;
                case CapVision: _visionModel = modelId; break;
                case CapReview: _qualityModel = modelId; break;
                case CapMusic: _audioModel = modelId; break;
                case CapVoice: _voiceModel = modelId; break;
                case CapVideoEdit: _videoEditModel = modelId; break;
            }
        }


        internal IReadOnlyList<SupportedModelDto> ModelsForCoverage(string coverageId) => coverageId switch
        {
            CapVideo => S.Catalog._videoModels,
            CapImage => S.Catalog._imageModels,
            CapPlanning => S.Catalog._planningModels,
            CapVision => S.Catalog._visionModels,
            CapReview => S.Catalog._videoReviewModels,
            CapMusic => S.Catalog._audioModels,
            CapVoice => S.Catalog._voiceModels,
            CapVideoEdit => S.Catalog._videoEditModels,
            _ => Array.Empty<SupportedModelDto>(),
        };


        /// <summary>Models for a coverage job limited to one provider (second-stage dropdown).</summary>
        internal IReadOnlyList<SupportedModelDto> ModelsForCoverageProvider(string coverageId, string? providerId)
        {
            var all = ModelsForCoverage(coverageId);
            if (string.IsNullOrWhiteSpace(providerId))
                return Array.Empty<SupportedModelDto>();
            var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
            var list = all
                .Where(m => !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase))
                .Where(m => string.Equals(ConfigurationCatalog.ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase))
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
                else if (coverageId is CapMusic or CapVoice or CapVideoEdit)
                    SetCoverageModelId(coverageId, "none");
            }

            _coverageEditId = null;
            S._message = null;
            if (!string.IsNullOrEmpty(S._projectId) && S._cfg is not null)
            {
                try
                {
                    await S.Form.SaveAsync();
                    S._message = $"Provider set to {ConfigurationCatalog.FriendlyProviderLabel(pid)}.";
                }
                catch (Exception ex)
                {
                    S._error = ex.Message;
                }
            }

            var row = BuildCoverageRows().FirstOrDefault(c =>
                string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
            if (row is { KeyReady: false, OptionalOff: false } && !string.IsNullOrWhiteSpace(row.ProviderId))
                S.Keys.BeginAddKey(coverageId, row.ProviderId);
        }


        internal IEnumerable<ProviderKeyStatusDto> ProvidersForCoverage(string coverageId)
        {
            var ids = CollectCoverageProviderIds(coverageId);
            var rows = MaterializeCoverageProviderRows(ids);
            AddMissingCoverageProviderRows(ids, rows);
            var currentPid = EnsureCurrentCoverageProviderRow(coverageId, rows);
            return OrderCoverageProviders(rows, currentPid);
        }

        private HashSet<string> CollectCoverageProviderIds(string coverageId)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in ModelsForCoverage(coverageId))
                AddModelProviderId(ids, m);
            if (ids.Count == 0)
                AddFallbackCoverageProviderIds(coverageId, ids);
            return ids;
        }

        private static void AddModelProviderId(HashSet<string> ids, SupportedModelDto m)
        {
            if (string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase))
                return;
            var pid = ConfigurationCatalog.ModelProviderId(m);
            if (!string.IsNullOrWhiteSpace(pid) && !pid.Equals("none", StringComparison.OrdinalIgnoreCase))
                ids.Add(pid);
        }

        private void AddFallbackCoverageProviderIds(string coverageId, HashSet<string> ids)
        {
            foreach (var pr in S.Keys.ProviderRows.Where(p => ProviderSupportsCoverage(p, coverageId)))
                ids.Add(pr.ProviderId);
        }

        private static bool ProviderSupportsCoverage(ProviderKeyStatusDto pr, string coverageId) =>
            coverageId switch
            {
                CapVideo => pr.SupportsVideoGen || pr.SupportsVideo,
                CapImage => pr.SupportsImageGen || pr.SupportsImage,
                CapPlanning => pr.SupportsScriptPlanning || pr.SupportsChat,
                CapVision => pr.SupportsImageVision || pr.SupportsVision,
                CapReview => pr.SupportsVideoReview || pr.SupportsChat,
                CapMusic => IsMusicCoverageProvider(pr.ProviderId),
                CapVoice => IsVoiceCoverageProvider(pr.ProviderId),
                _ => false,
            };

        private static bool IsMusicCoverageProvider(string? providerId) =>
            string.Equals(providerId, "fal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, "suno", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, "aimusicapi", StringComparison.OrdinalIgnoreCase);

        private static bool IsVoiceCoverageProvider(string? providerId) =>
            string.Equals(providerId, "elevenlabs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, "fal", StringComparison.OrdinalIgnoreCase);

        private List<ProviderKeyStatusDto> MaterializeCoverageProviderRows(HashSet<string> ids)
        {
            // Include any provider id we discovered from models even if ProviderRows lacks it (e.g. OpenAI).
            return S.Keys.ProviderRows
                .Select(CloneProviderRow)
                .Where(pr => ids.Contains(pr.ProviderId))
                .GroupBy(pr => pr.ProviderId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static ProviderKeyStatusDto CloneProviderRow(ProviderKeyStatusDto pr) => new()
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
        };

        private static void AddMissingCoverageProviderRows(HashSet<string> ids, List<ProviderKeyStatusDto> rows)
        {
            foreach (var id in ids)
            {
                if (rows.Any(r => string.Equals(r.ProviderId, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                rows.Add(StubProviderRow(id));
            }
        }

        private string EnsureCurrentCoverageProviderRow(string coverageId, List<ProviderKeyStatusDto> rows)
        {
            // Selected provider (from model) first so the <select> cannot fall back to a random first option.
            var currentModelId = GetCoverageModelId(coverageId);
            if (string.IsNullOrWhiteSpace(currentModelId)
                || string.Equals(currentModelId, "none", StringComparison.OrdinalIgnoreCase))
                return "";

            var currentPid = ResolveProviderIdForModel(
                currentModelId,
                CoverageCapabilityName(coverageId),
                preferVideoReview: coverageId == CapReview);
            if (!string.IsNullOrWhiteSpace(currentPid)
                && !rows.Any(r => string.Equals(r.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Insert(0, StubProviderRow(currentPid));
            }

            return currentPid;
        }

        private static string CoverageCapabilityName(string coverageId) => coverageId switch
        {
            CapVideo => CapVideo,
            CapImage => CapImage,
            CapPlanning => "chat",
            CapVision => CapVision,
            CapReview => "chat",
            CapMusic => CapAudio,
            CapVoice => CapVoice,
            CapVideoEdit => nameof(ModelCapability.VideoEdit),
            _ => "chat",
        };

        private static ProviderKeyStatusDto StubProviderRow(string id) => new()
        {
            ProviderId = id,
            DisplayName = ConfigurationCatalog.FriendlyProviderLabel(id),
            ActiveSource = "none",
            CapabilitiesSummary = "—",
            RequiredEnvKeys = new List<string>(),
        };

        private static IEnumerable<ProviderKeyStatusDto> OrderCoverageProviders(
            List<ProviderKeyStatusDto> rows, string currentPid) =>
            rows
                .OrderBy(pr => CurrentProviderSortKey(pr, currentPid))
                .ThenBy(ConfiguredSortKey)
                .ThenBy(pr => ConfigurationCatalog.FriendlyProviderLabel(pr.ProviderId));

        private static int CurrentProviderSortKey(ProviderKeyStatusDto pr, string currentPid) =>
            string.Equals(pr.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        private static int ConfiguredSortKey(ProviderKeyStatusDto pr) =>
            pr.IsConfigured ? 0 : 1;


        internal async Task OnCoverageModelChangedAsync(string coverageId, string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return;
            SetCoverageModelId(coverageId, modelId);
            _coverageEditId = null;
            S._message = null;
            await PersistCoverageModelChangeAsync();
            PromptAddKeyIfCoverageNeedsIt(coverageId);
        }

        private async Task PersistCoverageModelChangeAsync()
        {
            if (string.IsNullOrEmpty(S._projectId) || S._cfg is null)
                return;
            try
            {
                await S.Form.SaveAsync();
                S._message = "Model saved.";
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
        }

        private void PromptAddKeyIfCoverageNeedsIt(string coverageId)
        {
            var row = BuildCoverageRows().FirstOrDefault(c => string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
            if (row is { KeyReady: false, OptionalOff: false } && !string.IsNullOrWhiteSpace(row.ProviderId))
                S.Keys.BeginAddKey(coverageId, row.ProviderId);
        }


        internal async Task TurnOffOptionalAsync(string coverageId)
        {
            // Every optional slot turns off the same way — by storing "none" in its own field.
            SetCoverageModelId(coverageId, "none");
            if (coverageId == CapMusic)
                _enableBackgroundMusic = false;
            S.Keys.CancelAddKey();
            if (!string.IsNullOrEmpty(S._projectId) && S._cfg is not null)
            {
                try { await S.Form.SaveAsync(); }
                catch (Exception ex) { S._error = ex.Message; }
            }
        }


        /// <summary>Model product name from catalog displayName only.</summary>
        internal static string ModelOptionLabel(SupportedModelDto m)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName.Trim();
            return m.LabMode ? $"{name} [LAB]" : name;
        }




        /// <summary>After saving a voice-provider key, pick a clone-step model from the catalog for that provider.</summary>
        internal void EnsureVoiceModelForProvider(string providerId)
        {
            var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
            if (VoiceCloneModelAlreadyMatchesProvider(pid))
                return;

            var pick = FindVoiceModelForProvider(pid) ?? TryAddVoiceModelFromCatalog(pid);
            if (pick is not null)
                _voiceModel = pick.Id;
            // else leave as-is — no invented model id
        }

        private bool VoiceCloneModelAlreadyMatchesProvider(string pid)
        {
            var currentProvider = ResolveProviderIdForModel(_voiceModel, CapVoice, preferVideoReview: false);
            if (IsCoverageModelOff(_voiceModel) ||
                !string.Equals(currentProvider, pid, StringComparison.OrdinalIgnoreCase))
                return false;
            var cur = S.Catalog._voiceModels.FirstOrDefault(m => string.Equals(m.Id, _voiceModel, StringComparison.OrdinalIgnoreCase));
            if (cur is { IsVoiceCloneStep: true })
                return true;
            // Prefer clone-step over TTS-only when switching within same provider.
            return false;
        }

        private SupportedModelDto? FindVoiceModelForProvider(string pid) =>
            S.Catalog._voiceModels.FirstOrDefault(m =>
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
                && m.IsVoiceCloneStep
                && string.Equals(ConfigurationCatalog.ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase))
            ?? S.Catalog._voiceModels.FirstOrDefault(m =>
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ConfigurationCatalog.ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));

        private SupportedModelDto? TryAddVoiceModelFromCatalog(string pid)
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
                if (fromCatalog is null)
                    return null;
                var pick = SupportedModelCatalog.ToDto(fromCatalog);
                if (!S.Catalog._voiceModels.Any(m => string.Equals(m.Id, pick.Id, StringComparison.OrdinalIgnoreCase)))
                    S.Catalog._voiceModels.Add(pick);
                return pick;
            }
            catch { return null; }
        }

        private static bool IsCoverageModelOff(string modelId) =>
            string.IsNullOrWhiteSpace(modelId)
            || modelId.Equals("none", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals(ModelDisabled, StringComparison.OrdinalIgnoreCase);

        /// <summary>After saving a music-provider key, pick a matching music model from the catalog.</summary>
        internal void EnsureMusicModelForProvider(string providerId)
        {
            var pid = SupportedModelCatalog.NormalizeProviderId(providerId);
            var currentProvider = ResolveProviderIdForModel(_audioModel, CapAudio, preferVideoReview: false);
            if (!IsCoverageModelOff(_audioModel) && string.Equals(currentProvider, pid, StringComparison.OrdinalIgnoreCase))
                return;

            var pick = FindMusicModelForProvider(pid) ?? TryAddMusicModelFromCatalog(pid);
            if (pick is not null)
            {
                _audioModel = pick.Id;
                _enableBackgroundMusic = true;
            }
        }

        private SupportedModelDto? FindMusicModelForProvider(string pid) =>
            S.Catalog._audioModels.FirstOrDefault(m =>
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ConfigurationCatalog.ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));

        private SupportedModelDto? TryAddMusicModelFromCatalog(string pid)
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
                if (fromCatalog is null)
                    return null;
                var pick = SupportedModelCatalog.ToDto(fromCatalog);
                if (!S.Catalog._audioModels.Any(m => string.Equals(m.Id, pick.Id, StringComparison.OrdinalIgnoreCase)))
                    S.Catalog._audioModels.Add(pick);
                return pick;
            }
            catch { return null; }
        }

    }
}
