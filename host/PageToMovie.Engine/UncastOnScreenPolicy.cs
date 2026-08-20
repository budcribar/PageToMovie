using System.Text.Json;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// What to do with an on-screen character the shot plan names but the cast does not list
/// (no profile, so no portrait can ever be locked). Product rule (2026-08-18):
/// <list type="bullet">
/// <item>An extra — non-speaking in this clip and appearing in at most one clip of the whole
/// plan — renders from its text description. Identity drift can't show in a single clip.</item>
/// <item>A role that speaks, or recurs across clips, must be cast (Characters → add role, lock a
/// portrait) — text-only prompting would give it a different face every clip.</item>
/// </list>
/// Groups/crowds are handled upstream (never need a portrait); locked-cast members keep the
/// fail-fast lock check.
/// </summary>
public static class UncastOnScreenPolicy
{
    public enum Verdict { TextOnlyExtra, MustBeCast }

    public sealed record Decision(string Key, Verdict Kind, int ClipAppearances, bool HasSpeech)
    {
        public bool TextOnly => Kind == Verdict.TextOnlyExtra;
    }

    /// <summary>Decide for one un-cast on-screen key of <paramref name="clipEl"/> within <paramref name="blueprintRoot"/>.</summary>
    public static Decision Decide(string key, JsonElement clipEl, JsonElement? blueprintRoot)
    {
        var speaks = SpeaksInClip(key, clipEl);
        var appearances = blueprintRoot is { } root ? CountClipsOnScreen(root, key) : 1;
        if (appearances < 1) appearances = 1; // the clip we are building counts
        var verdict = !speaks && appearances <= 1 ? Verdict.TextOnlyExtra : Verdict.MustBeCast;
        return new Decision(key, verdict, appearances, speaks);
    }

    /// <summary>True when the clip's audio payload names <paramref name="key"/> as speaker.</summary>
    public static bool SpeaksInClip(string key, JsonElement clipEl)
    {
        if (clipEl.ValueKind != JsonValueKind.Object) return false;
        if (!clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) || ap.ValueKind != JsonValueKind.Object)
            return false;
        if (ap.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String
            && string.Equals(sp.GetString()?.Trim(), key, StringComparison.OrdinalIgnoreCase))
            return true;
        // Multi-line payloads: any line whose speaker is this key.
        if (ap.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in lines.EnumerateArray())
            {
                if (l.ValueKind == JsonValueKind.Object && l.TryGetProperty("speaker", out var ls)
                    && ls.ValueKind == JsonValueKind.String
                    && string.Equals(ls.GetString()?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Number of clips across the whole shot plan whose <c>characters_on_screen</c> names <paramref name="key"/>.</summary>
    public static int CountClipsOnScreen(JsonElement blueprintRoot, string key)
    {
        try
        {
            if (blueprintRoot.ValueKind != JsonValueKind.Object) return 0;
            if (!blueprintRoot.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
                return 0;
            return CountKeyInScenes(scenes, key);
        }
        catch { /* malformed plan: treat as unknown → 0 */ }
        return 0;
    }

    private static int CountKeyInScenes(JsonElement scenes, string key)
    {
        var count = 0;
        foreach (var s in scenes.EnumerateArray())
        {
            if (!s.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                continue;
            count += CountKeyInClips(clips, key);
        }
        return count;
    }

    private static int CountKeyInClips(JsonElement clips, string key) =>
        clips.EnumerateArray().Count(c => ClipListsKeyOnScreen(c, key));

    private static bool ClipListsKeyOnScreen(JsonElement clip, string key)
    {
        if (!clip.TryGetProperty("characters_on_screen", out var cos) || cos.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var x in cos.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String
                && string.Equals(x.GetString()?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Keeps <c>Decision.Verdict</c> and <c>Decision.SpeaksInClip</c> compiling after S3218
/// renames to <see cref="UncastOnScreenPolicy.Decision.Kind"/> and
/// <see cref="UncastOnScreenPolicy.Decision.HasSpeech"/>.
/// </summary>
public static class UncastOnScreenDecisionExtensions
{
    extension(UncastOnScreenPolicy.Decision decision)
    {
        public UncastOnScreenPolicy.Verdict Verdict => decision.Kind;
        public bool SpeaksInClip => decision.HasSpeech;
    }
}
