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
    public sealed class ConfigurationProjectForm
    {
        private readonly Configuration S;
        public ConfigurationProjectForm(Configuration host) => S = host;

        internal string _aspect = "16:9";

        internal double _audioGain = 6;

        internal int? _adaptMaxSpeakingCast;

        internal int? _adaptMaxDialogueWords;

        internal int? _adaptVoMaxSentences;

        internal int? _adaptSceneCountMin;

        internal int? _adaptSceneCountMax;

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
                await TryLoadUserSettingsAsync();
                if (string.IsNullOrWhiteSpace(S._projectId))
                {
                    S._cfg = null;
                    return;
                }

                var dto = await S.Engine.GetConfigAsync(S._projectId);
                S._cfg = dto?.Config;
                _projectDir = dto?.ProjectDir ?? $"projects/{S._projectId}";
                if (S._cfg is null) return;
                var healed = ApplyLoadedConfig();
                if (healed)
                {
                    try { await PersistProjectConfigAsync(); }
                    catch (Exception persistEx)
                    {
                        S._error = persistEx.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
                S._cfg = null;
            }
            finally { S._busy = false; }
        }

        private async Task TryLoadUserSettingsAsync()
        {
            try { S.Keys._userSettings = await S.Engine.GetUserSettingsAsync(); }
            catch (Exception)
            {
                // User settings are optional; continue loading project config without them.
                System.Diagnostics.Debug.WriteLine("User settings unavailable; continuing project load.");
            }
        }

        private bool ApplyLoadedConfig()
        {
            var healed = S._cfg is not null && ProjectCatalogModelHeal.Apply(S._cfg);
            ApplyUiAndModelFields();
            ApplyPipelineFields();
            return healed;
        }

        private void ApplyUiAndModelFields()
        {
            S.Media._uiTheme = ThemeState.Normalize(GetStr("ui_theme", S.Media._uiTheme));
            _preferredVideoEditor = GetStr("preferred_video_editor", "ClipChamp");
            _blueprintFile = GetStr("blueprint_file", _blueprintFile);
            S.Coverage._modelName = GetStr("model_name", S.Coverage._modelName);
            S.Coverage._imageModel = GetStr("image_model_name", S.Coverage._imageModel);
            S.Coverage._planningModel = GetStr("planning_model_name", S.Coverage._planningModel);
            S.Coverage._visionModel = GetStr("vision_model_name", S.Coverage._visionModel);
            S.Coverage._qualityModel = GetStr("quality_model_name", S.Coverage._qualityModel);
            S.Coverage._audioModel = GetStr("audio_model_name", S.Coverage._audioModel);
            S.Coverage._voiceModel = GetStr("voice_model_name", S.Coverage._voiceModel);
        }

        private void ApplyPipelineFields()
        {
            S.Coverage._enableBackgroundMusic = GetBool("enable_background_music", S.Coverage._enableBackgroundMusic);
            S.Coverage._backgroundMusicVolumePercent = GetInt("background_music_volume_percent", S.Coverage._backgroundMusicVolumePercent);
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
            _adaptMaxSpeakingCast = GetIntOrNull("adaptation_max_speaking_cast");
            _adaptMaxDialogueWords = GetIntOrNull("adaptation_max_dialogue_words");
            _adaptVoMaxSentences = GetIntOrNull("adaptation_vo_max_sentences");
            _adaptSceneCountMin = GetIntOrNull("adaptation_scene_count_min");
            _adaptSceneCountMax = GetIntOrNull("adaptation_scene_count_max");
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

            var videoProvider = SupportedModelCatalog.ProviderIdFor(S.Coverage._modelName, ModelCapability.Video);
            var imageProvider = SupportedModelCatalog.ProviderIdFor(S.Coverage._imageModel, ModelCapability.Image);
            var planningProvider = SupportedModelCatalog.ProviderIdFor(S.Coverage._planningModel, ModelCapability.Chat);
            var visionProvider = SupportedModelCatalog.ProviderIdFor(S.Coverage._visionModel, ModelCapability.Vision);
            var qualityProvider = SupportedModelCatalog.ProviderIdFor(S.Coverage._qualityModel, ModelCapability.Chat);

            var updates = new Dictionary<string, object?>
            {
                ["version"] = 2,
                ["ui_theme"] = S.Media._uiTheme,
                ["preferred_video_editor"] = _preferredVideoEditor,
                ["blueprint_file"] = _blueprintFile,
                ["video_provider"] = videoProvider,
                ["character_design_provider"] = imageProvider,
                ["image_provider"] = imageProvider,
                ["planning_provider"] = planningProvider,
                ["vision_provider"] = visionProvider,
                ["quality_provider"] = qualityProvider,
                ["model_name"] = S.Coverage._modelName,
                ["image_model_name"] = S.Coverage._imageModel,
                ["planning_model_name"] = S.Coverage._planningModel,
                ["vision_model_name"] = S.Coverage._visionModel,
                ["quality_model_name"] = S.Coverage._qualityModel,
                ["audio_model_name"] = S.Coverage._audioModel,
                ["voice_model_name"] = S.Coverage._voiceModel,
                ["enable_background_music"] = S.Coverage._enableBackgroundMusic,
                ["background_music_volume_percent"] = S.Coverage._backgroundMusicVolumePercent,
                ["model_selections"] = new Dictionary<string, string>
                {
                    ["video"] = S.Coverage._modelName,
                    ["image"] = S.Coverage._imageModel,
                    ["chat"] = S.Coverage._planningModel,
                    ["vision"] = S.Coverage._visionModel,
                    ["video-review"] = S.Coverage._qualityModel,
                    ["audio"] = S.Coverage._audioModel,
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
                ["adaptation_max_speaking_cast"] = _adaptMaxSpeakingCast,
                ["adaptation_max_dialogue_words"] = _adaptMaxDialogueWords,
                ["adaptation_vo_max_sentences"] = _adaptVoMaxSentences,
                ["adaptation_scene_count_min"] = _adaptSceneCountMin,
                ["adaptation_scene_count_max"] = _adaptSceneCountMax,
                // Snapshot vendor rates for transparency; runtime CostReportService always re-derives
                // from model_name / image_model_name via SupportedModelCatalog.
                ["cost_estimates"] = BuildVendorCostEstimatesSnapshot(),
            };
            if (S._cfg is not null
                && S._cfg.TryGetValue("video_review_model_name", out var legacyReview)
                && legacyReview.ValueKind == JsonValueKind.String)
            {
                updates["video_review_model_name"] = legacyReview.GetString();
            }

            await S.Engine.SaveConfigAsync(S._projectId, updates);
        }


        internal Dictionary<string, object?> BuildVendorCostEstimatesSnapshot()
        {
            var video = SupportedModelCatalog.ResolveOrDefault(S.Coverage._modelName, ModelCapability.Video);
            var image = SupportedModelCatalog.Find(S.Coverage._imageModel, ModelCapability.Image)
                        ?? SupportedModelCatalog.ResolveOrDefault(S.Coverage._imageModel, ModelCapability.Image);
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


        internal int? GetIntOrNull(string key) =>
            S._cfg is not null && S._cfg.TryGetValue(key, out var el) &&
            el.ValueKind != JsonValueKind.Null && el.TryGetInt32(out var v)
                ? v
                : null;


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
            if (_autoSaveCts is { } oldCts)
            {
                await oldCts.CancelAsync();
                oldCts.Dispose();
            }
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
                await Task.Delay(2000, _autoSaveCts?.Token ?? CancellationToken.None);
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
