namespace PageToMovie.Cut.Cut;

/// <summary>
/// Compose audio + preview-cache contract. Play and Make movie keep each
/// clip's native audio on the hop/trim window. Optional music mixes under.
/// Hard-cut concat keeps audio. Visual dissolves try acrossfade; if that
/// cannot mix audio, hard-cut audio through the join.
/// Cut-to-black is a short black hold at the join — not a scene card.
/// </summary>
public static class CutComposeContract
{
    public const bool KeepNativeClipAudio = true;
    public const bool PadCardSilence = false;

    /// <summary>Instant black hold between scenes. Not a chapter card.</summary>
    public const double CutToBlackHoldSeconds = 0.4;

    public static bool CanReusePreview(string? moviePreviewUrl) =>
        !string.IsNullOrWhiteSpace(moviePreviewUrl);

    public static bool JoinInsertsBlackHold(CutJoinKind kind) =>
        kind == CutJoinKind.CutToBlack;

    /// <summary>
    /// Cut-to-black is a join look. Fountain <c>[[CARD:]]</c> / Add text
    /// stay on the text row — the join never invents a "Scene" card.
    /// </summary>
    public static bool JoinIsSceneCard(CutJoinKind kind)
    {
        _ = kind;
        return false;
    }

    public static double HoldSeconds(CutJoinKind kind) =>
        JoinInsertsBlackHold(kind) ? CutToBlackHoldSeconds : 0;

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
