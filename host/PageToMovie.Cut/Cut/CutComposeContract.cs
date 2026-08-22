namespace PageToMovie.Cut.Cut;

/// <summary>
/// Compose audio + preview-cache contract. Play and Make movie keep each
/// clip's native audio on the hop/trim window. Optional music mixes under.
/// Hard-cut concat keeps audio. Visual dissolves try acrossfade; if that
/// cannot mix audio, hard-cut audio through the join.
/// </summary>
public static class CutComposeContract
{
    public const bool KeepNativeClipAudio = true;
    public const bool PadCardSilence = true;

    public static bool CanReusePreview(string? moviePreviewUrl) =>
        !string.IsNullOrWhiteSpace(moviePreviewUrl);

    public static CutComposeAudioJoin AudioJoin(CutJoinKind kind) =>
        kind switch
        {
            CutJoinKind.Dissolve or CutJoinKind.Dip or CutJoinKind.FadeWhite
                or CutJoinKind.FadeIn or CutJoinKind.FadeOut
                => CutComposeAudioJoin.AcrossfadeOrHardCut,
            _ => CutComposeAudioJoin.KeepThroughConcat,
        };
}

public enum CutComposeAudioJoin
{
    KeepThroughConcat,
    AcrossfadeOrHardCut,
}
