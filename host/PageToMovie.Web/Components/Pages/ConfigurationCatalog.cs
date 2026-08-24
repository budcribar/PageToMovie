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
    /// <summary>Catalog domain for the Configuration page. Owns related UI state and behavior.</summary>
    public sealed class ConfigurationCatalog
    {
        private readonly Configuration S;
        public ConfigurationCatalog(Configuration host) => S = host;

        internal List<SupportedModelDto> _allModels = new();

        internal List<SupportedModelDto> _audioModels = new();

        internal List<SupportedModelDto> _imageModels = new();

        internal List<SupportedModelDto> _planningModels = new();

        internal List<SupportedModelDto> _videoModels = new();

        internal List<SupportedModelDto> _videoReviewModels = new();

        internal List<SupportedModelDto> _visionModels = new();

        internal List<SupportedModelDto> _voiceModels = new();


        internal async Task LoadCatalogAsync()
        {
            // Catalog is the only source of truth — never invent models in UI code.
            ResetCatalogLists();
            try
            {
                await TryHydrateCatalogJsonAsync();
                await LoadAndFilterModelsAsync();
                AssignModelsByCapability();
                EnsureOptionalNoneRows();
                ApplyCatalogDefaultsIfEmpty();
            }
            catch (Exception ex)
            {
                S._error = $"Models catalog unavailable: {ex.Message}";
            }
        }

        private void ResetCatalogLists()
        {
            _allModels = new();
            _videoModels = new();
            _imageModels = new();
            _planningModels = new();
            _visionModels = new();
            _videoReviewModels = new();
            _audioModels = new();
            _voiceModels = new();
        }

        private async Task TryHydrateCatalogJsonAsync()
        {
            try
            {
                var raw = await S.Engine.GetModelsCatalogJsonAsync();
                if (!string.IsNullOrWhiteSpace(raw))
                    SupportedModelCatalog.TryLoadFromJson(raw);
            }
            catch (Exception ex)
            {
                S._error = $"Could not load models catalog: {ex.Message}";
            }
        }

        private async Task LoadAndFilterModelsAsync()
        {
            var list = await S.Engine.GetSupportedModelsAsync();
            _allModels = (list ?? Array.Empty<SupportedModelDto>())
                .Where(VisibleToSession)
                .ToList();
            // Hydrate from static catalog when the API list is empty/stale (WASM must still show music/voice).
            if (_allModels.Count == 0)
                TryHydrateFromStaticCatalog();
        }

        private void TryHydrateFromStaticCatalog()
        {
            try
            {
                _allModels = SupportedModelCatalog.ToDtoList(enabledOnly: true, includeLabModels: S.Session.IsAdmin)
                    .Where(VisibleToSession)
                    .ToList();
            }
            catch { /* catalog not loaded */ }
        }

        private bool VisibleToSession(SupportedModelDto m) =>
            S.Session.IsAdmin || !m.LabMode;

        private static bool MatchesCapability(SupportedModelDto m, string c)
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

        private void AssignModelsByCapability()
        {
            _videoModels = _allModels.Where(m => MatchesCapability(m, "video")).ToList();
            _imageModels = _allModels.Where(m => MatchesCapability(m, "image")).ToList();
            _planningModels = _allModels.Where(m => MatchesCapability(m, "chat")).ToList();
            _visionModels = _allModels.Where(m => MatchesCapability(m, "vision")).ToList();
            _audioModels = _allModels.Where(m => MatchesCapability(m, "audio")).ToList();
            _voiceModels = _allModels.Where(m => MatchesCapability(m, "voice")).ToList();
            // Review: only models the catalog marks SupportsVideoReview, else chat/vision from catalog.
            _videoReviewModels = _allModels.Where(m => m.SupportsVideoReview).ToList();
            if (_videoReviewModels.Count == 0)
                _videoReviewModels = _allModels.Where(m => MatchesCapability(m, "chat") || MatchesCapability(m, "vision")).ToList();
        }

        private void EnsureOptionalNoneRows()
        {
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
        }


        /// <summary>
        /// Optional slots may stay <c>none</c> until the user picks. Required slots stay
        /// empty — do not seed <c>capabilities[].defaultModelId</c> into project Settings.
        /// </summary>
        internal void ApplyCatalogDefaultsIfEmpty()
        {
            if (string.IsNullOrWhiteSpace(S.Coverage._audioModel))
                S.Coverage._audioModel = "none";
            if (string.IsNullOrWhiteSpace(S.Coverage._voiceModel))
                S.Coverage._voiceModel = "none";
        }


        internal static string ModelProviderId(SupportedModelDto m)
        {
            // Catalog fields only (providerId / provider). Never guess from model id.
            if (!string.IsNullOrWhiteSpace(m.ProviderId))
                return SupportedModelCatalog.NormalizeProviderId(m.ProviderId);
            if (!string.IsNullOrWhiteSpace(m.Provider))
                return SupportedModelCatalog.NormalizeProviderId(m.Provider);
            return "";
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


        /// <summary>Provider UI label — catalog providers[] only.</summary>
        internal static string FriendlyProviderLabel(string? providerId) =>
            SupportedModelCatalog.ProviderLabelFor(providerId);



        internal string VendorLabel(string modelId, string capability)
        {
            var m = _allModels.FirstOrDefault(x =>
                string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase))
                ?? _allModels.FirstOrDefault(x =>
                    string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null)
            {
                ModelCapability cap;
                if (capability.Equals("video", StringComparison.OrdinalIgnoreCase))
                    cap = ModelCapability.Video;
                else if (capability.Equals("image", StringComparison.OrdinalIgnoreCase))
                    cap = ModelCapability.Image;
                else
                    cap = ModelCapability.Chat;
                return SupportedModelCatalog.ProviderIdFor(modelId, cap);
            }
            return string.IsNullOrWhiteSpace(m.ProviderId) ? m.Provider : m.ProviderId;
        }


        internal double CatalogVideoRate(string resolution)
        {
            try
            {
                var entry = SupportedModelCatalog.ResolveOrDefault(S.Coverage._modelName, ModelCapability.Video);
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
                var entry = SupportedModelCatalog.Find(S.Coverage._imageModel, ModelCapability.Image)
                            ?? SupportedModelCatalog.ResolveOrDefault(S.Coverage._imageModel, ModelCapability.Image);
                return entry.ImageCostPerImage ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }


        internal static string UsdRate(double v) =>
            v < 0.01 ? $"${v:0.####}" : $"${v:0.##}";

    }
}
