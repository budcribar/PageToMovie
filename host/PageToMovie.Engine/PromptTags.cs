using System.Text.RegularExpressions;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Single source of truth for the &lt;Tag&gt;content&lt;/Tag&gt; delimiter convention used
/// throughout clip video prompts (Voice, VoiceLock, VisualLock, Camera, Performance, Optics,
/// Audio, Score, Ambient, Foley, Negative, Identity, Pronunciation, CastCount, Characters,
/// Context, Clip, Continuity, PreviousClip, Look) — see <see cref="ClipVideoPromptBuilder"/>,
/// <see cref="Stage2PlannerService"/>, <see cref="VoicePreviewService"/>.
///
/// <see cref="Wrap"/>/<see cref="WrapWithNote"/> are pure structural assembly — they do NOT
/// sanitize content, because several tags legitimately nest others (Audio wraps Score/Ambient/
/// Foley/VoiceLock; Context/PreviousClip re-embed a previous clip's visual_prompt, which may
/// itself contain Camera/Performance/Optics tags; VoicePreviewService's Look wraps VisualLock).
/// Blanket-sanitizing at the outer wrap would destroy those inner tags. Instead, call
/// <see cref="SanitizeValue"/> on each untrusted LEAF value (AI-generated text, free-form cast/
/// dialogue fields) at the point it's produced, before it becomes tag content or gets
/// interpolated into a larger block — this is what actually fixes the bug the ad-hoc per-call-
/// site interpolation had: a value that happened to contain a literal '&lt;' or '&gt;' could
/// prematurely close a tag or corrupt a downstream regex boundary (the exact class of bug already
/// fixed once for the bare "Voice:"/"VOICE LOCK" text labels these tags replaced).
/// </summary>
public static class PromptTags
{
    /// <summary>Wrap already-safe content in a tag: &lt;Name&gt;content&lt;/Name&gt;.</summary>
    public static string Wrap(string name, string? content) => $"<{name}>{content}</{name}>";

    /// <summary>Wrap already-safe content in a tag carrying a descriptive "note" attribute
    /// (dropped during compression — see <see cref="StripNotes"/>):
    /// &lt;Name note="..."&gt;content&lt;/Name&gt;.</summary>
    public static string WrapWithNote(string name, string? note, string? content) =>
        $"<{name} note=\"{SanitizeAttr(note)}\">{content}</{name}>";

    /// <summary>Bare opening tag for a section header whose content is the rest of the prompt up
    /// to the next section, not a single bounded span: &lt;Name&gt;.</summary>
    public static string Open(string name) => $"<{name}>";

    /// <summary>Opening tag with a "note" attribute, no matching close: &lt;Name note="..."&gt;.</summary>
    public static string OpenWithNote(string name, string? note) =>
        $"<{name} note=\"{SanitizeAttr(note)}\">";

    /// <summary>Remove every &lt;Name&gt;...&lt;/Name&gt; span (and any leading whitespace) from
    /// text — used to fully drop non-essential tagged content during compression (e.g. Voice,
    /// VoiceLock: visual video models don't use voice-tuning text).</summary>
    public static string Strip(string text, string name) =>
        CommonRegex.Replace(text, $@"\s*<{name}>.*?</{name}>", "", RegexOptions.Singleline);

    /// <summary>Drop every tag's "note" attribute — the full instructional wording is only needed
    /// in the uncompressed prompt; the bare tag name is enough once budget is tight.</summary>
    public static string StripNotes(string text) =>
        CommonRegex.Replace(text, @"\s+note=""[^""]*""", "");

    /// <summary>
    /// Strip literal '&lt;'/'&gt;' from an untrusted LEAF value (an AI classifier's text, a cast
    /// profile field, a free-form pronunciation hint typed into the clip editor, dialogue) before
    /// it becomes tag content or an attribute. Call this at the point each individual value is
    /// produced — never on an already-assembled block that may legitimately contain nested tags
    /// (see the class doc comment); doing so would corrupt those tags instead of protecting them.
    /// </summary>
    public static string SanitizeValue(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("<", "").Replace(">", "");

    private static string SanitizeAttr(string? text) => SanitizeValue(text).Replace("\"", "'");
}
