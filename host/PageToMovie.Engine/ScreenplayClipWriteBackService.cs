using PageToMovie.Core.Models;
using PageToMovie.ScreenplayEditor.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Propagates a clip add/delete back into the approved screenplay, so the edit sticks.
/// </summary>
/// <remarks>
/// Stage 2 plans from the Fountain, so a blueprint-only delete is undone by the next replan of that
/// scene: Mary19 scene 3 lost the same clip twice, and the replan that "fixed" its beat map was
/// what brought the clip back. Removing the paragraph is what makes the deletion permanent.
///
/// <para>The screenplay is re-signed rather than left dirty. The sign-off gate exists so a shot
/// plan is never built from an unreviewed screenplay; a structured edit the user just performed on
/// a clip they are looking at is not that. Free-text edits and AI rewrites still go through
/// approval.</para>
/// </remarks>
public sealed class ScreenplayClipWriteBackService
{
    private readonly ProjectStore _store;

    public ScreenplayClipWriteBackService(ProjectStore store) => _store = store;

    /// <param name="Applied">True when the screenplay was changed and re-signed.</param>
    /// <param name="Removed">How many paragraphs were removed.</param>
    /// <param name="Unresolved">
    /// Beat ids that matched no paragraph — a clip whose prompt was hand-edited since planning, or
    /// one added before the ids were keyed consistently. Reported, never guessed at.
    /// </param>
    public sealed record WriteBackResult(bool Applied, int Removed, IReadOnlyList<string> Unresolved, string? Error = null);

    /// <summary>
    /// What deleting these clips would do to the screenplay, without doing it — the numbers a
    /// confirmation prompt needs before the user commits.
    /// </summary>
    /// <param name="Paragraphs">Screenplay paragraphs that would be removed.</param>
    /// <param name="ClipNumbers">
    /// Every clip in the scene bound to those paragraphs. A monologue split across clips brings its
    /// siblings with it: the screenplay holds one line for the speech, so removing it removes them
    /// all, and the prompt must say so rather than silently taking two extra clips.
    /// </param>
    /// <param name="EmptiesScene">
    /// True when that is every remaining clip in the scene — the delete is really a scene delete.
    /// </param>
    public sealed record DeletePreview(
        int Paragraphs,
        IReadOnlyList<int> ClipNumbers,
        bool EmptiesScene,
        IReadOnlyList<string> Unresolved);

    /// <summary>Reads the project's approved screenplay, or null when there is none.</summary>
    private string? ReadScreenplay(string projectId)
    {
        var doc = ScreenplayService.Get(_store, projectId);
        return string.IsNullOrWhiteSpace(doc.Text) ? null : doc.Text;
    }

    /// <summary>
    /// Describes the blast radius of deleting <paramref name="clipNumbers"/> from a scene.
    /// </summary>
    public DeletePreview PreviewDelete(string projectId, int sceneNumber, IReadOnlyList<int> clipNumbers)
    {
        var text = ReadScreenplay(projectId);
        var sceneClips = _store.ReadSceneClipBeatIds(projectId, sceneNumber);
        if (text is null || sceneClips.Count == 0)
            return new DeletePreview(0, clipNumbers, EmptiesScene: false, Array.Empty<string>());

        var model = FountainFormatter.Parse(text);
        var selectedIds = sceneClips
            .Where(c => clipNumbers.Contains(c.ClipNumber))
            .SelectMany(c => c.BeatIds)
            .ToList();
        var locations = ScreenplayBeatLocator.Locate(text, model, selectedIds, out var unresolved);
        var locationSet = locations.ToHashSet();

        // Any other clip bound to the same paragraph goes too — that is the split-monologue group.
        var pulled = new SortedSet<int>(clipNumbers);
        foreach (var clip in sceneClips)
        {
            if (pulled.Contains(clip.ClipNumber))
                continue;
            var theirs = ScreenplayBeatLocator.Locate(text, model, clip.BeatIds, out _);
            if (theirs.Any(locationSet.Contains))
                pulled.Add(clip.ClipNumber);
        }

        return new DeletePreview(
            locations.Count,
            pulled.ToList(),
            EmptiesScene: pulled.Count >= sceneClips.Count,
            unresolved);
    }

    /// <summary>
    /// Removes the paragraphs behind the given clips and re-signs the screenplay. Does nothing when
    /// no paragraph resolves — the caller then has a blueprint-only delete, which is honest but
    /// temporary, and should say so.
    /// </summary>
    public WriteBackResult RemoveBeatsForClips(string projectId, int sceneNumber, IReadOnlyList<int> clipNumbers)
    {
        var text = ReadScreenplay(projectId);
        if (text is null)
            return new WriteBackResult(false, 0, Array.Empty<string>(), "No screenplay to edit.");

        var sceneClips = _store.ReadSceneClipBeatIds(projectId, sceneNumber);
        var ids = sceneClips
            .Where(c => clipNumbers.Contains(c.ClipNumber))
            .SelectMany(c => c.BeatIds)
            .ToList();
        if (ids.Count == 0)
            return new WriteBackResult(false, 0, Array.Empty<string>());

        var model = FountainFormatter.Parse(text);
        var locations = ScreenplayBeatLocator.Locate(text, model, ids, out var unresolved);
        if (locations.Count == 0)
            return new WriteBackResult(false, 0, unresolved);

        // Highest index first so earlier removals do not shift the ones still to come.
        foreach (var loc in locations.OrderByDescending(l => l.SceneIndex).ThenByDescending(l => l.BeatIndex))
            model.Scenes[loc.SceneIndex].Beats.RemoveRange(loc.BeatIndex, loc.Count);

        return Commit(projectId, model, locations.Count, unresolved);
    }

    /// <summary>
    /// Adds a paragraph for a hand-added clip, positioned by the clips that surround it, and
    /// re-signs. Without this the clip exists only in the plan and the next replan drops it.
    /// </summary>
    public WriteBackResult AddBeatForClip(string projectId, int sceneNumber, ClipEditRequest fields)
    {
        var text = ReadScreenplay(projectId);
        if (text is null)
            return new WriteBackResult(false, 0, Array.Empty<string>(), "No screenplay to edit.");

        var model = FountainFormatter.Parse(text);
        var sceneIndex = model.Scenes.FindIndex(s => s.SceneNumber == sceneNumber);
        if (sceneIndex < 0)
            return new WriteBackResult(false, 0, Array.Empty<string>(), $"Scene {sceneNumber} is not in the screenplay.");

        var spoken = !string.IsNullOrWhiteSpace(fields.Dialogue);
        var beat = new ScreenplayBeat
        {
            Type = spoken ? BeatType.Dialogue : BeatType.Action,
            Speaker = spoken ? (fields.Speaker ?? "").Trim() : "",
            Text = (spoken ? fields.Dialogue : fields.VisualPrompt).Trim(),
        };
        if (beat.Text.Length == 0)
            return new WriteBackResult(false, 0, Array.Empty<string>(), "The clip has no text to put in the screenplay.");

        model.Scenes[sceneIndex].Beats.Insert(
            ResolveInsertIndex(projectId, sceneNumber, fields.Clip, text, model, sceneIndex), beat);

        return Commit(projectId, model, 1, Array.Empty<string>());
    }

    /// <summary>
    /// Where the new paragraph belongs: after the paragraph of the last preceding clip that
    /// resolves, else at the end. Clip numbers order the plan, so the clip before this one in
    /// number order is the beat this should follow.
    /// </summary>
    private int ResolveInsertIndex(
        string projectId, int sceneNumber, int clipNumber, string text, ScreenplayModel model, int sceneIndex)
    {
        var before = _store.ReadSceneClipBeatIds(projectId, sceneNumber)
            .Where(c => c.ClipNumber < clipNumber)
            .OrderByDescending(c => c.ClipNumber)
            .ToList();
        foreach (var clip in before)
        {
            var found = ScreenplayBeatLocator.Locate(text, model, clip.BeatIds, out _)
                .Where(l => l.SceneIndex == sceneIndex)
                .OrderByDescending(l => l.BeatIndex)
                .FirstOrDefault();
            if (found is not null)
                return found.BeatIndex + found.Count;
        }
        return model.Scenes[sceneIndex].Beats.Count;
    }

    /// <summary>
    /// Removes a scene from the screenplay and pins every surviving scene's number in the Fountain
    /// so nothing renumbers, then re-signs.
    /// </summary>
    /// <remarks>
    /// Scene numbers in a screenplay are ordinal unless a heading carries an explicit
    /// <c>#N#</c>, but the blueprint deliberately does not renumber when a scene is deleted —
    /// renumbering would mean renaming every later scene's video files. Without pinning, deleting
    /// scene 4 shifts screenplay scene 5 to 4 while the blueprint still calls it 5, and the next
    /// replan merges by number and lands one scene's plan on another's clips. Stamping the numbers
    /// makes the screenplay agree with the blueprint permanently.
    /// </remarks>
    public WriteBackResult RemoveScene(string projectId, int sceneNumber)
    {
        var text = ReadScreenplay(projectId);
        if (text is null)
            return new WriteBackResult(false, 0, Array.Empty<string>(), "No screenplay to edit.");

        var model = FountainFormatter.Parse(text);
        var index = model.Scenes.FindIndex(s => s.SceneNumber == sceneNumber);
        if (index < 0)
            return new WriteBackResult(false, 0, Array.Empty<string>(), $"Scene {sceneNumber} is not in the screenplay.");

        model.Scenes.RemoveAt(index);
        PinSceneNumbers(model);
        return Commit(projectId, model, 1, Array.Empty<string>());
    }

    /// <summary>
    /// Adds a scene heading to the screenplay at the number the blueprint gave it, pinning numbers
    /// so the two stay in step, then re-signs.
    /// </summary>
    public WriteBackResult AddScene(string projectId, int sceneNumber, string? setting)
    {
        var text = ReadScreenplay(projectId);
        if (text is null)
            return new WriteBackResult(false, 0, Array.Empty<string>(), "No screenplay to edit.");

        var model = FountainFormatter.Parse(text);
        if (model.Scenes.Any(s => s.SceneNumber == sceneNumber))
            return new WriteBackResult(false, 0, Array.Empty<string>(), $"Scene {sceneNumber} is already in the screenplay.");

        var scene = new ScreenplayScene { SceneNumber = sceneNumber };
        if (!string.IsNullOrWhiteSpace(setting))
            scene.SceneTitle = setting.Trim();
        var at = model.Scenes.FindIndex(s => s.SceneNumber > sceneNumber);
        if (at < 0)
            model.Scenes.Add(scene);
        else
            model.Scenes.Insert(at, scene);

        PinSceneNumbers(model);
        return Commit(projectId, model, 1, Array.Empty<string>());
    }

    /// <summary>
    /// Writes every scene's number into its heading, so ordinal position stops deciding identity.
    /// </summary>
    private static void PinSceneNumbers(ScreenplayModel model)
    {
        foreach (var scene in model.Scenes)
        {
            if (scene.SceneNumber > 0)
                scene.HasExplicitSceneNumber = true;
        }
    }

    /// <summary>Serializes, saves and re-signs in one step so the screenplay never sits unapproved.</summary>
    private WriteBackResult Commit(
        string projectId, ScreenplayModel model, int changed, IReadOnlyList<string> unresolved)
    {
        var next = model.ToFountain();
        var signed = ScreenplayService.SignOff(_store, projectId, next);
        return signed.Ok
            ? new WriteBackResult(true, changed, unresolved)
            : new WriteBackResult(false, 0, unresolved, signed.Error);
    }
}
