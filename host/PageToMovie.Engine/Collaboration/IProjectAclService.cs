namespace PageToMovie.Engine.Collaboration;

public interface IProjectAclService
{
    Task<ProjectAclDocument> GetOrCreateAclAsync(string projectId, string ownerUserId, CancellationToken ct = default);
    Task<ProjectAclDocument?> GetAclAsync(string projectId, CancellationToken ct = default);
    Task SaveAclAsync(string projectId, ProjectAclDocument acl, CancellationToken ct = default);
    Task<ProjectAccessLevel> GetAccessLevelAsync(string projectId, string userId, CancellationToken ct = default);
    Task<bool> CanAccessAsync(string projectId, string userId, ProjectAccessLevel minimum, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="CanAccessAsync(string,string,ProjectAccessLevel,CancellationToken)"/>,
    /// but administrators skip the ACL file (middleware already does this; handlers must too).
    /// </summary>
    Task<bool> CanAccessAsync(
        string projectId, string userId, ProjectAccessLevel minimum, bool isAdmin, CancellationToken ct = default);
    Task InviteEditorAsync(string projectId, string ownerUserId, string editorUserId, CancellationToken ct = default);
    Task RemoveEditorAsync(string projectId, string ownerUserId, string editorUserId, CancellationToken ct = default);
    Task InviteViewerAsync(string projectId, string ownerUserId, string viewerUserId, CancellationToken ct = default);
    Task RemoveViewerAsync(string projectId, string ownerUserId, string viewerUserId, CancellationToken ct = default);
}
