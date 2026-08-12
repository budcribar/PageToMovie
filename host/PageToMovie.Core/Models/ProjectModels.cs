

using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectVisibility
{
    Private = 0,
    Public = 1,
    Unlisted = 2
}

public static class ProjectVisibilityExtensions
{
    public static ProjectVisibility ParseProjectVisibility(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ProjectVisibility.Private;
        return value.Trim().ToLowerInvariant() switch
        {
            "public" or "open" or "publicforkable" or "forkable" => ProjectVisibility.Public,
            "unlisted" => ProjectVisibility.Unlisted,
            "private" => ProjectVisibility.Private,
            _ => Enum.TryParse<ProjectVisibility>(value, true, out var result) ? result : ProjectVisibility.Private
        };
    }

    public static string ToClientString(this ProjectVisibility visibility) => visibility switch
    {
        ProjectVisibility.Public => "Public",
        ProjectVisibility.Unlisted => "Unlisted",
        _ => "Private"
    };
}

public sealed class ProjectInfo
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public string? Title { get; set; }
    public string Path { get; set; } = "";
    /// <summary>
    /// Owning user id from project.json (ownerUserId). Null/empty = legacy unowned project
    /// (admin-only for public demo publish).
    /// </summary>
    public string? OwnerUserId { get; set; }
    /// <summary>Set when this project was created via Invite-to-Fork — the project it was forked from.</summary>
    public string? ParentProjectId { get; set; }
    /// <summary>
    /// Git-aligned visibility mode: Private (owner/collaborators only), Public (Read-Only / listed on gallery), or Unlisted. Default: Private.
    /// </summary>
    public ProjectVisibility VisibilityMode { get; set; } = ProjectVisibility.Private;
    /// <summary>
    /// Product path: Full (cast/faces/estimate) or SimpleVoice (library book + narrator re-voice).
    /// Stored in project.json as studioPath.
    /// </summary>
    public StudioPath StudioPath { get; set; } = StudioPath.Full;
}

/// <summary>Known values and helpers for <see cref="ProjectInfo.StudioPath"/>.</summary>
public static class ProjectStudioPaths
{
    public const StudioPath Full = StudioPath.Full;
    public const StudioPath SimpleVoice = StudioPath.SimpleVoice;

    public static StudioPath Normalize(string? value) =>
        string.Equals(value?.Trim(), "simple-voice", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), nameof(StudioPath.SimpleVoice), StringComparison.OrdinalIgnoreCase)
            ? StudioPath.SimpleVoice
            : StudioPath.Full;

    public static StudioPath Normalize(StudioPath path) => path;

    public static string ToSerializedString(StudioPath path) =>
        path == StudioPath.SimpleVoice ? "simple-voice" : "full";

    public static bool IsSimpleVoice(StudioPath path) => path == StudioPath.SimpleVoice;
    public static bool IsSimpleVoice(string? value) => Normalize(value) == StudioPath.SimpleVoice;
}

public sealed class WorkspaceState
{
    public string? ActiveProject { get; set; }
}

public sealed class OpenFolderRequest
{
    public string? Path { get; set; }
    public string? ProjectId { get; set; }
}

public sealed class OpenFolderResponse
{
    public bool Ok { get; set; }
    public string? Opened { get; set; }
    public string? Error { get; set; }
}

public sealed class OpenEditorRequest
{
    public string? ProjectId { get; set; }
    public int? SceneNumber { get; set; }
    public int? ClipNumber { get; set; }
    public string? EditorName { get; set; }
}

public sealed class OpenEditorResponse
{
    public bool Ok { get; set; }
    public bool IsRemote { get; set; }
    public string? Opened { get; set; }
    public string? Editor { get; set; }
    public string? VideoUrl { get; set; }
    public string? Error { get; set; }
}

public sealed class JobSnapshot
{
    /// <summary>Multi-job id. Empty when idle / not yet assigned.</summary>
    public string? JobId { get; set; }
    public string Status { get; set; } = "idle"; // idle|running|done|error|cancelled
    public string? Kind { get; set; }
    public string? Message { get; set; }
    public string? ProjectId { get; set; }
    public string? UserId { get; set; }
    public string? CharKey { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>True when status indicates job processing is finished (done, partial, error, cancelled, idle).</summary>
    public bool IsFinished => Status is "done" or "partial" or "error" or "cancelled" or "idle";

    /// <summary>True when job completed successfully (done or partial).</summary>
    public bool IsSuccess => Status is "done" or "partial";

    /// <summary>
    /// Same-origin proxy path for client to download gen output (e.g. /api/media/proxy/{token}).
    /// Set when bytes should be saved to the user's media folder instead of server disk.
    /// </summary>
    public string? ClientMediaUrl { get; set; }
    /// <summary>Project-relative path under the client media folder, e.g. assets/video/scene_01_clip_01.mp4.</summary>
    public string? ClientRelativePath { get; set; }
    /// <summary>For Kind="music": one id shared by every segment of the same generation run, so the
    /// client archives all of a take's segments under the same take-history timestamp instead of each
    /// segment computing its own independently (they can be minutes apart — real provider polling
    /// happens between segments). Minted once in FilmJobService.RunSceneMusicGenAsync.</summary>
    public string? MusicTakeId { get; set; }
}

/// <summary>Helpers for multi-job lists (Phase F).</summary>
public static class JobListHelpers
{
    /// <summary>Prefer a running job; else most recently finished/queued.</summary>
    public static JobSnapshot? PickPrimary(IReadOnlyList<JobSnapshot>? jobs)
    {
        if (jobs is null || jobs.Count == 0) return null;
        // Prefer active work over finished: running → queued → newest finished.
        // Without "queued", a second generate briefly surfaces the previous done job
        // (Index==Total) and the progress bar looks stuck full.
        var running = jobs
            .Where(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
            .FirstOrDefault();
        if (running is not null) return running;
        var queued = jobs
            .Where(j => string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.QueuedAt)
            .FirstOrDefault();
        if (queued is not null) return queued;
        return jobs
            .OrderByDescending(j => j.FinishedAt ?? j.StartedAt ?? j.QueuedAt)
            .FirstOrDefault();
    }
}

/// <summary>Persisted multi-job record (same fields as snapshot + queue metadata).</summary>
public sealed class JobRecord
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued|running|done|error|cancelled
    public string? Kind { get; set; }
    public string? Message { get; set; }
    public string? ProjectId { get; set; }
    public string? UserId { get; set; }
    public string? CharKey { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ClientMediaUrl { get; set; }
    public string? ClientRelativePath { get; set; }

    public JobSnapshot ToSnapshot() => new()
    {
        JobId = JobId,
        Status = Status,
        Kind = Kind,
        Message = Message,
        ProjectId = ProjectId,
        UserId = UserId,
        CharKey = CharKey,
        Scene = Scene,
        Clip = Clip,
        Index = Index,
        Total = Total,
        Log = Log.ToList(),
        Error = Error,
        QueuedAt = QueuedAt,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
        ClientMediaUrl = ClientMediaUrl,
        ClientRelativePath = ClientRelativePath,
    };
}

public sealed class StartSceneGenRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    /// <summary>When set, only generate this clip within the scene.</summary>
    public int? Clip { get; set; }
    public bool OnlyMissing { get; set; } = true;
    /// <summary>Video resolution for this gen (e.g. 480p / 720p). Empty → project Configuration.</summary>
    public string? Resolution { get; set; }
    /// <summary>
    /// Block video gen until every cast member has an approved voice profile and
    /// (for on-screen roles) a locked ref image. Default true — prevents wasted API spend.
    /// </summary>
    public bool RequireLockedCharacters { get; set; } = true;
    /// <summary>
    /// When true, return 409 immediately if the scene lock is held by another user.
    /// Default false: accept as queued and wait for the lock (Phase 2).
    /// </summary>
    public bool FailIfLocked { get; set; }
    /// <summary>
    /// H2 take trigger for cost ledger: <c>initial</c> | <c>user_regen</c> | <c>stale_regen</c> |
    /// <c>qa_auto</c> | <c>fill_holes</c>. Empty → inferred from onlyMissing / on-disk state.
    /// </summary>
    public string? TakeTrigger { get; set; }
}

public sealed class StartBatchGenRequest
{
    public string ProjectId { get; set; } = "";
    public List<int> Scenes { get; set; } = new();
    /// <summary>
    /// Explicit (scene, clip) targets — e.g. a multi-select of specific clips across one or more
    /// scenes. When set and non-empty, this replaces the <see cref="Scenes"/>-derived work list
    /// (every listed clip is force-regenerated, ignoring <see cref="OnlyMissing"/>, matching the
    /// single-clip regen behavior). <see cref="Scenes"/> may be left empty in this mode.
    /// </summary>
    public List<ClipTarget>? Clips { get; set; }
    public bool OnlyMissing { get; set; } = true;
    /// <summary>Video resolution for this gen (e.g. 480p / 720p). Empty → project Configuration.</summary>
    public string? Resolution { get; set; }
    /// <summary>
    /// One-off video model override for this batch only (admin model comparison) — does NOT change
    /// project Configuration. Empty/null → project Configuration's video model. Must resolve to a
    /// video-capable catalog model or the batch fails at start.
    /// </summary>
    public string? VideoModel { get; set; }
    /// <summary>
    /// Block video gen until every cast member has an approved voice profile and
    /// (for on-screen roles) a locked ref image. Default true — prevents wasted API spend.
    /// </summary>
    public bool RequireLockedCharacters { get; set; } = true;
    /// <summary>When true, 409 if any scene lock is held by another user (default wait).</summary>
    public bool FailIfLocked { get; set; }
    /// <summary>
    /// H2 take trigger for cost ledger: <c>initial</c> | <c>user_regen</c> | <c>stale_regen</c> |
    /// <c>qa_auto</c> | <c>fill_holes</c>. Empty → inferred from onlyMissing / on-disk state.
    /// </summary>
    public string? TakeTrigger { get; set; }
}

/// <summary>
/// Prompt-based edit of an already-generated clip (xAI /v1/videos/edits). Explicit, human-triggered,
/// per-clip only — never wired into <see cref="StartSceneGenRequest"/>/<see cref="StartBatchGenRequest"/>'s
/// automatic pipeline. Runs as its own job kind ("video_edit") so a long provider round trip gets
/// the same job-queue + progress treatment as scene generation, not a blocking HTTP request.
/// </summary>
public sealed class StartVideoEditRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public string Prompt { get; set; } = "";
    /// <summary>Catalog model id override (ModelCapability.VideoEdit). Empty → project/catalog default.</summary>
    public string? Model { get; set; }
}

/// <summary>
/// Server-side batch TTS for re-voice: synthesize each clip's dialogue with a stored clone voice id.
/// Audio is written under the project and handed to the client via <see cref="JobSnapshot.ClientMediaUrl"/>
/// (same mid-batch pattern as video/music). Keys never leave the server.
/// </summary>
public sealed class StartSpeakBatchRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>Character seed that owns the clone (e.g. Character_Narrator).</summary>
    public string CharKey { get; set; } = "Character_Narrator";
    /// <summary>
    /// Explicit (scene, clip[, text]) targets. When empty, the job loads the blueprint and selects
    /// clips automatically (see <see cref="NarratorOnly"/>).
    /// </summary>
    public List<SpeakBatchClip>? Clips { get; set; }
    /// <summary>
    /// Speech substitution mode (Narrator, Dialogue, All, None). Default Narrator.
    /// </summary>
    public SpeechSubstitutionMode SubstitutionMode { get; set; } = SpeechSubstitutionMode.Narrator;
    /// <summary>
    /// When <see cref="Clips"/> is empty: only clips whose speaker is the narrator (default true).
    /// When false, every clip with non-empty dialogue is spoken with <see cref="CharKey"/>'s voice.
    /// </summary>
    public bool NarratorOnly { get; set; } = true;
    /// <summary>Skip clips that already have revoice audio on the server project disk.</summary>
    public bool OnlyMissing { get; set; } = true;
    /// <summary>Max concurrent TTS provider calls (clamped 1–8). Default 3.</summary>
    public int MaxParallel { get; set; } = 3;
    /// <summary>Optional catalog speak-model id override; empty uses project voice_model_name / seed provider.</summary>
    public string? Model { get; set; }
    public bool FailIfLocked { get; set; }
}

/// <summary>One line to synthesize in a speak-batch job.</summary>
public sealed class SpeakBatchClip
{
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>Dialogue text. When empty, the job reads dialogue from the Stage 2 blueprint.</summary>
    public string? Text { get; set; }
}

/// <summary>One explicit clip target for a multi-select clip regen batch.</summary>
public sealed class ClipTarget
{
    public int Scene { get; set; }
    public int Clip { get; set; }
}

public sealed class CharacterImageRef
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? Url { get; set; }
    public int? Index { get; set; }
    public bool Exists { get; set; }
}

public sealed class CharacterSummary
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string VisualLock { get; set; } = "";
    public VisualMedium VisualMedium { get; set; } = VisualMedium.LiveAction;
    public string VoiceProfile { get; set; } = "";
    public string VoiceLabel { get; set; } = "";
    /// <summary>human | animal | creature | object … from the cast seed. Silent animals don't require a voice.</summary>
    public string? SpeciesKind { get; set; }
    /// <summary>
    /// True when this character actually speaks a line in the screenplay (a Character cue with dialogue).
    /// Talking cast — including talking animals — need a voice; silent cast (e.g. a background lamb) do not.
    /// Keys voice UI/gates on whether the character speaks, not on species alone.
    /// </summary>
    public bool Speaks { get; set; }
    /// <summary>Operator uploaded/recorded audio used as a voice-clone template (future TTS providers).</summary>
    public bool HasVoiceCloneSample { get; set; }
    public string? VoiceCloneFileName { get; set; }
    public string? VoiceCloneUrl { get; set; }
    /// <summary>Provider id that owns the cloned/catalog voice (e.g. elevenlabs).</summary>
    public string? VoiceProvider { get; set; }
    /// <summary>Provider-side voice id for TTS (ElevenLabs voice_id).</summary>
    public string? VoiceProviderVoiceId { get; set; }
    public bool VoiceOnly { get; set; }
    /// <summary>Plural/ensemble cast (CHILDREN, CROWD, …) — no single-face portrait required.</summary>
    public bool IsGroup { get; set; }
    /// <summary>individual | group | voice_only (derived).</summary>
    public string CastKind { get; set; } = "individual";
    public bool Locked { get; set; }
    public string? RefFileName { get; set; }
    public string? RefUrl { get; set; }
    /// <summary>Locked ref or variant 1 when present — default primary Grok seed.</summary>
    public bool HasPreferred { get; set; }
    public string? PreferredLabel { get; set; }
    public string? PreferredUrl { get; set; }
    public List<string> WardrobeAlways { get; set; } = new();
    public List<string> DesignReferenceImages { get; set; } = new();
    public List<CharacterImageRef> BookRefs { get; set; } = new();
    public List<CharacterImageRef> Variants { get; set; } = new();
    public VoiceAgeBand? AgeBand { get; set; }
    public VoiceGender Gender { get; set; } = VoiceGender.Neutral;
    public VoiceCloneStatus VoiceCloneStatus { get; set; } = VoiceCloneStatus.Ready;
    /// <summary>Base character's Key when this entry is an age-variant seed (child/teen/etc. of another character) — see cast_seeds.json's <c>variant_of</c>.</summary>
    public string? VariantOf { get; set; }
    /// <summary>
    /// True when this character appears in the current shot plan / scenes (on-screen cast).
    /// Unused seeds are kept in cast_seeds but hidden from the default Characters list.
    /// </summary>
    public bool UsedInPlan { get; set; } = true;
}

/// <summary>Location seed from Stage‑1 / classifier (location_seed_tokens).</summary>
public sealed class LocationSummary
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string VisualLock { get; set; } = "";
    /// <summary>True when <c>assets/locations/{key}_ref.png</c> exists and is locked.</summary>
    public bool Locked { get; set; }
    /// <summary>True when a preferred/locked plate exists on disk.</summary>
    public bool HasPreferred { get; set; }
    /// <summary>Browser URL for the locked/preferred set plate (if any).</summary>
    public string? PreferredUrl { get; set; }
    /// <summary>Relative path under the project for the locked ref image.</summary>
    public string? PreferredRelativePath { get; set; }
    /// <summary>Generated set plate options (variant_01..).</summary>
    public List<CharacterImageRef> Variants { get; set; } = new();
    /// <summary>
    /// True when this location is referenced by the current shot plan / scenes.
    /// Unused seeds stay in cast_seeds but are hidden from the default Locations list.
    /// </summary>
    public bool UsedInPlan { get; set; } = true;
}

/// <summary>Save location description / visual_lock (Locations look panel).</summary>
public sealed class UpdateLocationLookRequest
{
    public string? Description { get; set; }
    public string? VisualLock { get; set; }
}

/// <summary>Background job: generate location set plate variants (Grok image).</summary>
public sealed class StartLocationVariantsRequest
{
    public string ProjectId { get; set; } = "";
    public string LocKey { get; set; } = "";
    /// <summary>Variant count (default 3).</summary>
    public int Count { get; set; } = 3;
    public string? DescriptionOverride { get; set; }
    public string? VisualLockOverride { get; set; }
    /// <summary>When set with an existing lock, runs image edit from the locked plate.</summary>
    public string? ImageEditInstruction { get; set; }
    public bool PersistDescription { get; set; } = true;
}

/// <summary>
/// Flexible seed policy for portrait generation.
/// <list type="bullet">
/// <item><c>auto</c> — preferred (if any) + up to MaxBookHints book plates (default).</item>
/// <item><c>preferred_only</c> — only preferred lock/best variant.</item>
/// <item><c>book_hints</c> — preferred + all attached book plates (capped by MaxRefs).</item>
/// <item><c>explicit</c> — only paths/indices supplied by the client.</item>
/// <item><c>none</c> — text-only (description / visual_lock).</item>
/// </list>
/// </summary>
public sealed class StartCharacterVariantsRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    /// <summary>0 = auto (1 if locked, else 3).</summary>
    public int Count { get; set; }
    /// <summary>auto | preferred_only | book_hints | explicit | none</summary>
    public string SeedMode { get; set; } = "auto";
    /// <summary>
    /// Cap on image refs sent to the image API (Grok ≤ 3, Gemini ≤ 14).
    /// 0 = use provider default for the active image backend.
    /// </summary>
    public int MaxRefs { get; set; }
    /// <summary>In auto/book_hints, how many book plates after preferred (default 2).</summary>
    public int MaxBookHints { get; set; } = 2;
    /// <summary>Include preferred lock/best as first seed (default true for non-explicit modes).</summary>
    public bool IncludePreferred { get; set; } = true;
    /// <summary>When SeedMode is explicit (or to force-include): book ref indices from CharacterSummary.BookRefs.</summary>
    public List<int> BookRefIndices { get; set; } = new();
    /// <summary>When SeedMode is explicit: existing variant indices 1..3 to use as seeds.</summary>
    public List<int> VariantIndices { get; set; } = new();
    /// <summary>When SeedMode is explicit: also include locked ref if present.</summary>
    public bool IncludeLockedRef { get; set; } = true;
    /// <summary>
    /// Optional full selection order: "pref", "v1".."v3", "b0".."bN".
    /// When non-empty in explicit mode, overrides separate indices and preserves rank.
    /// </summary>
    public List<string> SeedOrderKeys { get; set; } = new();
    /// <summary>Optional description override for this generate (also used when PersistDescription).</summary>
    public string? DescriptionOverride { get; set; }
    /// <summary>Optional visual_lock override for this generate.</summary>
    public string? VisualLockOverride { get; set; }
    /// <summary>Write DescriptionOverride / VisualLockOverride into scenes.json seeds before generate.</summary>
    public bool PersistDescription { get; set; } = true;
}

public sealed class ExtractCastRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>Rebuild cast even if cast_seeds.json already exists.</summary>
    public bool Force { get; set; } = true;
    public string Model { get; set; } = "";
}

public sealed class AttachCharacterPlatesRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>
    /// Re-sort even if pipeline_state.character_plates.sorted_by_character is true.
    /// Default false so import/UI can skip a second sort.
    /// </summary>
    public bool Force { get; set; }
    /// <summary>Copy into assets/characters/*_bookref_*.</summary>
    public bool CopyIntoAssets { get; set; } = true;
    /// <summary>Optional single character; empty = all on-screen cast.</summary>
    public string? CharKey { get; set; }
    /// <summary>Use Grok vision to assign pages to cast (default true for job).</summary>
    public bool UseGrok { get; set; } = true;
    /// <summary>Max book images to send to Grok (cost/latency cap).</summary>
    public int MaxImages { get; set; } = 32;
    public string VisionModel { get; set; } = "";
}

public sealed class AttachCharacterPlatesResult
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public int CharactersUpdated { get; set; }
    public int CharactersSkipped { get; set; }
    /// <summary>pipeline_state says plates were already sorted; Attach was a no-op.</summary>
    public bool AlreadySorted { get; set; }
    /// <summary>After this call, character plates are sorted in scenes.json.</summary>
    public bool SortedByCharacter { get; set; }
    public string? SortedAt { get; set; }
    /// <summary>grok_vision | heuristic | heuristic_after_grok_empty | none</summary>
    public string? Method { get; set; }
    public int ImagesClassified { get; set; }
    public int ImagesSkippedText { get; set; }
    public Dictionary<string, List<string>> AttachedByCharacter { get; set; } = new();
}

/// <summary>
/// Snapshot of pipeline_state.character_plates — whether book images were
/// sorted onto character seeds (scenes.json design_reference_images).
/// </summary>
public sealed class CharacterPlatesState
{
    public bool SortedByCharacter { get; set; }
    public string? SortedAt { get; set; }
    public string Source { get; set; } = "scenes.json#character_seed_tokens.design_reference_images";
    public int CharactersUpdated { get; set; }
    /// <summary>grok_vision | heuristic | …</summary>
    public string? Method { get; set; }
}

/// <summary>Active image backend seed limits for Characters UI.</summary>
public sealed class ImageSeedLimits
{
    /// <summary>Catalog provider id (from models_catalog.json), e.g. grok | gemini | openai.</summary>
    public string Provider { get; set; } = "";
    public string? ImageModel { get; set; }
    /// <summary>Max reference images the active image API accepts per edit.</summary>
    public int MaxReferenceImages { get; set; } = 3;
}

public sealed class StartBookPrepareRequest
{
    public string ProjectId { get; set; } = "";
    public bool ForceExtract { get; set; } = true;
    public bool ForceVision { get; set; }
    public bool AutoVision { get; set; } = true;
    public string VisionModel { get; set; } = "";
}

/// <summary>
/// Import pipeline job: optional book prepare (PDF/OCR) + book→Fountain adapt.
/// Prefer this over sync from-book for long novels.
/// </summary>
public sealed class StartBookImportRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>When true, skip PDF/OCR and only run book→Fountain (book_full.txt must exist).</summary>
    public bool SkipPrepare { get; set; }
    public bool ForceExtract { get; set; } = true;
    public bool ForceVision { get; set; }
    public bool AutoVision { get; set; } = true;
    public string VisionModel { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>POST /api/projects — create a new project folder.</summary>
public sealed class RenameProjectRequest
{
    public string? Title { get; set; }
    public string? Name { get; set; }
}

public sealed class CreateProjectRequest
{
    /// <summary>Display name or folder id.</summary>
    public string? Name { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }
    /// <summary>optional: Full | SimpleVoice</summary>
    public StudioPath? StudioPath { get; set; }
}

public sealed class SetStudioPathRequest
{
    /// <summary>Full | SimpleVoice</summary>
    public StudioPath? StudioPath { get; set; }
}

public sealed class LockCharacterRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    /// <summary>bookref | variant</summary>
    public string Source { get; set; } = "variant";
    public int Index { get; set; } = 1;
}

/// <summary>Update voice_label / voice_profile on character seeds (scenes.json + blueprint).</summary>
public sealed class ApplyCatalogVoiceRequest
{
    public string? ProviderVoiceId { get; set; }
    public string? DisplayName { get; set; }
    public string? PreviewText { get; set; }
}

public sealed class UpdateCharacterVoiceRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? VoiceLabel { get; set; }
    public string? VoiceProfile { get; set; }
}

/// <summary>POST .../voice/clone body — optional catalog model id override.</summary>
public sealed class CloneVoiceApiRequest
{
    public string? Model { get; set; }
}

/// <summary>POST .../voice/speak body — narration/dialogue text (required) + optional voice id / model override.</summary>
public sealed class SpeakVoiceApiRequest
{
    public string? Text { get; set; }
    /// <summary>Provider voice id. When omitted, resolved from the character's stored voice/clone result.</summary>
    public string? VoiceId { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// Film-pipeline voice sample: short video with voice style + dialogue (cached as MP4).
/// </summary>
public sealed class StartVoicePreviewRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? VoiceLabel { get; set; }
    public string? VoiceProfile { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Optional dialogue line; default is a short name intro.</summary>
    public string? SampleText { get; set; }
    /// <summary>Ignore cache and regenerate (e.g. after editing profile).</summary>
    public bool Force { get; set; }
}

/// <summary>Whether a cached film voice sample exists and matches current profile text.</summary>
public sealed class VoicePreviewStatusDto
{
    public bool Ok { get; set; } = true;
    public bool Exists { get; set; }
    public bool Matches { get; set; }
    public string? Fingerprint { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    /// <summary>Same-origin media URL (MP4 or legacy MP3).</summary>
    public string? AudioUrl { get; set; }
    /// <summary>video/mp4 or audio/mpeg</summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// One JPEG (or PNG) frame sampled in the browser for auto-review.
/// Server never holds the provider key client-side; frames are uploaded over an authenticated API.
/// </summary>
public sealed class ClipAutoReviewClientFrame
{
    /// <summary>PREVIOUS_CLIP_TAIL | CURRENT_CLIP</summary>
    public string Label { get; set; } = "CURRENT_CLIP";
    /// <summary>image/jpeg or image/png</summary>
    public string Mime { get; set; } = "image/jpeg";
    /// <summary>Raw base64 (no data: prefix).</summary>
    public string Base64 { get; set; } = "";
}

/// <summary>Start AI auto-review of one clip (this clip + previous tail for continuity).</summary>
public sealed class StartClipAutoReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>
    /// Browser-sampled frames (ffmpeg.wasm). Required — server has no native ffmpeg.
    /// Order: previous-tail frames (if any), then current-clip frames.
    /// </summary>
    public List<ClipAutoReviewClientFrame>? Frames { get; set; }
}

/// <summary>Batch AI auto-review for all (or missing) on-disk clips in a project/scene.</summary>
public sealed class StartClipAutoReviewBatchRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>When set, only that scene; otherwise all scenes with clips on disk.</summary>
    public int? Scene { get; set; }
    /// <summary>When true (default), skip clips that already have an auto-review draft.</summary>
    public bool OnlyMissing { get; set; } = true;
}

/// <summary>Project-wide review index (assets/review/index.json) — one row per on-disk clip.</summary>
public sealed class ReviewIndexDocument
{
    public string ProjectId { get; set; } = "";
    public DateTimeOffset BuiltAtUtc { get; set; }
    public string SchemaVersion { get; set; } = "1";
    public List<ReviewIndexClipRow> Clips { get; set; } = new();
}

/// <summary>One clip’s QC / assembly / frame paths for V4 audit.</summary>
public sealed class ReviewIndexClipRow
{
    public string Key { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public string VideoPath { get; set; } = "";
    public bool VideoExists { get; set; }
    public string? AutoSuggestion { get; set; }
    public string? AutoCategory { get; set; }
    public string? AutoNote { get; set; }
    public DateTimeOffset? AutoReviewedAt { get; set; }
    public string? HumanStatus { get; set; }
    public string? HumanNote { get; set; }
    public bool AssemblyEligible { get; set; }
    public string? AssemblyBlockReason { get; set; }
    public string? DraftPath { get; set; }
    public bool HasDraft { get; set; }
    public List<string> FramePaths { get; set; } = new();
}

/// <summary>One suggested edit from auto-review (user may accept/edit before apply).</summary>
public sealed class ClipAutoReviewSuggestion
{
    /// <summary>clip | character | scene</summary>
    public string Layer { get; set; } = "clip";
    /// <summary>Field id: visual_prompt | voice_profile | description | visual_lock | note</summary>
    public string Field { get; set; } = "";
    /// <summary>Character key when layer=character.</summary>
    public string? CharKey { get; set; }
    public string Label { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public string SuggestedValue { get; set; } = "";
    /// <summary>Default checked when opening Apply panel (high confidence).</summary>
    public bool IncludeByDefault { get; set; } = true;
    public string? Rationale { get; set; }
}

/// <summary>AI draft for one clip — Review → Apply suggestions → Regen.</summary>
public sealed class ClipAutoReviewDraft
{
    public bool Ok { get; set; } = true;
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>pass | fail | unclear</summary>
    public string Suggestion { get; set; } = "unclear";
    public string Category { get; set; } = "other";
    public string Confidence { get; set; } = "medium";
    public string Note { get; set; } = "";
    public string Continuity { get; set; } = "unclear";
    public bool IncludedPreviousTail { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public List<ClipAutoReviewSuggestion> Suggestions { get; set; } = new();
    public string? RawSummary { get; set; }
    public string? Error { get; set; }
}

/// <summary>Apply selected suggestion rows (writes seeds / blueprint), optional then regen via gen job.</summary>
public sealed class ApplyClipAutoReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public List<ClipAutoReviewApplyItem> Items { get; set; } = new();
}

public sealed class ClipAutoReviewApplyItem
{
    public string Layer { get; set; } = "clip";
    public string Field { get; set; } = "";
    public string? CharKey { get; set; }
    /// <summary>Final text to write (user may have edited the suggestion).</summary>
    public string Value { get; set; } = "";
}

/// <summary>Update description / visual_lock on character seeds (cast_seeds + blueprint).</summary>
public sealed class UpdateCharacterLookRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? Description { get; set; }
    public string? VisualLock { get; set; }
    /// <summary>
    /// Run AI visual scrub (literal + base-look, not later wardrobe) before save. Default true.
    /// </summary>
    public bool ScrubWithAi { get; set; } = true;
    public string Model { get; set; } = "";
}

/// <summary>Response from look save (optional AI scrub applied).</summary>
public sealed class UpdateCharacterLookResult
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? CharKey { get; set; }
    public bool ScrubbedWithAi { get; set; }
    public string? Description { get; set; }
    public string? VisualLock { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

/// <summary>Delete preferred, variant, or book-ref picture for a character.</summary>
public sealed class DeleteCharacterImageRequest
{
    public string Kind { get; set; } = ""; // preferred | variant | bookref
    public int Index { get; set; }
}

/// <summary>
/// Structured content for the end-credits card, single-sourced from
/// <c>ProjectStore.BuildCreditsContent</c> (same inputs as the card prompt). The client renders these
/// exact strings deterministically (canvas) instead of asking a generative model to draw text.
/// </summary>
public sealed class CreditsContentDto
{
    public string Title { get; set; } = "The End";
    public string Author { get; set; } = "";
    public string SoftwareName { get; set; } = "PageToMovie";
    public string SiteUrl { get; set; } = "pagetomovie.com";
}

public sealed class SceneSummary
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    /// <summary>The auto-inserted (editable) end-credits scene — the one credits path. Used to hide the "Add credits" button once one exists.</summary>
    public bool IsCredits { get; set; }
    public int ClipCount { get; set; }
    public int ClipsOnDisk { get; set; }
    public bool ClipsComplete { get; set; }
    /// <summary>E5/E6: count of on-disk clips that look stale vs plan/QA.</summary>
    public int StaleClipCount { get; set; }
    /// <summary>True when any clip is stale or composite dirty.</summary>
    public bool HasStaleClips { get; set; }
    /// <summary>Stage 2 plan total (sum of planned clip targets). Not measured media.</summary>
    public double? PlannedDurationSeconds { get; set; }
    /// <summary>Measured from composite, or sum of on-disk clips. Null if no media.</summary>
    public double? ActualDurationSeconds { get; set; }
    /// <summary>Preferred display: actual if known, else planned.</summary>
    public double? DurationSeconds { get; set; }
    public bool CompositeExists { get; set; }
    public bool IsUserOverride { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    /// <summary>True when <see cref="PrimaryLocationId"/> has a locked set plate on disk (soft video continuity).</summary>
    public bool PrimaryLocationLocked { get; set; }
    public string Status { get; set; } = "empty"; // empty | partial | complete
    public bool IsApproved { get; set; }
    /// <summary>True when a background-music take is registered for this scene (drives the
    /// "Audio Takes" affordance on the all-scenes overview).</summary>
    public bool HasBackgroundMusic { get; set; }

    /// <summary>User holding scene lock (Phase D), if any.</summary>
    public string? LockOwnerUserId { get; set; }
    /// <summary>True when locked by a different user than the caller.</summary>
    public bool LockedByOther { get; set; }
    public string? LockReason { get; set; }
}

public sealed class ClipSummary
{
    public int ClipNumber { get; set; }
    public string Timestamp { get; set; } = "";
    /// <summary>Stage 2 planned duration for this clip.</summary>
    public int DurationSeconds { get; set; }
    /// <summary>Measured from the MP4 when on disk.</summary>
    public double? ActualDurationSeconds { get; set; }
    public string Continuation { get; set; } = "none";
    public string PrimarySubject { get; set; } = "";
    public string VisualPrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public string? Speaker { get; set; }
    public string? Delivery { get; set; }
    /// <summary>Second speaker in a cross-speaker two-hander clip (else null). Additive: single-line
    /// readers keep using <see cref="Speaker"/>/<see cref="Dialogue"/> unchanged.</summary>
    public string? SecondarySpeaker { get; set; }
    /// <summary>Second speaker's line in a two-hander clip (else null).</summary>
    public string? SecondaryDialogue { get; set; }
    public string? PronunciationHint { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public string? ColorPalette { get; set; }
    public string? FilmStock { get; set; }
    public bool OnDisk { get; set; }
    public long SizeBytes { get; set; }
    public string? VideoUrl { get; set; }
    public string? FileName { get; set; }
    public ClipDialogueVerificationResult? DialogueVerification { get; set; }

    /// <summary>Primary Stage‑1 / screenplay beat id this clip was planned from (stable <c>sb_…</c> when available).</summary>
    public string? Stage1BeatId { get; set; }

    /// <summary>
    /// All source beat ids when clips merge/split dialogue (prelude fold, monologue coalesce, two-hander).
    /// Empty when unknown; otherwise includes <see cref="Stage1BeatId"/> first.
    /// </summary>
    public List<string> Stage1BeatIds { get; set; } = new();

    /// <summary>E5: on-disk video may not match current plan / QA (regen recommended).</summary>
    public bool IsStale { get; set; }

    /// <summary>Short reason for <see cref="IsStale"/> (plan_newer, dialogue_qa, missing_beat).</summary>
    public string? StaleReason { get; set; }
}

/// <summary>Create or fully edit a clip's shot-plan fields (Scenes clip editor).</summary>
public sealed class ClipEditRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public string VisualPrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public string? Speaker { get; set; }
    public string? Delivery { get; set; }
    public string? PronunciationHint { get; set; }
    public string PrimarySubject { get; set; } = "";
    public List<string> CharactersOnScreen { get; set; } = new();
    public string? ColorPalette { get; set; }
    public string? FilmStock { get; set; }
    public int DurationSeconds { get; set; }
}

public sealed class SceneDetail
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    /// <summary>Stage 2 plan total.</summary>
    public double? PlannedDurationSeconds { get; set; }
    /// <summary>Measured composite or sum of clips.</summary>
    public double? ActualDurationSeconds { get; set; }
    /// <summary>Preferred display: actual if known, else planned.</summary>
    public double? DurationSeconds { get; set; }
    public int ClipCount { get; set; }
    public int ClipsOnDisk { get; set; }
    public bool CompositeExists { get; set; }
    public bool HasBackgroundMusic { get; set; }
    public MusicScoreInfo? MusicScore { get; set; }
    public bool IsUserOverride { get; set; }
    public string? CompositeUrl { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    /// <summary>True when <see cref="PrimaryLocationId"/> has a locked set plate on disk.</summary>
    public bool PrimaryLocationLocked { get; set; }
    public List<ClipSummary> Clips { get; set; } = new();

    /// <summary>Clip numbers that appeared more than once in this scene's shot plan (malformed data).
    /// The read path keeps the first and drops the rest so the scene still works, but flags it here so
    /// an admin surface can show the problem instead of silently hiding it. Empty = clean.</summary>
    public List<int> DuplicateClipNumbers { get; set; } = new();
}

public sealed class MusicScoreInfo
{
    public string Prompt { get; set; } = "";
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public string? Tempo { get; set; }
}

/// <summary>Book + Fountain screenplay + Stage 1 + Stage 2 readiness for the Adaptation page.</summary>
public sealed class AdaptationStatus
{
    public string ProjectId { get; set; } = "";
    public BookSourceStatus Book { get; set; } = new();
    /// <summary>Editable Fountain draft + sign-off state (source of truth for the screenplay).</summary>
    public ScreenplayStatus Screenplay { get; set; } = new();
    public Stage1Status Stage1 { get; set; } = new();
    public Stage2PlanStatus Stage2 { get; set; } = new();
    /// <summary>Cast pin progress (look + voice) for next-step gating.</summary>
    public CastStatus Cast { get; set; } = new();
    public bool XaiConfigured { get; set; }
    /// <summary>Configured planning model (Configuration page) — Stage 1 / book→Fountain / cast scrub.</summary>
    public string PlanningModel { get; set; } = "";
    public string NextStep { get; set; } = "";
    /// <summary>Which optional Book sub-steps (Look / Enrich / Fit length) have been run — for the sub-strip "done" checks.</summary>
    public BookSubstepStatus BookSubsteps { get; set; } = new();
}

/// <summary>
/// Which optional Book sub-steps have been run on the current screenplay, so the Book sub-strip can
/// show a "done" check. Look / Enrich / Fit length are optional passes — a check means "you ran this,"
/// not "required and complete." Cleared when a fresh full-length screenplay base is generated, so the
/// checks never carry over to freshly generated text. Import/Screenplay completion come from their own
/// state (source exists / signed), not from here.
/// </summary>
public sealed class BookSubstepStatus
{
    public bool LookDone { get; set; }
    public bool EnrichDone { get; set; }
    public bool FitLengthDone { get; set; }
    /// <summary>Target minutes recorded the last time Fit length ran (for the sub-strip label).</summary>
    public double? FitLengthTargetMinutes { get; set; }
}

/// <summary>
/// Whether every cast member is approved for video: voice profile for all,
/// locked ref image for on-screen roles (variant drafts alone are not enough).
/// </summary>
public sealed class CastStatus
{
    public int Total { get; set; }
    public int Ready { get; set; }
    /// <summary>True when every cast member has approved voice + locked look (if needed).</summary>
    public bool ReadyForShots { get; set; }
    /// <summary>Character keys still missing voice and/or locked image (for UI messaging).</summary>
    public List<string> Missing { get; set; } = new();
}

/// <summary>
/// Fountain draft under source/screenplay.fountain.
/// Signed = user approved; cast seeds materialised on sign-off.
/// </summary>
public sealed class ScreenplayStatus
{
    public bool DraftExists { get; set; }
    public long DraftBytes { get; set; }
    public string? DraftHash { get; set; }
    public string? DraftMtime { get; set; }
    public int SceneHeadingCount { get; set; }
    public string? Title { get; set; }
    /// <summary>True when draft hash matches last sign-off hash.</summary>
    public bool Signed { get; set; }
    public string? SignedHash { get; set; }
    public string? SignedAt { get; set; }
    /// <summary>Draft exists and differs from signed hash (or never signed).</summary>
    public bool Dirty { get; set; }
    /// <summary>Characters/Shots may proceed (signed Fountain with scene headings).</summary>
    public bool ReadyForShots { get; set; }
}

public sealed class BookSourceStatus
{
    public bool PdfExists { get; set; }
    public string? PdfName { get; set; }
    public bool BookTextExists { get; set; }
    public string? BookTextPath { get; set; }
    public long BookTextBytes { get; set; }
    public string? TextQuality { get; set; }
    public double GarbageScore { get; set; }
    public SourceDocumentType BookKind { get; set; } = SourceDocumentType.None;
    public TextEngineKind? TextEngine { get; set; }
    public int? TextWords { get; set; }
    public int? SuggestedTotalMinutes { get; set; }
    /// <summary>Density-based natural film length (minutes).</summary>
    public int? NaturalRuntimeMinutes { get; set; }
    /// <summary>User or system target used for Stage 1 / cost (minutes).</summary>
    public int? TargetRuntimeMinutes { get; set; }
    /// <summary>natural | reduced | custom</summary>
    public RuntimeMode? RuntimeMode { get; set; }
    public int? SuggestedChunkPages { get; set; }
    public int PageImageCount { get; set; }
    public bool ReadyForStage1 { get; set; }
    public string? Preview { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class Stage1Status
{
    public bool Present { get; set; }
    public string? ScenesFile { get; set; }
    public string? MovieTitle { get; set; }
    public string? SourceBookTitle { get; set; }
    public int SceneCount { get; set; }
    public int BeatCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public double? RuntimeSeconds { get; set; }
    public string? Mtime { get; set; }
    public List<string> CastNames { get; set; } = new();
    public List<Stage1SceneRow> Scenes { get; set; } = new();
}

public sealed class Stage1SceneRow
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    public int BeatCount { get; set; }
    public double? DurationSeconds { get; set; }
}

public sealed class Stage2PlanStatus
{
    public bool Stage1Exists { get; set; }
    public int Stage1Scenes { get; set; }
    public bool BlueprintExists { get; set; }
    public string? BlueprintPath { get; set; }
    public string? BlueprintFileName { get; set; }
    public int Stage2Scenes { get; set; }
    public int Stage2Clips { get; set; }
    public bool Stage2Ready { get; set; }
    public bool Stage2Stale { get; set; }
    public string? LastCompletedAt { get; set; }
    public string? LastRunMessage { get; set; }
    public int ValidationIssueCount { get; set; }
}

public sealed class StartStage1Request
{
    public string ProjectId { get; set; } = "";
    public int ChunkPages { get; set; } = 10;
    public int? TotalMinutes { get; set; }
    public string Model { get; set; } = "";
    public bool Resume { get; set; }
    public int MaxChunks { get; set; }
}

public sealed class StartStage2Request
{
    public string ProjectId { get; set; } = "";
    /// <summary>Optional; empty uses project Configuration resolution (prompt tag only).</summary>
    public string? Resolution { get; set; }
    public string Scenes { get; set; } = "all";
}

public sealed class StartYouTubeUploadRequest
{
    public string ProjectId { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>private | unlisted | public. Default unlisted (link-shareable, not publicly listed).</summary>
    public string PrivacyStatus { get; set; } = "unlisted";
}

/// <summary>Generate background music for a scene via audio API (client saves the audio segment(s)).</summary>
public sealed class StartSceneMusicGenRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    /// <summary>Overrides the project's configured audio_model_name for this run only; falls back to
    /// that config default when null/blank.</summary>
    public string? Model { get; set; }
    /// <summary>Request sung vocals instead of instrumental. Only Suno-family models can honor this —
    /// see IAudioClient.GenerateMusicTrackAsync.</summary>
    public bool IsVocal { get; set; }
}

/// <summary>Last successful YouTube upload for a project (sidecar assets/youtube_upload.json).</summary>
public sealed class YouTubeUploadInfo
{
    public string VideoId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string PrivacyStatus { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>WIP movie freshness for Play-WIP-one-step UX.</summary>
public sealed class WipFreshness
{
    public bool Exists { get; set; }
    public bool Stale { get; set; }
    public bool CanBuild { get; set; }
    public string Reason { get; set; } = "";
    public string? Path { get; set; }
    public long Bytes { get; set; }
    public string? UpdatedAt { get; set; }
    /// <summary>Scenes whose composites are dirty (clips newer / missing composite) — browser Play stitches clips instead.</summary>
    public List<int> StaleScenes { get; set; } = new();
    /// <summary>Scenes with on-disk clips (Stage 2 order) that can participate in a browser WIP stitch.</summary>
    public List<int> ScenesToRemux { get; set; } = new();
}

public sealed class ClipReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>pass | fail | pending</summary>
    public string Status { get; set; } = "pass";
    public string Note { get; set; } = "";
}

public sealed class SceneApproveRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Client reports a media file written to the local media folder (hash only on server).</summary>
public sealed class MediaRegisterRequest
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Kind { get; set; } = "clip";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
}

public sealed class EditLogEntry
{
    public string Id { get; set; } = "";
    public string Ts { get; set; } = "";
    public string Type { get; set; } = "";
    public string LearningLayer { get; set; } = "clip";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Character { get; set; }
    public string UserNote { get; set; } = "";
    public string ActionTaken { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string SuggestedRule { get; set; } = "";
}

public sealed class EditLogDocument
{
    public int Version { get; set; } = 1;
    public List<EditLogEntry> Entries { get; set; } = new();
}

/// <summary>SignalR event payloads.</summary>
public static class JobHubEvents
{
    public const string JobUpdated = "JobUpdated";
    public const string JobLog = "JobLog";
    /// <summary>Admin ops group snapshot push (Phase C).</summary>
    public const string AdminState = "AdminState";
}

// ---- Cost / ledger ----

public sealed class CostEvent
{
    public string? Id { get; set; }
    public string? Ts { get; set; }
    public string Kind { get; set; } = "video"; // transport: video | image | chat | …
    /// <summary>User-facing bucket (<see cref="CostCategories"/>).</summary>
    public string Category { get; set; } = CostCategories.Other;
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Model { get; set; }
    public string? Resolution { get; set; }
    public double? DurationSec { get; set; }
    public double Usd { get; set; }
    /// <summary>Vendor list rate before charge multiplier (when recorded).</summary>
    public double? ListUsd { get; set; }
    /// <summary>Charge multiplier applied when the event was written (null = legacy list-rate-only).</summary>
    public double? ChargeMultiplier { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Source { get; set; } // list_rate | backfill
    public string? Character { get; set; }
    public double? OutputRatePerSec { get; set; }
    public bool? HasRefImage { get; set; }
    public bool? IsExtend { get; set; }
    /// <summary>H1 — actor who started the gen (multi-user).</summary>
    public string? UserId { get; set; }
    /// <summary>H1/I13 — <c>shared</c> | <c>personal</c> key scope used for this take.</summary>
    public string? KeyMode { get; set; }
    /// <summary>
    /// H1/H2 take trigger: <c>initial</c> | <c>user_regen</c> | <c>stale_regen</c> |
    /// <c>qa_auto</c> | <c>fill_holes</c>.
    /// </summary>
    public string? TakeKind { get; set; }
    /// <summary>1 = first billed take for this scene+clip; 2+ = subsequent regens.</summary>
    public int? TakeIndex { get; set; }
    /// <summary>Stable beat id when the clip is beat-mapped.</summary>
    public string? StableBeatId { get; set; }
    public bool? HadCharRefs { get; set; }
    public bool? HadLocRef { get; set; }
    /// <summary>Minutes since previous take for the same scene+clip (null if first).</summary>
    public double? MinutesSincePrevTake { get; set; }
    /// <summary>H3 optional regen reason: dialogue | look | motion | audio | other.</summary>
    public string? Reason { get; set; }
}

public sealed class CostLedgerSummary
{
    public double ActualUsd { get; set; }
    /// <summary>Sum of vendor list rates (COGS). Equals ActualUsd when multiplier was 1 or legacy events.</summary>
    public double ListRateUsd { get; set; }
    public int EventCount { get; set; }
    public int VideoJobs { get; set; }
    public int ImageJobs { get; set; }
    public double VideoSec { get; set; }
    public Dictionary<string, double> ByKind { get; set; } = new();
    public Dictionary<string, double> ByScene { get; set; } = new();
    public Dictionary<string, double> ByModel { get; set; } = new();
    public string Currency { get; set; } = "USD";
    public string Notes { get; set; } =
        "Tracked actuals = list rates in cost_ledger; UI multiplies by admin charge multiplier.";
}

public sealed class CostSceneRow
{
    public int Scene { get; set; }
    public string Setting { get; set; } = "";
    public int ClipsTotal { get; set; }
    public int ClipsOnDisk { get; set; }
    public int ClipsMissing { get; set; }
    public bool IsHero { get; set; }
    public string? HeroResolution { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    public double SpentUsd { get; set; }
    public double ActualUsd { get; set; }
    public double RemainingDraftUsd { get; set; }
    public double HeroUpgradeUsd { get; set; }
    public double AllDraftUsd { get; set; }
    public double AllHeroUsd { get; set; }
    public double DurationOnDiskSec { get; set; }
    public double DurationMissingSec { get; set; }
}

public sealed class CostReportSummary
{
    public int ClipsTotal { get; set; }
    public int ClipsOnDisk { get; set; }
    public int ClipsMissing { get; set; }
    public double SecOnDisk { get; set; }
    public double SecMissing { get; set; }
    public double SpentUsd { get; set; }
    public double ActualUsd { get; set; }
    public int ActualEvents { get; set; }
    public int ActualVideoJobs { get; set; }
    public double ActualVideoSec { get; set; }
    public double RemainingFirstPassUsd { get; set; }
    public double RemainingHeroUpgradeUsd { get; set; }
    public double FinishDraftUsd { get; set; }
    public double FinishDraftPlusHeroUsd { get; set; }
    public double FinishFromActualUsd { get; set; }
    public double FullFilmAllDraftUsd { get; set; }
    public double FullFilmAllHeroUsd { get; set; }
    /// <summary>Vendor list-rate full-film draft estimate (before charge multiplier).</summary>
    public double FullFilmAllDraftListUsd { get; set; }
    /// <summary>Vendor list-rate full-film hero estimate (before charge multiplier).</summary>
    public double FullFilmAllHeroListUsd { get; set; }
    public int ScenesWithMedia { get; set; }
    public int ScenesHero { get; set; }
    public int ScenesTotal { get; set; }
}

public sealed class CostScenarioRow
{
    public string Label { get; set; } = "";
    public string Resolution { get; set; } = "480p";
    public string? ModelName { get; set; }
    public double RatePerSec { get; set; }
    public double FullFilmUsd { get; set; }
    public double RemainingMissingUsd { get; set; }
    public double RegenOnDiskUsd { get; set; }
    public double AssumeAvgRetries { get; set; }
}

/// <summary>How planning estimates were adjusted from stored actuals (DB / ledger).</summary>
public sealed class CostEstimateRefinement
{
    public bool UsedHistory { get; set; }
    public int VideoApiSamples { get; set; }
    public int ReviewApiSamples { get; set; }
    public int TimingSamples { get; set; }
    public int ProjectLedgerEvents { get; set; }
    /// <summary>QA video multiplier prior before history (e.g. 1.3).</summary>
    public double PriorVideoMultiplier { get; set; } = 1.3;
    /// <summary>Multiplier after blending history (fail/truncation rate, etc.).</summary>
    public double AppliedVideoMultiplier { get; set; } = 1.0;
    public double? LearnedFailRate { get; set; }
    public double HistoryWeight { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>H4/H5 — clip samples used for learned takes (global or project).</summary>
    public int TakesClipSamples { get; set; }
    /// <summary>H5 — p50 takes/clip from telemetry when sufficient.</summary>
    public double? LearnedTakesP50 { get; set; }
    /// <summary>H5 — blended expected takes (prior × QA mult blended with learned p50).</summary>
    public double ExpectedTakes { get; set; } = 1.0;
}

public sealed class CostReport
{
    public string ProjectId { get; set; } = "";
    public string DraftResolution { get; set; } = "480p";
    public string HeroResolution { get; set; } = "720p";
    public string? ModelName { get; set; }
    public string? VideoProvider { get; set; }
    /// <summary>Active image model used for character-portrait estimates.</summary>
    public string? ImageModelName { get; set; }
    /// <summary>Active planning/chat model (screenplay / shot plan).</summary>
    public string? PlanningModelName { get; set; }
    /// <summary>Active voice model when voice is in scope.</summary>
    public string? VoiceModelName { get; set; }
    /// <summary>
    /// What drove clip counts: <c>shot_plan</c> (blueprint), <c>screenplay</c> (post-import durations),
    /// <c>remaining</c> (partial media + missing), or <c>none</c> (no usable screenplay yet).
    /// </summary>
    public string EstimateBasis { get; set; } = "none";
    /// <summary>
    /// A1 — clip count source for the DecisionCard:
    /// <c>none</c> | <c>synthetic_screenplay</c> | <c>blueprint</c> | <c>remaining</c>.
    /// </summary>
    public string ClipSource { get; set; } = "none";
    /// <summary>
    /// A1 — confidence label: <c>very_low</c> | <c>rough</c> | <c>good</c> | <c>best</c>.
    /// </summary>
    public string EstimateConfidence { get; set; } = "very_low";
    /// <summary>A1/A3 — low end of full-pass $ band (first-pass takes, cold-start prior).</summary>
    public double? CostLowUsd { get; set; }
    /// <summary>A1/A3 — point full-pass $ (current plan × expected takes).</summary>
    public double? CostPointUsd { get; set; }
    /// <summary>A1/A3 — high end of full-pass $ band (elevated takes prior).</summary>
    public double? CostHighUsd { get; set; }
    /// <summary>A1/A3 — planned duration in minutes (from clip seconds or screenplay target).</summary>
    public double? DurationMinutes { get; set; }
    /// <summary>A3 — human duration line for DecisionCard (e.g. "~94 min").</summary>
    public string DurationLabel { get; set; } = "";
    /// <summary>A3 — human cost line for DecisionCard (e.g. "~$42" or "~$32–$58").</summary>
    public string CostLabel { get; set; } = "";
    /// <summary>
    /// A5 — operational remaining strip: spent + missing $ while gen runs
    /// (e.g. "Spent $12 · remaining $28 · finish ~$40 · 14 clips missing").
    /// Empty when no media on disk yet.
    /// </summary>
    public string RemainingLabel { get; set; } = "";
    /// <summary>G4 — <c>draft</c> | <c>full</c> production mode for this project.</summary>
    public string ProductionMode { get; set; } = ProductionModes.Full;
    /// <summary>H5–H8 learned takes / calibration for DecisionCard.</summary>
    public CostTakesLearning TakesLearning { get; set; } = new();
    /// <summary>True when optional personal voice is included in the estimate.</summary>
    public bool VoiceIncludedInEstimate { get; set; }
    /// <summary>
    /// Admin charge multiplier applied to list rates for customer-facing estimates
    /// (and used when recording actual charges). 1.0 = list rate.
    /// </summary>
    public double ChargeMultiplier { get; set; } = 1.0;
    public double OutputRateDraft { get; set; }
    public double OutputRateHero { get; set; }
    public double AssumeAvgRetries { get; set; }
    public CostReportSummary Summary { get; set; } = new();
    public CostLedgerSummary Actual { get; set; } = new();
    /// <summary>
    /// Full-film planning estimate by user-facing category
    /// (screenplay, characters, video, voice, music, other) — <b>customer charge</b> amounts.
    /// </summary>
    public Dictionary<string, double> EstimateByCategory { get; set; } = new();
    /// <summary>Same buckets as EstimateByCategory but vendor list rates (before multiplier).</summary>
    public Dictionary<string, double> EstimateByCategoryListRate { get; set; } = new();
    public CostEstimateRefinement Refinement { get; set; } = new();
    public List<CostSceneRow> Scenes { get; set; } = new();
    public List<CostScenarioRow> Scenarios { get; set; } = new();
    public List<CostEvent> RecentEvents { get; set; } = new();
    public string Notes { get; set; } =
        "Estimates = planning × charge multiplier. Actual display = cost_ledger list rates × charge multiplier.";
    public string Currency { get; set; } = "USD";
}

public sealed class CostBackfillResult
{
    public int Added { get; set; }
    public int Skipped { get; set; }
    public int LedgerEvents { get; set; }
    public double ActualUsd { get; set; }
}
