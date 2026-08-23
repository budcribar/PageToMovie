namespace PageToMovie.Cut.Cut;

/// <summary>
/// Music dropped or edited during picture combine waits for the mix
/// pass. Adding or replacing the one track never cancels the stitch.
/// </summary>
public sealed class CutMusicQueue
{
    public const string QueuedMessage = "Music queued — mixing when combine finishes";
    public const string WaitingMessage = "Music waiting — Play to mix";

    public bool IsQueued { get; private set; }
    public bool IsMixing { get; private set; }

    public string? Status => IsQueued && !IsMixing ? QueuedMessage : null;

    public string? StatusAfterCompose(bool composeSucceeded) =>
        !IsQueued ? null : composeSucceeded ? QueuedMessage : WaitingMessage;

    public static bool ShouldForgetPreview(bool composing) => !composing;

    public static bool ShouldCancelCompose(bool composing)
    {
        _ = composing;
        return false;
    }

    public static bool ShouldClearMerge(bool composing)
    {
        _ = composing;
        return false;
    }

    public static bool ShouldRebuildPictureOnMixEdit(bool composing) => !composing;

    public void AttachFile(bool composing, Action? forgetPreview = null)
    {
        IsQueued = composing;
        if (ShouldForgetPreview(composing))
            forgetPreview?.Invoke();
    }

    public void ReplaceFile(bool composing, Action? forgetPreview = null) =>
        AttachFile(composing, forgetPreview);

    public void ChangeMix(bool composing, Action? forgetPreview = null)
    {
        if (composing)
            IsQueued = true;
        if (ShouldForgetPreview(composing))
            forgetPreview?.Invoke();
    }

    public void Remove(bool composing, Action? forgetPreview = null)
    {
        IsQueued = false;
        if (ShouldForgetPreview(composing))
            forgetPreview?.Invoke();
    }

    public bool ShouldMixAfterCompose(bool composeSucceeded, bool hasAudio) =>
        composeSucceeded && IsQueued && hasAudio;

    public void BeginMix() => IsMixing = true;

    public void EndMixUnfinished() => IsMixing = false;

    public void MarkMixed()
    {
        IsQueued = false;
        IsMixing = false;
    }

    public void OnComposeCancelled()
    {
        IsQueued = false;
        IsMixing = false;
    }
}
