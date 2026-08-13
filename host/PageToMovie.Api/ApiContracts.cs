using PageToMovie.Core.Models;

namespace PageToMovie.Api;

public record AcceptTermsRequest(string UserId, string? Version);
public record SendInviteApiRequest(string? ProjectId, string? TargetHandle, string? TargetEmail);
public record AcceptInviteApiRequest(string? Token);
public record CommitProjectApiRequest(string? Message, bool ForceCommit = false);
public record PushProjectApiRequest(bool CommitFirst = false, string? Message = null);
public record SyncOriginApiRequest(string? ParentProjectId, string? AutoResolveStrategy = null);
public record ProjectVisibilityRequest(string VisibilityMode);
public record SetBookRefsRequest(List<string>? ImagePaths);
public record MovieReviewRequest(List<MovieAutoReviewKeyframe>? Keyframes);
public record RegisterYouTubeDemoRequest(string? YoutubeIdOrUrl, string? Title, string? Description, string? ProjectId);

internal sealed record TakeReasonBody(int Scene, int Clip, string? Reason, int? TakeIndex);

internal sealed class FilmBuildRegisterRequest
{
    public string? StudioSha256 { get; set; }
    public double DurationSeconds { get; set; }
    public long? ByteLength { get; set; }
    public string? StudioPath { get; set; }
    public string? AssemblyWhere { get; set; }
    public bool? HashFromServerWip { get; set; }
    public List<FilmBuildSegmentDto>? Segments { get; set; }
}

internal sealed class FilmBuildSegmentDto
{
    public int Index { get; set; } = -1;
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int? Take { get; set; }
    public double TStart { get; set; }
    public double TEnd { get; set; }
    public string? Src { get; set; }
    public string? SrcSha256 { get; set; }
    public string? Sidecar { get; set; }
}
