namespace PageToMovie.Engine.Abstractions;

/// <summary>Video generate / poll / download. Grok, Gemini (Veo), or fake — see MultiProviderVideoClient.</summary>
public interface IVideoClient
{
    bool IsConfigured { get; }

    /// <param name="referenceImagePaths">
    /// Character/style refs for reference-to-video (<c>reference_images</c>).
    /// Prompt should use <c><IMAGE_1></c>… tags.
    /// Mutually exclusive with start-frame / video-continue modes.
    /// </param>
    /// <param name="startFrameImagePath">
    /// Optional still used as the first frame (image-to-video).
    /// Prefer <paramref name="continueFromVideoPath"/> for true clip-to-clip continue.
    /// </param>
    /// <param name="continueFromVideoPath">
    /// Local path to previous clip video. Uses Imagine <c>/videos/extensions</c>
    /// (continue from last frame with the new prompt). Result is prev+extension;
    /// caller should trim the new portion for a per-clip file.
    /// </param>
    Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null,
        string? aspectRatio = null);

    Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct);

    Task DownloadToFileAsync(string url, string destPath, CancellationToken ct);

    /// <summary>
    /// The provider's own re-fetchable file reference (Files-API-style id + unix expiry) for a
    /// request that already completed via <see cref="PollForVideoUrlAsync"/> — populated only when
    /// the provider/model requested persistent storage at submit time (currently: Grok only, when
    /// storage was requested). Returns (null, null) when unsupported, storage wasn't requested, or
    /// the request id is unknown/already evicted. Callers persist this in the clip sidecar to allow
    /// a later prompt-based video edit to reuse the file instead of re-uploading — purely an
    /// optimization; the local on-disk clip file is always the fallback source of truth.
    /// </summary>
    (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId);
}

/// <summary>Image generate / edit. Grok, Gemini, or fake — see MultiProviderImageClient.</summary>
public interface IImageClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}

// IChatClient + ChatCallModes live in PageToMovie.Core.Abstractions so Adaptation
// can depend on chat without referencing Engine. Engine re-exports via GlobalUsings.

/// <summary>
/// Vision (transcribe / classify / multi-image completion). Grok, or fake; Anthropic and Gemini
/// only implement <see cref="CompleteWithImagesAsync"/> — see MultiProviderVisionClient.
/// </summary>
public interface IVisionClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// True when this client can serve <paramref name="model"/> (that model's provider key is present).
    /// Single-provider clients default to <see cref="IsConfigured"/>. Multi-provider facades resolve
    /// the catalog row and check that provider only — not "any vision key".
    /// </summary>
    bool IsConfiguredFor(string? model) => IsConfigured;

    Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "",
        CancellationToken ct = default);

    Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "",
        CancellationToken ct = default);

    /// <summary>
    /// Multi-image vision completion (clip auto-review: prev tail + current frames).
    /// Returns model text (JSON expected by caller).
    /// </summary>
    Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default);
}

/// <summary>
/// Native multimodal video+audio analysis — Gemini specifically can inline a raw video file
/// (with its audio track) for the model to watch/listen to, unlike other vision providers which
/// only accept still images (see ClipDialogueVerificationService, the only caller: it needs
/// Gemini's real video capability specifically, not whatever <see cref="IVisionClient"/>'s
/// routing config currently points at). Kept as its own interface rather than reusing
/// <see cref="IVisionClient"/> so a caller that specifically needs Gemini can depend on a
/// distinct DI seam (GeminiChatClient implements both) — and so fakes mode has something to
/// fake instead of this silently resolving to null.
/// </summary>
public interface IGeminiVideoAnalysisClient
{
    bool IsConfigured { get; }

    Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default);
}

/// <summary>Audio & music generate / download (Fal.ai, Suno resellers, or fake).</summary>
public interface IAudioClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the provider's audio URL, not downloaded bytes — mirrors
    /// IVideoClient.PollForVideoUrlAsync so the caller can hand it straight to
    /// MediaProxyTicketStore/ClientMediaUrl for client-side download, the same as video. The key
    /// never leaves the API host; only the short-lived provider URL does. Duration limits live on
    /// SupportedModelEntry.MaxAudioDurationSeconds (per-model, resolved by the caller via
    /// SupportedModelCatalog) rather than on this interface — a fixed per-client value doesn't
    /// generalize once <paramref name="model"/> selects the provider per call.
    /// </summary>
    /// <param name="onProgress">
    /// Optional status callback for slow providers (Suno-family generation runs 1-3+ minutes via
    /// internal polling) so the caller can surface progress in a job log. Fast providers (Fal.ai)
    /// may ignore it.
    /// </param>
    /// <param name="isVocal">
    /// True to request sung vocals instead of an instrumental track. Only the Suno-family providers
    /// (SunoClient, AiMusicApiClient) can honor this — Fal.ai Stable Audio has no vocal capability and
    /// always generates instrumental regardless of this flag.
    /// </param>
    /// <param name="lyrics">Lyrics text to sing, required by Suno-family providers when <paramref name="isVocal"/> is true; ignored otherwise.</param>
    Task<string?> GenerateMusicTrackAsync(
        string prompt,
        int durationSeconds,
        string? model = null,
        CancellationToken ct = default,
        Action<string>? onProgress = null,
        bool isVocal = false,
        string? lyrics = null);
}

/// <summary>
/// Video lip-sync: given a finished clip and a separate dialogue/narration audio track, produce a
/// new video whose mouth movement matches that audio (Fal.ai / Sync Labs). A genuinely different
/// shape from <see cref="IVideoClient"/> (video+audio in, video out — not a text prompt), so it gets
/// its own interface rather than overloading video generation. Same request/response contract as
/// <see cref="IAudioClient.GenerateMusicTrackAsync"/> (single awaitable call, provider URL out, no
/// bytes downloaded server-side) since the underlying provider call — while queue-based — completes
/// in the time span of a single HTTP request for a short clip, unlike multi-minute video generation.
/// Explicit, human-triggered only (per-clip action) — must never be wired into any automatic job or
/// pipeline stage; every call spends real provider money.
/// </summary>
public interface ILipSyncClient
{
    bool IsConfigured { get; }

    /// <param name="videoPath">Local path to the source clip (already on the API host — callers upload it first, same on-demand transfer pattern as scene-continuation video).</param>
    /// <param name="audioPath">Local path to the dialogue/narration audio track to sync to.</param>
    /// <param name="model">Catalog model id (<see cref="PageToMovie.Core.Models.ModelCapability.LipSync"/>). Null resolves the catalog default.</param>
    /// <param name="syncMode">How to reconcile a video/audio duration mismatch: <c>cut_off</c> (default), <c>loop</c>, <c>bounce</c>, <c>silence</c>, or <c>remap</c> — passed straight through to the provider.</param>
    /// <returns>Provider URL for the resulting lip-synced video, or null if the call failed.</returns>
    Task<string?> GenerateLipSyncAsync(
        string videoPath,
        string audioPath,
        string? model = null,
        string syncMode = "cut_off",
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Prompt-based edit of an already-generated clip via xAI's /v1/videos/edits — video+text-prompt
/// in, video out, output inherits the source clip's duration/resolution (not customizable), input
/// capped at 8.7s by xAI. Submit+poll internally (edit processing time is not guaranteed short
/// just because input is short — reuses the same poll budget as full video generation, unlike
/// <see cref="ILipSyncClient"/>'s single-request-span assumption). Explicit, human-triggered only,
/// run as its own FilmJobService job kind — must never be wired into the automatic scene/batch
/// generation pipeline; every call spends real provider money.
/// </summary>
public interface IVideoEditClient
{
    bool IsConfigured { get; }

    /// <param name="videoPath">Local path to the source clip (already on the API host) — always
    /// required, since this is the guaranteed fallback input regardless of <paramref name="sourceFileId"/>.</param>
    /// <param name="prompt">Edit instruction text.</param>
    /// <param name="sourceFileId">A previously-stored xAI Files API file_id for this exact clip, if
    /// the sidecar has one and it hasn't passed its recorded expiry. Tried first; any failure
    /// (missing/expired/rejected) transparently falls back to uploading <paramref name="videoPath"/>
    /// as a base64 data URI — the caller never needs to handle the fallback itself.</param>
    /// <param name="model">Catalog model id (<see cref="PageToMovie.Core.Models.ModelCapability.VideoEdit"/>). Null resolves catalog default.</param>
    /// <returns>Provider URL for the edited video.</returns>
    /// <exception cref="Exception">On any unrecoverable failure (throws rather than returning
    /// null — this runs as its own <c>FilmJobService</c> job kind, so the caller wants the real
    /// exception message surfaced to the job log/error field, the same convention every other
    /// <c>Run*Async</c> job method uses; unlike <see cref="ILipSyncClient"/>'s direct-endpoint
    /// caller, which prefers a null return it can turn into a friendly <c>ok:false</c> response).</exception>
    Task<string> EditClipAsync(
        string videoPath,
        string prompt,
        string? sourceFileId = null,
        string? model = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads the edited video from the URL <see cref="EditClipAsync"/> returned. A separate
    /// method (mirroring <see cref="IVideoClient.DownloadToFileAsync"/>) rather than a raw HTTP GET
    /// in the caller, because a fake implementation's "URL" isn't necessarily a real http(s)
    /// address — <c>FakeGrokVideoClient.DownloadToFileAsync</c> is the existing precedent for
    /// resolving a fake-fixture reference to local bytes instead of attempting a real request.
    /// </summary>
    Task DownloadToFileAsync(string url, string destPath, CancellationToken ct);
}

/// <summary>
/// Voice-clone narration/dialogue: clone a voice from a short reference sample, then synthesize
/// speech audio for arbitrary text in that voice (Fal.ai / MiniMax). Two explicit steps mirroring
/// the underlying provider's own API shape (clone once → reuse the returned voice id across many
/// speak calls) rather than one combined call, so a caller only pays the (larger, one-time) clone
/// cost once per sample. Deliberately its own interface, not folded into <see cref="IAudioClient"/>:
/// that interface is shaped for one text-prompt-to-music-track call, while this is a stateful
/// two-step "identity, then arbitrary text" flow closer to <see cref="IVideoClient"/>'s
/// reference-image pattern than to music generation. Explicit, human-triggered only — never runs
/// automatically as part of any job/pipeline; every call spends real provider money.
/// </summary>
public interface IVoiceCloneClient
{
    bool IsConfigured { get; }

    /// <param name="sampleAudioPath">Local path to the reference voice sample (provider requires >=10s of clean speech).</param>
    /// <param name="model">Catalog model id for the clone step. Null resolves the catalog default clone-shaped model.</param>
    /// <returns>Provider voice id (e.g. MiniMax <c>custom_voice_id</c>) to pass to <see cref="SynthesizeSpeechAsync"/>, or null if the call failed.</returns>
    Task<string?> CloneVoiceAsync(
        string sampleAudioPath,
        string? model = null,
        CancellationToken ct = default);

    /// <param name="text">Narration/dialogue text to speak. Callers should check the resolved model's <c>MaxPromptLength</c> (catalog) before calling — long-form narration (e.g. a whole book chapter) is the caller's responsibility to split into multiple calls; this client does not chunk automatically.</param>
    /// <param name="voiceId">Provider voice id previously returned by <see cref="CloneVoiceAsync"/>.</param>
    /// <param name="model">Catalog model id for the speak step. Null resolves the catalog default speak-shaped model.</param>
    /// <returns>Provider URL for the synthesized speech audio, or null if the call failed.</returns>
    Task<string?> SynthesizeSpeechAsync(
        string text,
        string voiceId,
        string? model = null,
        CancellationToken ct = default);
}
