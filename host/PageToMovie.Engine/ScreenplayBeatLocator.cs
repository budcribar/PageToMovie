using PageToMovie.Core.Utils;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Finds the screenplay paragraph a planned clip came from, so an edit to the clip can be an edit
/// to the story rather than a note that a replan will overwrite.
/// </summary>
/// <remarks>
/// Deleting a clip only removes a blueprint row. The beat is still in the Fountain, so the next
/// replan of that scene plans it again — Mary19 scene 3 lost the same clip twice for exactly that
/// reason. Reaching the paragraph is what makes the deletion stick.
///
/// <para>The mapping deliberately runs the real Stage 1 importer rather than recomputing beat ids
/// from the editor model. The id is hashed over the importer's own view of a beat — its inferred
/// action class as the kind, its resolved character key as the speaker, a monologue's full text
/// before splitting — and a second implementation of all that would be a copy of the importer that
/// silently diverges. Running the importer over the same screenplay is the only way to be sure the
/// ids being matched are the ids that were planned.</para>
///
/// <para>The importer's beat is then matched into the editor model by scene and text, which is a
/// plain comparison rather than a second derivation.</para>
/// </remarks>
public static class ScreenplayBeatLocator
{
    /// <param name="SceneIndex">Index into <see cref="ScreenplayModel.Scenes"/>.</param>
    /// <param name="BeatIndex">First paragraph, indexing that scene's <see cref="ScreenplayScene.Beats"/>.</param>
    /// <param name="Count">
    /// How many consecutive paragraphs the beat covers. Usually one, but the importer accumulates
    /// consecutive action paragraphs into a single beat, so one beat can span several lines.
    /// </param>
    /// <param name="SceneNumber">The Stage 1 scene number the beat belongs to.</param>
    public sealed record Location(int SceneIndex, int BeatIndex, int Count, int SceneNumber);

    /// <summary>
    /// Locates the paragraphs behind a clip's beat ids. Ids that resolve to the same paragraph
    /// collapse to one entry — a monologue split across clips is one line in the screenplay.
    /// </summary>
    /// <param name="unresolved">Ids that matched no paragraph. Never guessed at.</param>
    public static IReadOnlyList<Location> Locate(
        string? fountainText,
        ScreenplayModel model,
        IEnumerable<string> stage1BeatIds,
        out IReadOnlyList<string> unresolved)
    {
        var misses = new List<string>();
        unresolved = misses;
        var hits = new List<Location>();
        if (string.IsNullOrWhiteSpace(fountainText))
        {
            misses.AddRange(stage1BeatIds.Where(id => !string.IsNullOrWhiteSpace(id)));
            return hits;
        }

        var byId = IndexStage1Beats(fountainText);
        var seen = new HashSet<(int, int)>();
        foreach (var rawId in stage1BeatIds)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                continue;
            // A split monologue's parts all carry the same root, and the screenplay holds one line
            // for the whole speech — so every part resolves to the same paragraph.
            var root = StableBeatId.Root(rawId);
            if (!byId.TryGetValue(root, out var beat) || LocateInModel(model, beat) is not { } found)
            {
                misses.Add(rawId);
                continue;
            }
            if (seen.Add((found.SceneIndex, found.BeatIndex)))
                hits.Add(found);
        }
        return hits;
    }

    /// <summary>Root beat id → the importer's view of that beat.</summary>
    private static Dictionary<string, Stage1Beat> IndexStage1Beats(string fountainText)
    {
        var index = new Dictionary<string, Stage1Beat>(StringComparer.OrdinalIgnoreCase);
        var stage1 = ScreenplayService.BuildModelFromFountainText(fountainText);
        if (stage1.GetValueOrDefault("scenes") is not List<object?> scenes)
            return index;

        foreach (var sceneObj in scenes)
        {
            if (sceneObj is not Dictionary<string, object?> scene)
                continue;
            var sceneNumber = ToInt(scene.GetValueOrDefault("scene_number"));
            if (scene.GetValueOrDefault("story_beats") is not List<object?> beats)
                continue;
            foreach (var beatObj in beats)
            {
                if (beatObj is not Dictionary<string, object?> beat)
                    continue;
                var id = StableBeatId.Root(beat.GetValueOrDefault("beat_id")?.ToString());
                if (string.IsNullOrWhiteSpace(id) || index.ContainsKey(id))
                    continue;
                index[id] = new Stage1Beat(
                    sceneNumber,
                    Str(beat.GetValueOrDefault("dialogue")),
                    Str(beat.GetValueOrDefault("speaker")),
                    Str(beat.GetValueOrDefault("visual_event")));
            }
        }
        return index;
    }

    /// <summary>
    /// Matches the importer's beat to the paragraph — or run of paragraphs — behind it, by text.
    /// </summary>
    /// <remarks>
    /// Dialogue matches a spoken paragraph one-to-one. Action does not: the importer accumulates
    /// consecutive action paragraphs and flushes them as ONE beat when a character cue or heading
    /// interrupts, so a beat's <c>visual_event</c> can be several screenplay lines joined. Matching
    /// only single paragraphs would leave every such beat unresolved, which is most of the action in
    /// a normally-formatted screenplay. So an action beat is matched against a growing run of
    /// consecutive action paragraphs and reports the whole span.
    ///
    /// <para>Comparison is on normalized text, so whitespace differences between the parser's view
    /// and the formatter's do not decide it. A monologue the importer split reports the FULL text,
    /// which is what the one screenplay paragraph holds, so the split parts land on it too.</para>
    /// </remarks>
    private static Location? LocateInModel(ScreenplayModel model, Stage1Beat beat)
    {
        var sceneIndex = model.Scenes.FindIndex(s => s.SceneNumber == beat.SceneNumber);
        if (sceneIndex < 0)
            return null;
        var scene = model.Scenes[sceneIndex];
        var wantsDialogue = !string.IsNullOrWhiteSpace(beat.Dialogue);
        var wanted = StableBeatId.Normalize(wantsDialogue ? beat.Dialogue : beat.VisualEvent);
        if (wanted.Length == 0)
            return null;

        for (var i = 0; i < scene.Beats.Count; i++)
        {
            var candidate = scene.Beats[i];
            if (wantsDialogue)
            {
                if (candidate.Type == BeatType.Dialogue && StableBeatId.Normalize(candidate.Text) == wanted)
                    return new Location(sceneIndex, i, 1, beat.SceneNumber);
                continue;
            }
            if (candidate.Type != BeatType.Action)
                continue;
            if (MatchActionRun(scene, i, wanted) is { } span)
                return new Location(sceneIndex, i, span, beat.SceneNumber);
        }
        return null;
    }

    /// <summary>
    /// Length of the run of action paragraphs starting at <paramref name="start"/> whose joined text
    /// is <paramref name="wanted"/>, or null when no run from here does.
    /// </summary>
    private static int? MatchActionRun(ScreenplayScene scene, int start, string wanted)
    {
        var joined = "";
        for (var i = start; i < scene.Beats.Count; i++)
        {
            if (scene.Beats[i].Type != BeatType.Action)
                return null;
            joined = joined.Length == 0
                ? StableBeatId.Normalize(scene.Beats[i].Text)
                : joined + " " + StableBeatId.Normalize(scene.Beats[i].Text);
            if (joined == wanted)
                return i - start + 1;
            if (joined.Length >= wanted.Length)
                return null;
        }
        return null;
    }

    private sealed record Stage1Beat(int SceneNumber, string Dialogue, string Speaker, string VisualEvent);

    private static string Str(object? value) => value?.ToString() ?? "";

    private static int ToInt(object? value) =>
        value switch
        {
            int i => i,
            long l => (int)l,
            _ => int.TryParse(value?.ToString(), out var p) ? p : 0,
        };
}

/// <summary>One clip's Stage 1 beat ids, as stored in the shot plan.</summary>
public sealed record SceneClipBeatIds(int ClipNumber, IReadOnlyList<string> BeatIds);
