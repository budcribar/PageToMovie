namespace PageToMovie.Core.Utils;

/// <summary>
/// Assigns <see cref="StableBeatId"/> values to a scene's beats in reading order, counting repeats
/// so two identical lines in one scene get distinct ids.
/// </summary>
/// <remarks>
/// Extracted from the Stage 1 Fountain importer so anything that needs to map a planned clip back
/// to the screenplay paragraph that produced it can recompute the same ids from the same screenplay.
/// It has to be one implementation: a second copy that drifted by a single normalization detail
/// would produce hashes that match nothing, and a "delete this clip's line from the screenplay"
/// feature built on it would silently do nothing rather than fail.
///
/// <para>The scene key is the scene's <c>setting</c> heading, not its number. Scene numbers move
/// when a scene is inserted or removed; the heading does not, so ids survive those edits. Anything
/// minting an id outside the importer must use the same key or its ids can never match.</para>
/// </remarks>
public sealed class BeatIdSequencer
{
    private readonly Dictionary<string, int> _occurrence = new(StringComparer.Ordinal);

    /// <summary>Scene key for a scene, given its heading and 1-based number.</summary>
    /// <remarks>
    /// Mirrors the importer exactly: the heading when there is one, else <c>scene:N</c>. Callers
    /// that only know the number pass a null/empty setting and get the same fallback the importer
    /// would have used for an untitled scene.
    /// </remarks>
    public static string SceneKey(string? setting, int sceneNumber) =>
        !string.IsNullOrWhiteSpace(setting)
            ? setting
            : $"scene:{(sceneNumber > 0 ? sceneNumber : 0)}";

    /// <summary>Clears the repeat counter. Call when a new scene starts.</summary>
    public void Reset() => _occurrence.Clear();

    /// <summary>
    /// Next id for a beat in <paramref name="sceneKey"/>, advancing that content's repeat counter.
    /// </summary>
    public string Next(string sceneKey, string kind, string? speaker, string? body)
    {
        var key = string.Join(
            '\u001f',
            StableBeatId.Normalize(kind),
            StableBeatId.Normalize(speaker),
            StableBeatId.Normalize(body));
        _occurrence.TryGetValue(key, out var n);
        _occurrence[key] = n + 1;
        return StableBeatId.ForContent(sceneKey, kind, speaker, body, n);
    }
}
