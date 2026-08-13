using System.Text.Json;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Deterministic;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Model-backed action classifier with validated correction and deterministic fallback.
/// Any caller depending on this namespace can initiate a paid model request.
/// </summary>
public sealed class AiActionOverheadClassifier
{
    private readonly ValidatedModelOperation<ActionInput, ActionClassifierEstimation>? _pipeline;

    public AiActionOverheadClassifier(
        SmartClassifierModelRouter router,
        ActionCameraOverheadLedger ledger,
        IChatClient? chat = null,
        ILogger<AiActionOverheadClassifier>? log = null,
        string? modelOverride = null)
    {
        _ = ledger;
        if (chat is null || !chat.IsConfigured)
            return;

        _pipeline = new ValidatedModelOperation<ActionInput, ActionClassifierEstimation>(
            new ActionModelOperation(chat, router, log, modelOverride),
            new ActionResponseParser(),
            new ActionResultValidator(),
            new ActionFallback(),
            new ModelOperationOptions
            {
                CorrectiveMaxAttempts = 1,
                BehaviorVersions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["action_ledger"] = "1",
                },
            });
    }

    public async Task<ActionClassifierEstimation> ClassifyNovelActionAsync(
        string actionDescription,
        string? parenthetical = null,
        CancellationToken ct = default)
    {
        var result = await ClassifyNovelActionWithProvenanceAsync(actionDescription, parenthetical, ct)
            .ConfigureAwait(false);
        return result.Value ?? ActionOverheadHeuristic.Classify(actionDescription, parenthetical);
    }

    public Task<ValidatedModelResult<ActionClassifierEstimation>> ClassifyNovelActionWithProvenanceAsync(
        string actionDescription,
        string? parenthetical = null,
        CancellationToken ct = default)
    {
        var input = new ActionInput(actionDescription ?? "", parenthetical);
        if (_pipeline is not null && !string.IsNullOrWhiteSpace(input.CombinedText))
            return _pipeline.ExecuteAsync(input, ct);

        var value = ActionOverheadHeuristic.Classify(input.ActionDescription, input.Parenthetical);
        return Task.FromResult(new ValidatedModelResult<ActionClassifierEstimation>(
            value,
            ModelResultSource.DeterministicFallback,
            "action_overhead_classifier",
            null,
            Array.Empty<ModelOperationAttempt>(),
            Array.Empty<ModelValidationIssue>(),
            null));
    }

    public ActionClassifierEstimation ClassifyNovelAction(string actionDescription, string? parenthetical = null) =>
        ActionOverheadHeuristic.Classify(actionDescription, parenthetical);

    public ActionClassifierEstimation ClassifyNovelActionHeuristic(string actionDescription, string? parenthetical = null) =>
        ActionOverheadHeuristic.Classify(actionDescription, parenthetical);

    private sealed record ActionInput(string ActionDescription, string? Parenthetical)
    {
        public string CombinedText => $"{ActionDescription} {Parenthetical}".Trim();
    }

    private sealed class ActionModelOperation(
        IChatClient chat,
        SmartClassifierModelRouter router,
        ILogger<AiActionOverheadClassifier>? log,
        string? modelOverride)
        : IModelOperation<ActionInput, string>
    {
        public string OperationName => "action_overhead_classifier";
        public string PromptVersion => "1";

        public async Task<ModelResponse<string>> ExecuteAsync(
            ActionInput input,
            ModelAttemptContext<string> context,
            CancellationToken ct)
        {
            var model = router.ResolveOptimalModelForTask("screenplay_adaptation", modelOverride);
            var correction = context.Kind == ModelAttemptKind.Correction
                ? $"""

                   The previous response was invalid:
                   {string.Join("\n", context.ValidationIssues.Select(issue => $"- {issue.Path ?? "$"}: {issue.Message}"))}
                   Return a corrected complete JSON object only. Previous response:
                   {context.PreviousResponse}
                   """
                : "";
            var user = $"""
                Classify this action beat.
                Action: "{input.ActionDescription}"
                Parenthetical: "{input.Parenthetical ?? ""}"
                {correction}
                """;

            log?.LogInformation(
                "[ActionClassifier] {AttemptKind} request via {Model}",
                context.Kind,
                model);
            var raw = await chat.CompleteAsync(
                SystemPrompt,
                user,
                model,
                temperature: 0.1,
                ct: ct,
                mode: "action_timing_classifier").ConfigureAwait(false);
            return new ModelResponse<string>(raw, model);
        }
    }

    private sealed class ActionResponseParser : IModelResponseParser<string, ActionClassifierEstimation>
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public ModelParseResult<ActionClassifierEstimation> Parse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return ModelParseResult<ActionClassifierEstimation>.Failure(
                    new ModelValidationIssue("empty_response", "The response was empty."));
            try
            {
                var json = ClassifierJsonParser.StripFences(response);
                var value = JsonSerializer.Deserialize<ActionClassifierEstimation>(json, Options);
                return value is null
                    ? ModelParseResult<ActionClassifierEstimation>.Failure(
                        new ModelValidationIssue("null_result", "The JSON result was null."))
                    : ModelParseResult<ActionClassifierEstimation>.Success(value);
            }
            catch (JsonException ex)
            {
                return ModelParseResult<ActionClassifierEstimation>.Failure(
                    new ModelValidationIssue("invalid_json", ex.Message));
            }
        }
    }

    private sealed class ActionResultValidator : IModelResultValidator<ActionClassifierEstimation>
    {
        public IReadOnlyList<ModelValidationIssue> Validate(ActionClassifierEstimation result)
        {
            var issues = new List<ModelValidationIssue>();
            if (string.IsNullOrWhiteSpace(result.MatchCategoryId))
                issues.Add(new("missing_category", "matchCategoryId is required.", "$.matchCategoryId"));
            else if (ActionCameraOverheadLedger.GetOverheadSec(result.MatchCategoryId.Trim(), -1) <= 0)
                issues.Add(new("unknown_category", "matchCategoryId is not in the calibrated ledger.", "$.matchCategoryId"));
            if (double.IsNaN(result.ConfidenceScore) || result.ConfidenceScore < 0 || result.ConfidenceScore > 1)
                issues.Add(new("invalid_confidence", "confidenceScore must be between 0 and 1.", "$.confidenceScore"));
            return issues;
        }
    }

    private sealed class ActionFallback : IDeterministicFallback<ActionInput, ActionClassifierEstimation>
    {
        public ActionClassifierEstimation Create(ActionInput input, IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
            ActionOverheadHeuristic.Classify(input.ActionDescription, input.Parenthetical);
    }

    private const string SystemPrompt = """
        You classify film actions into one calibrated timing category.
        Allowed category ids:
        cam_push_in, cam_whip_pan, cam_tracking_dolly, cam_crane_canopy,
        act_pills_sorting, act_knife_pull, act_stabbing, act_choke_wall,
        act_heavy_carry, act_weightlifting, act_running_panic, act_creeping_step,
        act_lantern_unshutter, act_sudden_shriek, act_floorboard_dismantle,
        act_creature_pounce, act_creature_stalk, act_vine_swing,
        react_gasp_shock, react_confused_stare, react_heart_pounding,
        react_creature_roar, car_muscle_drive, car_broadside_crash,
        car_ferry_ride, scene_visitation_room, act_yoga_pose,
        dream_viking_battle, dream_lake_goddess, combo_pills_and_snivel,
        combo_weights_and_taunt, combo_knife_and_threat, combo_drive_and_talk,
        combo_bar_and_confront, combo_yoga_and_explain, act_generic_action.
        Return JSON only:
        {"matchCategoryId":"category_id","estimatedOverheadSec":0.0,"confidenceScore":0.0,"explanation":"short rationale"}
        """;
}
