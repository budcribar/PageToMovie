using System.Text.Json;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Single source of truth for "what dialogue does this clip actually say?".
///
/// A clip can carry more than one spoken line: the primary line
/// (<c>speaker</c>/<c>dialogue</c>/<c>delivery</c>) plus a second speaker's line
/// (<c>secondary_speaker</c>/<c>secondary_dialogue</c>) produced by Stage 2's cross-speaker
/// "two-hander" coalesce (<see cref="Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats"/>).
/// This concept used to be hand-rolled at four sites (duration estimate, coalesce sizing, dialogue
/// verification, prompt/audio build) and two of them silently dropped the secondary line — so a
/// two-hander clip was under-budgeted for time AND its second line was never verified. Every reader
/// now routes through this accessor so they can never disagree again.
///
/// Mirrors the <see cref="ClipKeying"/> pattern (small internal static Engine helper). Adding a
/// hypothetical THIRD speaker later is a one-line change: add a slot to <see cref="Slots"/> (the
/// JSON / beat-dict data paths pick it up automatically); the only other touch is the DTO
/// projection in <see cref="FromClip"/>, because <see cref="ClipSummary"/> must declare a field to
/// carry it.
/// </summary>
internal static class ClipSpokenLines
{
    /// <summary>One spoken line of a clip: who speaks, what they say, and how it is delivered.</summary>
    public readonly record struct SpokenLine(string Speaker, string Dialogue, string Delivery);

    /// <summary>
    /// The ordered slots a clip's audio payload / beat dict can carry, primary first. This is the
    /// single place that knows the slot key names and their order — add a third
    /// <c>(tertiary_speaker, tertiary_dialogue, tertiary_delivery)</c> row here to support a third
    /// speaker across every JSON / beat-dict reader in one line.
    /// </summary>
    private static readonly (string Speaker, string Dialogue, string Delivery)[] Slots =
    {
        ("speaker", "dialogue", "delivery"),
        ("secondary_speaker", "secondary_dialogue", "secondary_delivery"),
        ("tertiary_speaker", "tertiary_dialogue", "tertiary_delivery"),
    };

    /// <summary>Delivery value that explicitly marks a beat as silent (no spoken line).</summary>
    private const string SilentDelivery = "none";

    /// <summary>
    /// Spoken lines from an <c>audio_payload</c> object element. Reads every slot in
    /// <see cref="Slots"/> and keeps only lines that actually have dialogue and are not explicitly
    /// marked silent (<c>delivery:"none"</c>).
    /// </summary>
    public static IReadOnlyList<SpokenLine> FromAudioPayload(JsonElement audioPayload)
    {
        if (audioPayload.ValueKind != JsonValueKind.Object)
            return Array.Empty<SpokenLine>();
        return Build(key =>
            audioPayload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null);
    }

    /// <summary>Spoken lines from a blueprint clip element (reads its <c>audio_payload</c>).</summary>
    public static IReadOnlyList<SpokenLine> FromClipElement(JsonElement clipEl)
    {
        if (clipEl.ValueKind == JsonValueKind.Object &&
            clipEl.TryGetProperty("audio_payload", out var ap))
            return FromAudioPayload(ap);
        return Array.Empty<SpokenLine>();
    }

    /// <summary>
    /// Spoken lines from a Stage 2 beat dictionary (flat <c>dialogue</c>/<c>speaker</c>/… keys, as
    /// used by <see cref="Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats"/>).
    /// </summary>
    public static IReadOnlyList<SpokenLine> FromBeat(IReadOnlyDictionary<string, object?>? beat)
    {
        if (beat is null) return Array.Empty<SpokenLine>();
        return Build(key => beat.TryGetValue(key, out var v) ? v?.ToString() : null);
    }

    /// <summary>
    /// Spoken lines from a UI-facing <see cref="ClipSummary"/>. This is the DTO projection of the
    /// same slots; a third speaker would add its field(s) here (and to the DTO) in addition to the
    /// single <see cref="Slots"/> row.
    /// </summary>
    public static IReadOnlyList<SpokenLine> FromClip(ClipSummary? clip)
    {
        if (clip is null) return Array.Empty<SpokenLine>();
        return Build(new (string? Speaker, string? Dialogue, string? Delivery)[]
        {
            (clip.Speaker, clip.Dialogue, clip.Delivery),
            (clip.SecondarySpeaker, clip.SecondaryDialogue, null),
            (clip.TertiarySpeaker, clip.TertiaryDialogue, null),
        });
    }

    private static IReadOnlyList<SpokenLine> Build(Func<string, string?> get) =>
        Build(Slots.Select(s => (get(s.Speaker), get(s.Dialogue), get(s.Delivery))));

    private static IReadOnlyList<SpokenLine> Build(
        IEnumerable<(string? Speaker, string? Dialogue, string? Delivery)> raw)
    {
        var lines = new List<SpokenLine>();
        foreach (var (speaker, dialogue, delivery) in raw)
        {
            var line = (dialogue ?? "").Trim();
            if (line.Length == 0) continue;
            var del = (delivery ?? "").Trim();
            if (string.Equals(del, SilentDelivery, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(new SpokenLine((speaker ?? "").Trim(), line, del));
        }
        return lines;
    }
}
