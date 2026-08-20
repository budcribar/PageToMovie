namespace PageToMovie.Engine;

/// <summary>
/// Provider-persisted video handle after generate/poll.
/// <see cref="PublicUrl"/> is the durable unauthenticated Files link (when requested via
/// <c>storage_options.public_url</c>). <see cref="FileId"/> is for edit/extend attach —
/// Imagine file_ids are generate-only and cannot be re-downloaded via Files content GET.
/// </summary>
public readonly record struct StoredVideoFileRef(
    string? FileId,
    long? ExpiresAtUnixSeconds,
    string? PublicUrl)
{
    public static StoredVideoFileRef Empty => default;

    public bool HasFileId => !string.IsNullOrWhiteSpace(FileId);
    public bool HasPublicUrl => !string.IsNullOrWhiteSpace(PublicUrl);

    /// <summary>Sidecar <c>source_url</c>: Files <c>public_url</c> when present, else the poll <c>video.url</c>.</summary>
    public string? DurableSourceUrl(string? pollUrl)
    {
        if (HasPublicUrl)
            return PublicUrl!.Trim();
        if (string.IsNullOrWhiteSpace(pollUrl))
            return null;
        return pollUrl.Trim();
    }
}
