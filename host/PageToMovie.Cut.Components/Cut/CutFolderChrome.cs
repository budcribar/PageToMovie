namespace PageToMovie.Cut.Cut;

/// <summary>
/// Toolbar chrome for standalone Cut vs Review-hosted CutEditor.
/// Hosted mode reuses the host media folder; it must not ask the user to pick again.
/// </summary>
internal static class CutFolderChrome
{
    internal const string AttachFailedMessage = "Could not open project media.";

    internal static bool IsHosted(bool autoAttachHostFolder, string? hostProjectPrefix) =>
        autoAttachHostFolder && !string.IsNullOrWhiteSpace(hostProjectPrefix);

    /// <summary>
    /// Standalone Cut always shows Pick folder / Choose MP4s.
    /// Hosted Review Finish hides them while the host folder is attached, attaching,
    /// or not yet tried. After a failed attach, show them only when the host has
    /// no folder at all (last resort).
    /// </summary>
    internal static bool ShowPickButtons(
        bool autoAttachHostFolder,
        string? hostProjectPrefix,
        bool hasFolder,
        bool attaching,
        bool hostAttachTried,
        bool hostFolderUnavailable)
    {
        if (!IsHosted(autoAttachHostFolder, hostProjectPrefix))
            return true;
        if (hasFolder || attaching || !hostAttachTried)
            return false;
        return hostFolderUnavailable;
    }

    internal static bool ShowAttachRetry(
        bool autoAttachHostFolder,
        string? hostProjectPrefix,
        bool hasFolder,
        bool attaching,
        bool hostAttachFailed) =>
        IsHosted(autoAttachHostFolder, hostProjectPrefix)
        && hostAttachFailed
        && !hasFolder
        && !attaching;
}
