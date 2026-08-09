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
    /// <summary>Form domain for the Configuration page. Owns related UI state and behavior.</summary>
    internal sealed class ConfigurationProjectForm
    {
        private readonly Configuration S;
        public ConfigurationProjectForm(Configuration host) => S = host;

        internal string _aspect = "16:9";

        internal double _audioGain = 6;

        internal CancellationTokenSource? _autoSaveCts;

        internal int _autoSaveEpoch;

        internal string _blueprintFile = "blueprint.clips.grok.json";

        internal bool _dirty;

        internal int _durationSeconds = 8;

        internal bool _mergeAfterClip = true;

        internal string _preferredVideoEditor = "ClipChamp";

        internal string? _projectDir;

        internal int _qaFrameCount = 4;

        internal int _qaMaxRetries = 2;

        internal bool _qaRetry = true;

        internal bool _rebuildWip = true;

        internal bool _regenSilent = true;

        internal string _resolution = "480p";

        internal string? _saveStatus;

        internal bool _smartContinuation = true;

        internal bool _useDurationDefaults = true;

        internal string _wipPath = "assets/movie_wip.mp4";


        internal async Task OnProjectChangedAsync(ChangeEventArgs e)
        {
            var id = e.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(id)) return;
            S._projectId = id;
            S.ActiveProject.Set(id);
            await LoadAsync();
        }


        internal async Task LoadAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                try { S._userSettings = await S.Engine.GetUserSettingsAsync(); } catch { }

                if (string.IsNullOrWhiteSpace(S._projectId))
                {
                    S._cfg = null;
                    return;
                }

                var dto = await S.Engine.GetConfigAsync(S._projectId);
                S._cfg = dto?.Config;
                _projectDir = dto?.ProjectDir ?? $"projects/{S._projectId}";
                if (S._cfg is null) return;
                S._uiTheme = ThemeState.Normalize(GetStr("ui_theme", S._uiTheme));
                _preferredVideoEditor = GetStr("preferred_video_editor", "ClipChamp");
                _blueprintFile = GetStr("blueprint_file", _blueprintFile);
                S._modelName = GetStr("model_name", S._modelName);
                S._imageModel = GetStr("image_model_name", S._imageModel);
                S._planningModel = GetStr("planning_model_name", S._planningModel);
                S._visionModel = GetStr("vision_model_name", S._visionModel);
                S._qualityModel = GetStr("quality_model_name", S._qualityModel);
                S._audioModel = GetStr("audio_model_name", S._audioModel);
                S._voiceModel = GetStr("voice_model_name", S._voiceModel);
                // Drop ids that are not in the catalog (stale project config).
                if (!string.IsNullOrWhiteSpace(S._modelName) && !S._videoModels.Any(m => string.Equals(m.Id, S._modelName, StringComparison.OrdinalIgnoreCase)))
                    S._modelName = ConfigurationCatalog.DefaultForCapability("video");
                if (!string.IsNullOrWhiteSpace(S._imageModel) && !S._imageModels.Any(m => string.Equals(m.Id, S._imageModel, StringComparison.OrdinalIgnoreCase)))
                    S._imageModel = ConfigurationCatalog.DefaultForCapability("image");
                if (!string.IsNullOrWhiteSpace(S._planningModel) && !S._planningModels.Any(m => string.Equals(m.Id, S._planningModel, StringComparison.OrdinalIgnoreCase)))
                    S._planningModel = ConfigurationCatalog.DefaultForCapability("chat");
                if (!string.IsNullOrWhiteSpace(S._visionModel) && !S._visionModels.Any(m => string.Equals(m.Id, S._visionModel, StringComparison.OrdinalIgnoreCase)))
                    S._visionModel = ConfigurationCatalog.DefaultForCapability("vision");
                if (!string.IsNullOrWhiteSpace(S._qualityModel) && !S._videoReviewModels.Any(m => string.Equals(m.Id, S._qualityModel, StringComparison.OrdinalIgnoreCase)))
                    S._qualityModel = ConfigurationCatalog.DefaultQualityModel();
                if (!string.IsNullOrWhiteSpace(S._audioModel)
                    && !S._audioModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                    && !S._audioModels.Any(m => string.Equals(m.Id, S._audioModel, StringComparison.OrdinalIgnoreCase)))
                    S._audioModel = "none";
                if (!string.IsNullOrWhiteSpace(S._voiceModel)
                    && !S._voiceModel.Equals("none", StringComparison.OrdinalIgnoreCase)
                    && !S._voiceModels.Any(m => string.Equals(m.Id, S._voiceModel, StringComparison.OrdinalIgnoreCase)))
                    S._voiceModel = "none";
                S._enableBackgroundMusic = GetBool("enable_background_music", S._enableBackgroundMusic);
                S._backgroundMusicVolumePercent = GetInt("background_music_volume_percent", S._backgroundMusicVolumePercent);
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
                S._error = ex.Message;
                S._cfg = null;
            }
            finally { S._busy = false; }
        }


        internal async Task SaveAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                if (string.IsNullOrWhiteSpace(S._projectId))
                {
                    S._error = "Choose a project first.";
                    return;
                }

                await PersistProjectConfigAsync();
                S._message = $"Settings saved for {S._projectId}";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally { S._busy = false; }
        }


        /// <summary>Write current form fields to the project config (no busy flag / reload).</summary>
        internal async Task PersistProjectConfigAsync()
        {
            if (string.IsNullOrWhiteSpace(S._projectId))
                throw new InvalidOperationException("Choose a project first.");

            var videoProvider = SupportedModelCatalog.ProviderIdFor(S._modelName, ModelCapability.Video);
            var imageProvider = SupportedModelCatalog.ProviderIdFor(S._imageModel, ModelCapability.Image);
            var planningProvider = SupportedModelCatalog.ProviderIdFor(S._planningModel, ModelCapability.Chat);
            var visionProvider = SupportedModelCatalog.ProviderIdFor(S._visionModel, ModelCapability.Vision);
            var qualityProvider = SupportedModelCatalog.ProviderIdFor(S._qualityModel, ModelCapability.Chat);

            var updates = new Dictionary<string, object?>
            {
                ["version"] = 2,
                ["ui_theme"] = S._uiTheme,
                ["preferred_video_editor"] = _preferredVideoEditor,
                ["blueprint_file"] = _blueprintFile,
                ["video_provider"] = videoProvider,
                ["character_design_provider"] = imageProvider,
                ["image_provider"] = imageProvider,
                ["planning_provider"] = planningProvider,
                ["vision_provider"] = visionProvider,
                ["quality_provider"] = qualityProvider,
                ["model_name"] = S._modelName,
                ["image_model_name"] = S._imageModel,
                ["planning_model_name"] = S._planningModel,
                ["vision_model_name"] = S._visionModel,
                ["quality_model_name"] = S._qualityModel,
                ["audio_model_name"] = S._audioModel,
                ["voice_model_name"] = S._voiceModel,
                ["enable_background_music"] = S._enableBackgroundMusic,
                ["background_music_volume_percent"] = S._backgroundMusicVolumePercent,
                ["model_selections"] = new Dictionary<string, string>
                {
                    ["video"] = S._modelName,
                    ["image"] = S._imageModel,
                    ["chat"] = S._planningModel,
                    ["vision"] = S._visionModel,
                    ["video-review"] = S._qualityModel,
                    ["audio"] = S._audioModel,
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

            await S.Engine.SaveConfigAsync(S._projectId, updates);
        }


        internal Dictionary<string, object?> BuildVendorCostEstimatesSnapshot()
        {
            var video = SupportedModelCatalog.ResolveOrDefault(S._modelName, ModelCapability.Video);
            var image = SupportedModelCatalog.Find(S._imageModel, ModelCapability.Image)
                        ?? SupportedModelCatalog.ResolveOrDefault(S._imageModel, ModelCapability.Image);
            var table = new Dictionary<string, object?>
            {
                ["480p"] = S.Catalog.CatalogVideoRate("480p"),
                ["720p"] = S.Catalog.CatalogVideoRate("720p"),
                ["1080p"] = S.Catalog.CatalogVideoRate("1080p"),
            };

            // Preserve planning knobs (retries, etc.) if present; drop manual rate tables.
            double assumeRetries = 0;
            bool assumeRef = true;
            if (S._cfg is not null &&
                S._cfg.TryGetValue("cost_estimates", out var oldCe) &&
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


        internal string GetStr(string key, string fallback) =>
            S._cfg is not null && S._cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;


        internal int GetInt(string key, int fallback) =>
            S._cfg is not null && S._cfg.TryGetValue(key, out var el) && el.TryGetInt32(out var v)
                ? v
                : fallback;


        internal double GetDouble(string key, double fallback) =>
            S._cfg is not null && S._cfg.TryGetValue(key, out var el) && el.TryGetDouble(out var v)
                ? v
                : fallback;


        internal bool GetBool(string key, bool fallback) =>
            S._cfg is not null && S._cfg.TryGetValue(key, out var el) &&
            (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? el.GetBoolean()
                : fallback;


        /// <summary>Debounced project-settings save — no bottom Save button.</summary>
        internal async Task ScheduleAutoSaveAsync()
        {
            if (string.IsNullOrWhiteSpace(S._projectId) || S._cfg is null)
                return;

            _dirty = true;
            var epoch = ++_autoSaveEpoch;
            _autoSaveCts?.Cancel();
            _autoSaveCts?.Dispose();
            _autoSaveCts = new CancellationTokenSource();
            var token = _autoSaveCts.Token;

            _saveStatus = "Saving…";
            try { await S.InvokeAsync(S.StateHasChanged); } catch { /* ignore */ }

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
                try { await S.InvokeAsync(S.StateHasChanged); } catch { /* ignore */ }

                // Fade the Saved label after a moment.
                _ = ClearSaveStatusLaterAsync(epoch);
            }
            catch (TaskCanceledException)
            {
                // newer edit superseded this save
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                _saveStatus = "Save failed";
                try { await S.InvokeAsync(S.StateHasChanged); } catch { /* ignore */ }
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
                    await S.InvokeAsync(S.StateHasChanged);
                }
            }
            catch { /* ignore */ }
        }

    }
}
