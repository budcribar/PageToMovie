using System.Text;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Shared scaffolding for the per-scene beat chat classifiers (beat pacing, camera direction,
/// character emotion arc, depth of field, sound design, …). Concentrates the DI plumbing,
/// <see cref="IsEnabled"/> gate, coverage-retry invocation loop, and error logging that every one
/// of these classifiers repeats identically. Subclasses supply only the varying pieces: the option
/// flag, default model, system prompt, chat mode, operation/logging names, the per-classifier user
/// prompt serialization, and the JSON parse into the result record.
/// </summary>
/// <typeparam name="TItem">The per-beat result value (e.g. an int duration or a directive record).</typeparam>
public abstract class BeatChatClassifierBase<TItem>
{
    protected readonly IChatClient _chat;
    protected readonly PageToMovieOptions _opts;
    protected readonly ILogger _log;
    protected readonly GenerationErrorLogger? _errorLogger;

    protected BeatChatClassifierBase(
        IChatClient chat,
        PageToMovieOptions opts,
        ILogger log,
        GenerationErrorLogger? errorLogger)
    {
        _chat = chat;
        _opts = opts;
        _log = log;
        _errorLogger = errorLogger;
    }

    /// <summary>Whether this classifier's option flag is set (before the configured-chat check).</summary>
    protected abstract bool OptionEnabled { get; }

    public bool IsEnabled => OptionEnabled && _chat.IsConfigured;

    /// <summary>Default model used when the caller does not pass an explicit one.</summary>
    protected abstract string DefaultModel { get; }

    /// <summary>The <see cref="ChatCallModes"/> value tagging this classifier's chat calls.</summary>
    protected abstract string? ChatMode { get; }

    /// <summary>Stable operation name recorded by the coverage-retry policy.</summary>
    protected abstract string OperationName { get; }

    /// <summary>Classifier name recorded by <see cref="GenerationErrorLogger"/>.</summary>
    protected abstract string ErrorLoggerName { get; }

    /// <summary>Human noun used in the failure warning (e.g. "beat pacing").</summary>
    protected abstract string LogNoun { get; }

    /// <summary>The system prompt for this classifier.</summary>
    protected abstract string GetSystemPrompt();

    /// <summary>Progress message shown when classification starts.</summary>
    protected abstract string ProgressMessage(int beatCount);

    /// <summary>The heading line printed above the per-beat list (e.g. "BEATS TO PACE:").</summary>
    protected abstract string BeatsHeading { get; }

    /// <summary>
    /// Serializes the scene + beats into this classifier's user prompt. The default emits the shared
    /// "SCENE n: setting" header, the classifier's <see cref="BeatsHeading"/>, then one
    /// <see cref="AppendBeat"/> block per beat. Classifiers customize per-beat fields by overriding
    /// <see cref="AppendBeat"/>; only a classifier with a wholly different layout overrides this.
    /// </summary>
    protected virtual string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine();
        sb.AppendLine(BeatsHeading);

        foreach (var b in beats)
            AppendBeat(sb, b);

        return sb.ToString();
    }

    /// <summary>Id / action / speaker / dialogue — the fields every beat prompt reads.</summary>
    protected static (object Id, object Action, object Speaker, object Dialogue) ReadBeatCore(Dictionary<string, object?> b) =>
        (b.GetValueOrDefault("beat_id") ?? "b",
         b.GetValueOrDefault("visual_event") ?? "",
         b.GetValueOrDefault("speaker") ?? "",
         b.GetValueOrDefault("dialogue") ?? "");

    /// <summary>
    /// Appends one beat's serialized block. The default emits the standard
    /// <c>Beat 'id' (class: …)</c> header plus the shared spoken/action-prose lines; classifiers with
    /// extra or differently-shaped fields override this.
    /// </summary>
    protected virtual void AppendBeat(StringBuilder sb, Dictionary<string, object?> b)
    {
        var (id, action, spk, dlg) = ReadBeatCore(b);
        var ac = b.GetValueOrDefault("action_class") ?? "";

        sb.AppendLine($"Beat '{id}' (class: {ac}):");
        AppendSpoken(sb, spk, dlg);
        AppendActionProse(sb, action);
    }

    /// <summary>Appends the standard <c>Spoken (speaker): "dialogue"</c> line when either is present.</summary>
    protected static void AppendSpoken(StringBuilder sb, object? spk, object? dlg)
    {
        if (!string.IsNullOrWhiteSpace(spk?.ToString()) || !string.IsNullOrWhiteSpace(dlg?.ToString()))
            sb.AppendLine($"  Spoken ({spk}): \"{dlg}\"");
    }

    /// <summary>Appends the standard <c>Action prose: …</c> line when action text is present.</summary>
    protected static void AppendActionProse(StringBuilder sb, object? action)
    {
        if (!string.IsNullOrWhiteSpace(action?.ToString()))
            sb.AppendLine($"  Action prose: {action}");
    }

    /// <summary>Parses the raw chat JSON into an id→value map (null if nothing parsed).</summary>
    protected abstract Dictionary<string, TItem>? ParseResponse(string rawJson);

    /// <summary>
    /// Runs the shared classify-with-coverage-retry loop and error logging. Subclasses call this
    /// from their public <c>ClassifySceneXxxAsync</c> method.
    /// </summary>
    protected async Task<Dictionary<string, TItem>?> ClassifyAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress,
        CancellationToken ct,
        string? model)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke(ProgressMessage(beats.Count));

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : DefaultModel;
            var requestedIds = beats
                .Select(b => b.GetValueOrDefault("beat_id")?.ToString() ?? "")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            var retry = await AiRetryPolicy.RunWithCoverageRetryAsync(
                requestedIds,
                missingIds => _chat.CompleteAsync(
                    GetSystemPrompt(),
                    AiRetryPolicy.FocusCoveragePrompt(userPrompt, requestedIds, missingIds),
                    effectiveModel,
                    // 0, not 0.2: this is a categorical labeling task (same input should get the
                    // same label), and 0 is what makes CachingChatClient treat it as cacheable by
                    // default — a cancelled/retried Stage2 job re-hits already-classified scenes
                    // instead of re-querying and re-billing them.
                    temperature: 0,
                    ct: ct,
                    mode: ChatMode),
                ParseResponse,
                maxAttempts: AiRetryPolicy.DefaultCoverageMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultCoverageBackoffMs,
                ct: ct,
                operationName: OperationName,
                promptVersion: "1",
                model: effectiveModel).ConfigureAwait(false);

            if (_errorLogger is not null)
            {
                var sceneNum = ToIntOrNull(scene.GetValueOrDefault("scene_number"));
                await _errorLogger.LogCoverageResultAsync(
                    ErrorLoggerName, effectiveModel, ResolveProvider(effectiveModel), sceneNum,
                    requestedIds, retry, ct).ConfigureAwait(false);
            }

            return retry.Result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI {Classifier} classification for scene {Scene}", LogNoun, scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    protected static int? ToIntOrNull(object? val) => ClassifierValueHelpers.ToIntOrNull(val);

    protected static string? ResolveProvider(string? model) => ClassifierValueHelpers.ResolveProvider(model);
}
