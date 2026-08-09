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
    /// <summary>Keys domain for the Configuration page. Owns related UI state and behavior.</summary>
    public sealed class ConfigurationKeys
    {
        private readonly Configuration S;
        public ConfigurationKeys(Configuration host) => S = host;

        internal string? _apiKeyFeedback;

        internal bool _apiKeySaving;

        internal readonly Dictionary<string, string?> _keyInputs = new(StringComparer.OrdinalIgnoreCase)
            {
                ["grok"] = null,
                ["gemini"] = null,
                ["anthropic"] = null,
                ["fal"] = null,
            };

        internal string? _savingProviderId;

        internal UserSettingsDto? _userSettings;

        internal readonly HashSet<string> _visibleKeyProviders = new(StringComparer.OrdinalIgnoreCase);


        internal bool IsKeyVisible(string providerId) => _visibleKeyProviders.Contains(providerId);


        internal void ToggleKeyVisible(string providerId)
        {
            if (!IsKeyVisible(providerId)) _visibleKeyProviders.Add(providerId);
            else _visibleKeyProviders.Remove(providerId);
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
            S.Coverage._coverageEditId = coverageId;
            S.Coverage._coverageKeyProviderId = SupportedModelCatalog.NormalizeProviderId(providerId);
            S.Coverage._coverageKeyMode = "replace";
            _apiKeyFeedback = null;
            _keyInputs[S.Coverage._coverageKeyProviderId] = null;
        }


        /// <summary>Add/paste key for the provider currently on this coverage row only.</summary>
        internal void BeginAddKeyForProvider(string coverageId, string providerId)
        {
            S.Coverage._coverageEditId = coverageId;
            S.Coverage._coverageKeyProviderId = SupportedModelCatalog.NormalizeProviderId(providerId);
            S.Coverage._coverageKeyMode = "add-key";
            _apiKeyFeedback = null;
            _keyInputs[S.Coverage._coverageKeyProviderId] = null;
        }


        /// <summary>Choose among all providers that can run this job.</summary>
        internal void BeginAddProvider(string coverageId)
        {
            S.Coverage._coverageEditId = coverageId;
            S.Coverage._coverageKeyMode = "add-provider";
            _apiKeyFeedback = null;
            var list = S.Coverage.ProvidersForCoverage(coverageId).ToList();
            // Prefer first without a key so “Add provider” lands on something useful.
            S.Coverage._coverageKeyProviderId = list.FirstOrDefault(p => !p.IsConfigured)?.ProviderId
                                     ?? list.FirstOrDefault()?.ProviderId;
        }


        internal void CancelAddKey()
        {
            S.Coverage._coverageEditId = null;
            S.Coverage._coverageKeyProviderId = null;
            S.Coverage._coverageKeyMode = "add-provider";
        }


        /// <summary>
        /// Providers shown in the open key panel.
        /// Replace / Add key → only the target provider. Add provider → full list for the job.
        /// </summary>
        internal IEnumerable<ProviderKeyStatusDto> ProvidersForKeyPanel(string coverageId)
        {
            var all = S.Coverage.ProvidersForCoverage(coverageId);
            if (S.Coverage._coverageKeyMode is "replace" or "add-key"
                && !string.IsNullOrWhiteSpace(S.Coverage._coverageKeyProviderId))
            {
                var only = all.Where(p =>
                    string.Equals(p.ProviderId, S.Coverage._coverageKeyProviderId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (only.Count > 0)
                    return only;
                // Provider might not be in job list yet — synthesize a row from ProviderRows.
                var row = ProviderRows.FirstOrDefault(p =>
                    string.Equals(p.ProviderId, S.Coverage._coverageKeyProviderId, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                    return new[] { row };
            }
            return all;
        }


        internal void SelectKeyProvider(string providerId) => S.Coverage._coverageKeyProviderId = providerId;


        internal async Task SaveCoverageKeyAsync(string providerId, string coverageId)
        {
            await SaveProviderKeyAsync(providerId);
            var row = S.Coverage.BuildCoverageRows().FirstOrDefault(c => string.Equals(c.Id, coverageId, StringComparison.OrdinalIgnoreCase));
            if (row?.KeyReady == true)
                CancelAddKey();
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


        internal string? GetKeyInput(string providerId) =>
            _keyInputs.TryGetValue(providerId, out var v) ? v : null;


        internal void SetKeyInput(string providerId, string? value) =>
            _keyInputs[providerId] = value;


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
                S._busy = true;
                _apiKeySaving = true;
                _savingProviderId = providerId;
                _apiKeyFeedback = null;
                S._error = null;

                _userSettings = await S.Engine.UpdateUserSettingsAsync(
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
                    if (!string.IsNullOrWhiteSpace(S._projectId) && S._cfg is not null)
                    {
                        ApplyProviderModelDefaults(providerId);
                        try { await S.Form.PersistProjectConfigAsync(); } catch { /* optional */ }
                    }
                    // Music key: if coverage model is none/unknown, pick a model for this provider so Ready lights up.
                    if (providerId is "fal" or "suno" or "aimusicapi")
                    {
                        S.Coverage.EnsureMusicModelForProvider(providerId);
                    }
                    // Voice key (ElevenLabs) or Fal while adding voice coverage: pick clone-step model, not TTS-only.
                    if (providerId is "elevenlabs"
                        || (providerId is "fal" && string.Equals(S.Coverage._coverageEditId, "voice", StringComparison.OrdinalIgnoreCase)))
                    {
                        S.Coverage.EnsureVoiceModelForProvider(providerId);
                    }
                    if ((providerId is "fal" or "suno" or "aimusicapi" or "elevenlabs")
                        && !string.IsNullOrWhiteSpace(S._projectId) && S._cfg is not null)
                    {
                        try { await S.Form.PersistProjectConfigAsync(); } catch { /* optional */ }
                    }
                }

                S._message = _apiKeyFeedback;
                await S.ActiveProject.RefreshReadinessAsync(S.Engine);
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
                _apiKeySaving = false;
                _savingProviderId = null;
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
                    && string.Equals(ConfigurationCatalog.ModelProviderId(m), pid, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) slot = hit.Id;
            }
            Prefer(ref S.Coverage._modelName, S.Catalog._videoModels);
            Prefer(ref S.Coverage._imageModel, S.Catalog._imageModels);
            Prefer(ref S.Coverage._planningModel, S.Catalog._planningModels);
            Prefer(ref S.Coverage._visionModel, S.Catalog._visionModels);
            Prefer(ref S.Coverage._qualityModel, S.Catalog._videoReviewModels);
        }

    }
}
