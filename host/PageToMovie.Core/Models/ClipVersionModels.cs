using System;
using System.Collections.Generic;

namespace PageToMovie.Core.Models;

/// <summary>
/// Model representing a clip version/take for side-by-side comparison and rollback.
/// </summary>
public sealed class ClipVersionItem
{
    public string VersionId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public int Take { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string VisualPrompt { get; set; } = "";
    public string ScriptText { get; set; } = "";
    public string Model { get; set; } = "";
    public string Resolution { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Mp4FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>True when this version's bytes live only on the client (synced + pruned server-side) —
    /// the UI must resolve video playback via the local media folder, not a server URL.</summary>
    public bool ClientOnly { get; set; }
    /// <summary>Project-relative path (e.g. "assets/video/scene_01_clip_02.mp4") for ClientOnly
    /// versions — the exact key the media registry has, so the client can look up its local
    /// blob without re-deriving the folder convention (active vs. history vs. take-named).</summary>
    public string? RelativePath { get; set; }
    /// <summary>Take number this version was AI-edited from (a prompt-based /videos/edits result),
    /// null for an ordinary generated/regenerated take. Lets the Takes compare UI show "edited from
    /// Take N" instead of an indistinguishable flat entry.</summary>
    public int? EditedFromTake { get; set; }
    /// <summary>xAI Files API file_id for this exact clip, when generation requested storage and it
    /// succeeded — null otherwise (storage wasn't requested, failed, or has since aged out of the
    /// sidecar). Consumed by the video-edit job to try file_id reuse before falling back to
    /// uploading the local file.</summary>
    public string? SourceFileId { get; set; }
    /// <summary>Unix-seconds expiry for <see cref="SourceFileId"/>, when known.</summary>
    public long? SourceFileExpiresAtUnixSeconds { get; set; }
    /// <summary>Provider-hosted copy of this take (sidecar source_url). Takes are recorded by their
    /// sidecars — the server keeps no media — so a take can exist with no server or client file.</summary>
    public string? SourceUrl { get; set; }
    /// <summary>Seconds at the head of the provider copy that belong to the previous clip (extend takes).</summary>
    public double ProviderLeadInSeconds { get; set; }
    /// <summary>Short-lived proxy URL the browser can play/slice for a take that has no local file; null when the
    /// take is on the server (history file) or only on the client.</summary>
    public string? ProviderPlaybackUrl { get; set; }
}

/// <summary>
/// One scene-audio generation run ("take") for side-by-side comparison and rollback — the audio
/// equivalent of <see cref="ClipVersionItem"/>. A take is one or more segment files produced
/// together (see <c>MediaRegistryService.MusicSegmentRelativePath</c>), identified by
/// <see cref="TakeId"/> rather than by a single filename.
/// </summary>
public sealed class MusicVersionItem
{
    public string TakeId { get; set; } = "";
    public int Scene { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Model { get; set; } = "";
    public bool IsVocal { get; set; }
    public string Prompt { get; set; } = "";
    public string? Lyrics { get; set; }
    public List<string> SegmentFileNames { get; set; } = new();
    public bool ClientOnly { get; set; }
    public List<string> RelativePaths { get; set; } = new();
}

/// <summary>
/// Package Git status: last commit + optional uncommitted scene/clip summary.
/// Used for Home "Last saved" (never auto-reverts).
/// </summary>
public sealed class UncommittedStatusDto
{
    public bool GitAvailable { get; set; }
    public string? SkipReason { get; set; }
    public bool RemoteConfigured { get; set; }
    public string? LastCommitHash { get; set; }
    public string? LastCommitMessage { get; set; }
    public string? LastCommitAuthor { get; set; }
    public DateTime? LastCommitAtUtc { get; set; }
    public string? HistoryUrl { get; set; }
    public bool HasUncommittedChanges { get; set; }
    public List<int> ModifiedScenes { get; set; } = new();
    public List<string> ModifiedClipKeys { get; set; } = new();
    public string Summary { get; set; } = "";
}
