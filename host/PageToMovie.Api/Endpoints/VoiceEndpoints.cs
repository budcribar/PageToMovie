using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Api;

public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        // <summary>Read the persisted per-clip speech alignment for a project (empty when never built).</summary>
        app.MapGet("/api/projects/{id}/voice-alignment", GetProjectsIdVoiceAlignment);
        // <summary>
        // Persist client-detected speech timestamps (from browser ffmpeg silence detection) onto the saved
        // alignment so a future voice substitution reuses them and skips re-detection. Merges by segment
        // index; character/text/audio paths are preserved.
        // </summary>
        app.MapPost("/api/projects/{id}/voice-alignment/timestamps", PostProjectsIdVoiceAlignmentTimestamps);
        // <summary>Save voice_label / voice_profile into cast_seeds (+ blueprint) character seeds.</summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice", PostProjectsIdCharactersCharKeyVoice);
        // <summary>
        // Upload or replace voice-clone template audio (mic recording or file).
        // Multipart field: file. Stored under assets/characters/{key}/voice_clone_sample.*.
        // Used as a reference for future TTS clone providers; does not replace voice_profile text.
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice/clone-sample", PostProjectsIdCharactersCharKeyVoiceCloneSample)
            .WithUploadSizeLimit(ApiEndpointHelpers.VoiceSampleBytes);
        app.MapGet("/api/projects/{id}/characters/{charKey}/voice/clone-sample", GetProjectsIdCharactersCharKeyVoiceCloneSample);
        app.MapDelete("/api/projects/{id}/characters/{charKey}/voice/clone-sample", DeleteProjectsIdCharactersCharKeyVoiceCloneSample);
        // <summary>
        // Clone a voice from this character's saved voice-clone sample (reuses the same per-character
        // storage as the /voice/clone-sample upload above — a narration flow can point charKey at a
        // caller-chosen pseudo-character like "Narrator" rather than an on-screen cast member). Explicit,
        // human-triggered only — spends real provider money ($1.50/clone as of 2026-08, see
        // models_catalog.json) and is never called automatically from any job/pipeline. The returned
        // provider voice id is cached on the character seed so repeat narration calls reuse it instead of
        // re-cloning (and re-paying) every time.
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice/clone", PostProjectsIdCharactersCharKeyVoiceClone);
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice/speak", PostProjectsIdCharactersCharKeyVoiceSpeak);
        // <summary>
        // Video lip-sync: resync a video clip's mouth movement to a separate dialogue/narration audio
        // track (multipart upload: fields "video" and "audio", both required; optional "model" and
        // "syncMode" fields). Explicit, human-triggered per-clip action — spends real provider money
        // (~$5/min of output video as of 2026-08, see models_catalog.json) and is never called
        // automatically from any job/pipeline. Returns a media-proxy URL, not the raw provider URL.
        // </summary>
        app.MapPost("/api/projects/{id}/media/lip-sync", PostProjectsIdMediaLipSync);
        // <summary>List provider voices (ElevenLabs premade + clones, or mock catalog).</summary>
        app.MapGet("/api/voices", GetVoices);
        // <summary>
        // Create/apply a voice clone for a character from the saved sample (or generate a demo sample),
        // store provider voice_id on the seed, and synthesize a short TTS preview.
        // Complements POST .../voice/clone (Fal MiniMax) — this path uses IVoiceClient (ElevenLabs).
        // </summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice/apply-clone", PostProjectsIdCharactersCharKeyVoiceApplyClone);
        // <summary>Assign a catalog/premade provider voice id to a character (no sample clone).</summary>
        app.MapPost("/api/projects/{id}/characters/{charKey}/voice/apply-catalog", PostProjectsIdCharactersCharKeyVoiceApplyCatalog);
        // <summary>Serve the last TTS preview for a character (from apply-clone / apply-catalog).</summary>
        app.MapGet("/api/projects/{id}/characters/{charKey}/voice/tts-preview", GetProjectsIdCharactersCharKeyVoiceTtsPreview);
        // <summary>Cache status for film voice sample (matches current profile text?).</summary>
        app.MapGet("/api/projects/{id}/characters/{charKey}/voice/audio/status", GetProjectsIdCharactersCharKeyVoiceAudioStatus);
        // <summary>Serve cached film voice sample (MP4 preferred; legacy MP3 still supported).</summary>
        app.MapGet("/api/projects/{id}/characters/{charKey}/voice/audio", GetProjectsIdCharactersCharKeyVoiceAudio);
        // Per-scene solo lines for a target character (default: narrator), straight from the blueprint (no
        // dub / TTS needed) — lets the capture page build its phrase cache standalone. Returns each scene's
        // line texts for that character + whether the scene also has another speaker (those scenes aren't
        // capture material — mixed dialogue would bleed into the recording).
        app.MapGet("/api/projects/{id}/voice-capture/narrator-lines", GetProjectsIdVoiceCaptureNarratorLines);
        // Voice-capture phrase cache (per project, computed once per book): the confident STT-verified
        // dialogue phrases used by the capture UI and by the dub overlay's line↔window mapping.
        app.MapGet("/api/projects/{id}/voice-capture/phrases", GetProjectsIdVoiceCapturePhrases);
        app.MapPost("/api/projects/{id}/voice-capture/phrases", PostProjectsIdVoiceCapturePhrases);
        return app;
    }

    private static async Task<IResult> GetProjectsIdVoiceAlignment(string id,
    VoiceAlignmentStore alignmentStore,
    CancellationToken ct)
    {
    var alignment = await alignmentStore.LoadAsync(id, ct);
    return Results.Ok(new { ok = true, alignment });
}

    private static async Task<IResult> PostProjectsIdVoiceAlignmentTimestamps(string id,
    List<ClipTimestampUpdate> updates,
    VoiceAlignmentStore alignmentStore,
    CancellationToken ct)
    {
    if (updates is null || updates.Count == 0)
        return Results.BadRequest(new { ok = false, error = "no updates" });

    var alignment = await alignmentStore.LoadAsync(id, ct);
    if (alignment is null)
        return Results.BadRequest(new { ok = false, error = "no alignment to update — run voice substitution first" });

    var applied = 0;
    foreach (var u in updates)
    {
        var clip = alignment.Find(u.Scene, u.Clip);
        if (clip is null) continue;
        VoiceAlignmentStore.ApplyTimestamps(clip, u);
        applied++;
    }

    await alignmentStore.SaveAsync(id, alignment, ct);
    return Results.Ok(new { ok = true, clipsUpdated = applied });
}

    private static IResult PostProjectsIdCharactersCharKeyVoice(string id, string charKey, UpdateCharacterVoiceRequest? body, ProjectStore store)
    {
    try
    {
        body ??= new UpdateCharacterVoiceRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        store.UpdateCharacterSeedText(
            id,
            charKey,
            voiceProfile: body.VoiceProfile,
            voiceLabel: body.VoiceLabel);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            message = "Voice seed updated",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyVoiceCloneSample(string id,
    string charKey,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required (field: file)" });

        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No audio file (field: file)" });
        if (file.Length > ApiEndpointHelpers.VoiceSampleBytes)
            return Results.BadRequest(new { ok = false, error = "Audio too large (max 15 MB)." });

        await using var stream = file.OpenReadStream();
        var path = await store.SaveVoiceCloneSampleAsync(id, charKey, stream, file.FileName, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            fileName = Path.GetFileName(path),
            url = $"/api/projects/{Uri.EscapeDataString(id)}/characters/{Uri.EscapeDataString(charKey)}/voice/clone-sample",
            message = "Voice clone sample saved — optional add-on template for personal voice.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdCharactersCharKeyVoiceCloneSample(string id, string charKey, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        var path = store.GetVoiceCloneSamplePath(id, charKey);
        if (!File.Exists(path))
            return Results.NotFound(new { ok = false, error = "No voice clone sample yet." });
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => SpecializedMimeType.AudioMpeg.ToMimeTypeString(),
            ".wav" => SpecializedMimeType.AudioWav.ToMimeTypeString(),
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "audio/webm",
        };
        return Results.File(path, contentType, enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> DeleteProjectsIdCharactersCharKeyVoiceCloneSample(string id, string charKey, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        var removed = store.DeleteVoiceCloneSample(id, charKey);
        return Results.Ok(new { ok = true, removed, projectId = id, charKey });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyVoiceClone(string id,
    string charKey,
    CloneVoiceApiRequest? body,
    VoiceCloneApplyService apply,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        // Unified router: catalog voice_model_name (or body.Model) → Fal MiniMax or ElevenLabs.
        var result = await apply.ApplyFromSampleAsync(
            id, charKey, modelOverride: body?.Model, ct: ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            voiceId = result.ProviderVoiceId,
            providerId = result.ProviderId,
            modelId = result.ModelId,
            usedMock = result.UsedMock,
            estimatedUsd = result.EstimatedCloneUsd,
            previewUrl = result.PreviewUrl,
            message = result.Message ?? "Voice cloned — reused for narration until the sample is replaced.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyVoiceSpeak(string id,
    string charKey,
    SpeakVoiceApiRequest? body,
    ProjectStore store,
    IVoiceCloneClient voiceClone,
    IVoiceClient voiceClient,
    IHttpClientFactory httpFactory,
    MediaProxyTicketStore tickets,
    ProjectTelemetryService telemetry,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        var text = body?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { ok = false, error = "text required" });
        return await ExecuteSpeakAsync(id, charKey, text, body, new SpeakRuntime
        {
            Store = store,
            VoiceClone = voiceClone,
            VoiceClient = voiceClient,
            HttpFactory = httpFactory,
            Tickets = tickets,
            Telemetry = telemetry,
        }, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private sealed class SpeakRuntime
    {
        public required ProjectStore Store { get; init; }
        public required IVoiceCloneClient VoiceClone { get; init; }
        public required IVoiceClient VoiceClient { get; init; }
        public required IHttpClientFactory HttpFactory { get; init; }
        public required MediaProxyTicketStore Tickets { get; init; }
        public required ProjectTelemetryService Telemetry { get; init; }
    }

    private sealed class SpeakSynthResult
    {
        public byte[]? AudioBytes { get; set; }
        public string ContentType { get; set; } = SpecializedMimeType.AudioMpeg.ToMimeTypeString();
        public string FileExt { get; set; } = ".mp3";
        public string? ClientUrl { get; set; }
        public string? Error { get; set; }
        public bool UsedMock { get; set; }
        public IResult? EarlyError { get; set; }
    }

    private static async Task<IResult> ExecuteSpeakAsync(
        string id,
        string charKey,
        string text,
        SpeakVoiceApiRequest? body,
        SpeakRuntime runtime,
        CancellationToken ct)
    {
        var voiceId = body?.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            voiceId = runtime.Store.GetVoiceCloneProviderId(id, charKey);
        if (string.IsNullOrWhiteSpace(voiceId))
            return Results.BadRequest(new { ok = false, error = "No cloned voice yet — record and apply a voice sample first." });

        var seedProvider = runtime.Store.GetVoiceProviderId(id, charKey) ?? "";
        var (entry, model) = await ResolveSpeakCatalogEntryAsync(id, body?.Model, seedProvider, runtime.Store, ct);
        var maxLen = entry?.MaxPromptLength ?? 5000;
        if (text.Length > maxLen)
            return Results.BadRequest(new { ok = false, error = $"Text is {text.Length} characters — this voice model's limit is {maxLen} per call. Split into multiple calls." });

        var providerId = entry?.ProviderId
                         ?? (string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider)
                         ?? "unknown";
        var useEleven = providerId.Equals(ApiText.ElevenLabsClient, StringComparison.OrdinalIgnoreCase)
                        || (entry?.Provider == ModelProviderFamily.ElevenLabs)
                        || voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase);

        var synth = useEleven
            ? await SpeakWithElevenLabsAsync(runtime.VoiceClient, voiceId, text, entry, model, ct)
            : await SpeakWithFalAsync(runtime, voiceId, text, entry, model, ct);
        if (synth.EarlyError is not null)
            return synth.EarlyError;

        var estimatedUsd = entry?.CostPerThousandCharsUsd is { } rate
            ? Math.Round(rate * text.Length / 1000.0, 4)
            : (double?)null;
        var ok = synth.AudioBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(synth.ClientUrl);
        await runtime.Telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = id,
            Kind = "tts",
            Mode = "dialogue_tts",
            Model = entry?.Id ?? model,
            Provider = providerId,
            CharKey = charKey,
            PromptChars = text.Length,
            EstimatedUsd = estimatedUsd,
            Ok = ok,
            Error = ok ? null : synth.Error ?? "Speech synthesis failed",
        }, ct);

        if (!ok)
            return Results.BadRequest(new { ok = false, error = synth.Error ?? "Speech synthesis failed" });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            voiceId,
            clientUrl = synth.ClientUrl,
            audioBase64 = synth.AudioBytes is { Length: > 0 } ? Convert.ToBase64String(synth.AudioBytes) : null,
            contentType = synth.ContentType,
            fileExtension = synth.FileExt,
            characterCount = text.Length,
            estimatedUsd,
            usedMock = synth.UsedMock,
            message = "Narration audio ready.",
        });
    }

    private static async Task<(SupportedModelEntry? Entry, string? Model)> ResolveSpeakCatalogEntryAsync(
        string projectId, string? model, string seedProvider, ProjectStore store, CancellationToken ct)
    {
        // Prefer seed provider (who created the clone) so we don't TTS with the wrong stack.
        if (string.IsNullOrWhiteSpace(model))
        {
            var cfg = await store.GetConfigAsync(projectId, ct);
            if (cfg.TryGetValue("voice_model_name", out var vm) && vm.ValueKind == JsonValueKind.String)
                model = vm.GetString();
        }

        // Resolve speak-shaped catalog entry (not the clone step).
        SupportedModelEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(model))
            entry = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is { IsVoiceCloneStep: true })
        {
            // User selected the clone model — pair to same-provider speak model.
            entry = FindEnabledSpeakModel(entry.ProviderId);
            model = entry?.Id;
        }
        if (entry is null)
        {
            // Infer from seed provider id.
            entry = FindEnabledSpeakModel(string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider, matchAnyIfNoProvider: true);
            model = entry?.Id ?? model;
        }
        return (entry, model);
    }

    private static SupportedModelEntry? FindEnabledSpeakModel(string? providerId, bool matchAnyIfNoProvider = false) =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                (matchAnyIfNoProvider
                    ? string.IsNullOrWhiteSpace(providerId) ||
                      string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)));

    private static async Task<SpeakSynthResult> SpeakWithElevenLabsAsync(
        IVoiceClient voiceClient, string voiceId, string text, SupportedModelEntry? entry, string? model, CancellationToken ct)
    {
        var result = new SpeakSynthResult();
        if (!voiceClient.IsConfigured && !voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase))
        {
            result.EarlyError = Results.BadRequest(new { ok = false, error = "ElevenLabs key is not configured. Open Settings → Voice." });
            return result;
        }
        var speakModelId = entry?.Id
                           ?? SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice)?.Id
                           ?? model
                           ?? "eleven_multilingual_v2";
        var tts = await voiceClient.TextToSpeechAsync(voiceId, text, speakModelId, ct);
        if (!tts.Ok || tts.AudioBytes is not { Length: > 0 })
        {
            result.Error = tts.Error ?? "Speech synthesis failed";
            return result;
        }
        result.AudioBytes = tts.AudioBytes;
        result.ContentType = tts.ContentType ?? SpecializedMimeType.AudioMpeg.ToMimeTypeString();
        result.FileExt = tts.FileExtension ?? ".mp3";
        result.UsedMock = tts.UsedMock;
        return result;
    }

    private static async Task<SpeakSynthResult> SpeakWithFalAsync(
        SpeakRuntime runtime,
        string voiceId,
        string text,
        SupportedModelEntry? entry,
        string? model,
        CancellationToken ct)
    {
        var result = new SpeakSynthResult();
        if (!runtime.VoiceClone.IsConfigured)
        {
            result.EarlyError = Results.BadRequest(new { ok = false, error = "Connect a voice service (Fal) in Settings for MiniMax speech." });
            return result;
        }
        var speakModelId = entry?.Id
                           ?? SupportedModelCatalog.Find("fal-ai/minimax/speech-02-hd", ModelCapability.Voice)?.Id
                           ?? model;
        var audioUrl = await runtime.VoiceClone.SynthesizeSpeechAsync(text, voiceId, speakModelId, ct);
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            result.Error = "Speech synthesis failed — see server logs.";
            return result;
        }
        await TryDownloadOrProxySpeechAsync(runtime.HttpFactory, runtime.Tickets, audioUrl, result, ct);
        return result;
    }

    private static async Task TryDownloadOrProxySpeechAsync(
        IHttpClientFactory httpFactory, MediaProxyTicketStore tickets, string audioUrl, SpeakSynthResult result, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient();
            using var resp = await http.GetAsync(audioUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                result.AudioBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                result.ContentType = resp.Content.Headers.ContentType?.MediaType ?? SpecializedMimeType.AudioMpeg.ToMimeTypeString();
                return;
            }
            // Fall back to proxy URL if download fails
            result.ClientUrl = $"/api/media/proxy/{tickets.Issue(audioUrl, TimeSpan.FromMinutes(45))}";
        }
        catch
        {
            result.ClientUrl = $"/api/media/proxy/{tickets.Issue(audioUrl, TimeSpan.FromMinutes(45))}";
        }
    }

    private static async Task<IResult> PostProjectsIdMediaLipSync(string id,
    HttpRequest req,
    ProjectStore store,
    ILipSyncClient lipSync,
    MediaProxyTicketStore tickets,
    ProjectTelemetryService telemetry,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form required (fields: video, audio)" });

    string? videoTemp = null;
    string? audioTemp = null;
    try
    {
        return await RunLipSyncAsync(id, req, new LipSyncRuntime
        {
            Store = store,
            LipSync = lipSync,
            Tickets = tickets,
            Telemetry = telemetry,
        }, ct, v => videoTemp = v, a => audioTemp = a);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
    finally
    {
        DeleteTempFiles(videoTemp, audioTemp);
    }
}

    private sealed class LipSyncRuntime
    {
        public required ProjectStore Store { get; init; }
        public required ILipSyncClient LipSync { get; init; }
        public required MediaProxyTicketStore Tickets { get; init; }
        public required ProjectTelemetryService Telemetry { get; init; }
    }

    private static async Task<IResult> RunLipSyncAsync(
        string id,
        HttpRequest req,
        LipSyncRuntime runtime,
        CancellationToken ct,
        Action<string> setVideoTemp,
        Action<string> setAudioTemp)
    {
        await runtime.Store.RequireProjectAsync(id, ct);
        if (!runtime.LipSync.IsConfigured)
            return Results.BadRequest(new { ok = false, error = "Connect a lip-sync service (FAL_API_KEY) in Configuration." });

        var form = await req.ReadFormAsync(ct);
        var videoFile = form.Files.GetFile(ApiText.VideoFolder);
        var audioFile = form.Files.GetFile("audio");
        if (videoFile is null || videoFile.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No video file (field: video)" });
        if (audioFile is null || audioFile.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No audio file (field: audio)" });

        var model = form["model"].FirstOrDefault();
        var syncMode = form["syncMode"].FirstOrDefault();
        var entry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.LipSync);

        var videoTemp = Path.Combine(Path.GetTempPath(), $"lipsync_video_{Guid.NewGuid():N}{Path.GetExtension(videoFile.FileName)}");
        var audioTemp = Path.Combine(Path.GetTempPath(), $"lipsync_audio_{Guid.NewGuid():N}{Path.GetExtension(audioFile.FileName)}");
        setVideoTemp(videoTemp);
        setAudioTemp(audioTemp);
        await using (var vfs = File.Create(videoTemp))
            await videoFile.CopyToAsync(vfs, ct);
        await using (var afs = File.Create(audioTemp))
            await audioFile.CopyToAsync(afs, ct);

        var resultUrl = await runtime.LipSync.GenerateLipSyncAsync(
            videoTemp, audioTemp, model,
            string.IsNullOrWhiteSpace(syncMode) ? "cut_off" : syncMode,
            onProgress: null, ct);
        await runtime.Telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = id,
            Kind = "lip_sync",
            Model = entry.Id,
            Provider = entry.ProviderId,
            Ok = !string.IsNullOrWhiteSpace(resultUrl),
            Error = string.IsNullOrWhiteSpace(resultUrl) ? "Lip-sync failed" : null,
        }, ct);
        if (string.IsNullOrWhiteSpace(resultUrl))
            return Results.BadRequest(new { ok = false, error = "Lip-sync failed — see server logs." });

        var ticket = runtime.Tickets.Issue(resultUrl, TimeSpan.FromMinutes(45));
        var clientUrl = $"/api/media/proxy/{ticket}";

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            clientUrl,
            model = entry.Id,
            costPerMinuteUsd = entry.CostPerMinuteUsd,
            message = "Lip-synced clip ready.",
        });
    }

    private static void DeleteTempFiles(params string?[] paths)
    {
        foreach (var tmp in paths)
        {
            if (tmp is null) continue;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static async Task<IResult> GetVoices(IVoiceClient voices, CancellationToken ct)
    {
    try
    {
        var list = await voices.ListVoicesAsync(ct);
        return Results.Ok(new
        {
            ok = true,
            provider = voices.ProviderId,
            configured = voices.IsConfigured,
            voices = list,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyVoiceApplyClone(string id,
    string charKey,
    VoiceCloneApplyService apply,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        var result = await apply.ApplyFromSampleAsync(id, charKey, ct: ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            providerId = result.ProviderId,
            providerVoiceId = result.ProviderVoiceId,
            voiceId = result.ProviderVoiceId,
            modelId = result.ModelId,
            usedMock = result.UsedMock,
            voiceLabel = result.VoiceLabel,
            previewUrl = result.PreviewUrl,
            previewRelativePath = result.PreviewRelativePath,
            estimatedUsd = result.EstimatedCloneUsd,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> PostProjectsIdCharactersCharKeyVoiceApplyCatalog(string id,
    string charKey,
    ApplyCatalogVoiceRequest? body,
    VoiceCloneApplyService apply,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        body ??= new ApplyCatalogVoiceRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        if (string.IsNullOrWhiteSpace(body.ProviderVoiceId))
            return Results.BadRequest(new { ok = false, error = "providerVoiceId required" });
        var result = await apply.ApplyCatalogVoiceAsync(
            id, charKey, body.ProviderVoiceId, body.DisplayName, body.PreviewText, ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            providerId = result.ProviderId,
            providerVoiceId = result.ProviderVoiceId,
            voiceId = result.ProviderVoiceId,
            usedMock = result.UsedMock,
            voiceLabel = result.VoiceLabel,
            previewUrl = result.PreviewUrl,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdCharactersCharKeyVoiceTtsPreview(string id, string charKey, VoiceCloneApplyService apply)
    {
    try
    {
        var path = apply.GetTtsPreviewPath(id, charKey);
        if (path is null || !File.Exists(path))
            return Results.NotFound(new { ok = false, error = "No TTS preview yet — run apply-clone first." });
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => SpecializedMimeType.AudioMpeg.ToMimeTypeString(),
            ".wav" => SpecializedMimeType.AudioWav.ToMimeTypeString(),
            ".m4a" => "audio/mp4",
            _ => SpecializedMimeType.ApplicationOctetStream.ToMimeTypeString(),
        };
        return Results.File(path, contentType, Path.GetFileName(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdCharactersCharKeyVoiceAudioStatus(string id,
    string charKey,
    string? voiceProfile,
    string? voiceLabel,
    string? sampleText,
    VoicePreviewService voices)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        var info = voices.GetCacheInfo(id, charKey, voiceProfile, voiceLabel, sampleText, displayName: null);
        return Results.Ok(new VoicePreviewStatusDto
        {
            Ok = true,
            Exists = info.Exists,
            Matches = info.Matches,
            Fingerprint = info.Fingerprint,
            GeneratedAt = info.GeneratedAt,
            ContentType = info.ContentType,
            AudioUrl = info.Exists
                ? $"/api/projects/{Uri.EscapeDataString(id)}/characters/{Uri.EscapeDataString(charKey)}/voice/audio"
                : null,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static IResult GetProjectsIdCharactersCharKeyVoiceAudio(string id,
    string charKey,
    VoicePreviewService voices)
    {
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = ApiText.CharKeyRequired });
        var path = voices.GetSampleMediaPath(id, charKey);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "No voice sample yet — generate one first." });
        var isMp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        var contentType = isMp3 ? SpecializedMimeType.AudioMpeg.ToMimeTypeString() : SpecializedMimeType.VideoMp4.ToMimeTypeString();
        var fileName = isMp3 ? $"{charKey}_voice.mp3" : $"{charKey}_voice.mp4";
        return Results.File(path, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

    private static async Task<IResult> GetProjectsIdVoiceCaptureNarratorLines(string id, string? charKey, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    using var blueprint = await store.LoadBlueprintAsync(id, ct);
    if (blueprint is null)
        return Results.Ok(new { ok = true, scenes = Array.Empty<object>() });

    var targetKey = string.IsNullOrWhiteSpace(charKey) ? null : charKey.Trim();
    bool IsTarget(string? spk)
    {
        if (string.IsNullOrWhiteSpace(spk)) return false;
        // Explicit character key: exact match — this is a deliberate user pick, not a guess.
        if (targetKey is not null)
            return string.Equals(spk.Trim(), targetKey, StringComparison.OrdinalIgnoreCase);
        // Default (no key given): the original narrator heuristic.
        return string.Equals(spk.Trim(), "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
               spk.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    var all = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null);
    var scenesWithOther = new HashSet<int>();
    foreach (var cl in all.Where(c => c.Lines.Any(l => !IsTarget(l.CharacterKey))))
        scenesWithOther.Add(cl.Scene);

    var byScene = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, IsTarget)
        .GroupBy(c => c.Scene)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            scene = g.Key,
            hasOtherSpeakers = scenesWithOther.Contains(g.Key),
            lines = g.OrderBy(c => c.Clip)
                .SelectMany(c => c.Lines)
                .Select(l => l.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList(),
        })
        .ToList();

    return Results.Ok(new { ok = true, scenes = byScene });
}

    private static async Task<IResult> GetProjectsIdVoiceCapturePhrases(string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var path = Path.Combine(await store.GetProjectDirAsync(id, ct), ApiText.AssetsFolder, "voice_capture", "phrases.json");
    if (!File.Exists(path))
        return Results.Ok(new { ok = true, phrases = (VoiceCapturePhrases?)null });
    try
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<VoiceCapturePhrases>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Results.Ok(new { ok = true, phrases = data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}

    private static async Task<IResult> PostProjectsIdVoiceCapturePhrases(string id, VoiceCapturePhrases body, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct)
    {
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (body is null)
        return Results.BadRequest(new { ok = false, error = "phrases body required" });
    var dir = Path.Combine(await store.GetProjectDirAsync(id, ct), ApiText.AssetsFolder, "voice_capture");
    Directory.CreateDirectory(dir);
    body.ProjectId = id;
    body.GeneratedAtUtc = DateTime.UtcNow;
    var writeOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    await File.WriteAllTextAsync(
        Path.Combine(dir, "phrases.json"),
        System.Text.Json.JsonSerializer.Serialize(body, writeOpts) + "\n", ct);
    return Results.Ok(new { ok = true, count = body.Phrases?.Count ?? 0 });
}
}
