# Clip-duration calibration

Use this loop to improve `ClipDurationEstimator` from measured output without weakening dialogue safety or
learning provider quirks as universal rules.

The offline analyzer is `host/tools/ClipDurationCalibration`. It makes **no network or paid API calls**. It
recursively discovers project snapshots, replays the current estimator against each blueprint clip, and compares
the result with that clip's measured duration sidecar.

## Ground-truth contract

A scored sample must have all of the following in the same project snapshot:

- `blueprint.clips.grok.json` with `scene_number`, `clip_number`, `duration_seconds`, dialogue, delivery,
  `action_class`, continuation source, and the visual prompt;
- `pipeline_config.json` with the actual `model_name` used for generation;
- a valid `assets/video/scene_NN_clip_NN.mp4.duration.json` containing a positive numeric `seconds` value;
- an exact scene/clip-number match between the current blueprint and the sidecar.

Do not score scene composites, orphan sidecars, requested duration, file size, timestamps, or a guessed “valid
clip” threshold as actual clip duration. Scene composites are useful for playback checks but cannot identify
which clip caused an estimation error.

Keep provider/model versions and materially different generation eras as separate cohorts. For example, an old
provider that always returned about 10 seconds from a 15-second request measures that provider behavior—not the
current estimator. The analyzer reports results by model, but the operator must still split or exclude cohorts
when provider behavior changed without a model-id change.

## Rerun

From the repository root:

```powershell
dotnet run --project host/tools/ClipDurationCalibration -- "C:\path\to\project-or-backup-root"
```

Add per-clip rows ordered by largest error:

```powershell
dotnet run --project host/tools/ClipDurationCalibration -- "C:\path\to\project-or-backup-root" --details
```

Run the estimator regression tests after every candidate change:

```powershell
dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter `
  "FullyQualifiedName~ClipDurationEstimatorTests|FullyQualifiedName~ActionCameraOverheadLedgerTests|FullyQualifiedName~ClipSilenceTrimmerTests"
```

These commands are offline. Do not enable `PAGETOMOVIE_LIVE_API_TESTS` for calibration.

## Improvement loop

1. Preserve the analyzer's baseline output with the code revision, corpus path, model/provider cohort, eligible
   clip count, aggregate bias, MAE, and RMSE.
2. Inspect the largest absolute errors using `--details`. Look for a repeated semantic cause across projects,
   not a title, scene, character, or model-id special case.
3. Change the smallest general rule that explains the repeated errors. Provider limits belong in
   `models_catalog.json`; estimator logic must consume capabilities rather than branch on model names.
4. Add a generic regression test that fails for the identified cause and a counter-test proving required safety
   behavior remains.
5. Rerun the same eligible cohort, then a holdout cohort from different stories. Record both aggregate bias and
   per-clip MAE/RMSE; aggregate totals alone can hide offsetting errors.
6. Reject a change that reduces timing error by risking truncated dialogue, ignoring multi-speaker handoffs,
   exceeding catalog limits, or degrading a meaningful subgroup such as fresh clips, extensions, voice-over,
   on-camera speech, silent action, or large action.
7. Add newly generated projects to the corpus only after their per-clip duration sidecars and generating model
   metadata are present. More data helps only when it satisfies the same ground-truth contract.

## Current reference result

The 2026-08-24 backup audit found eight blueprints. Six Odyssey snapshots had no measured clips; Buster had only
seven scene-composite measurements from an older fixed-duration generation era. Tell-Tale Heart supplied 167
exact blueprint/sidecar matches suitable for scoring.

For those 167 clips, the production-prompt false-action fix changed the current estimate from 953.0 seconds
(+5.7%) to 895.0 seconds (-0.7%) against 901.2 seconds actual. Mean absolute error improved from 1.04 to 0.85
seconds per clip. This is a reference baseline, not a permanent tuning target; future runs should add independent
projects and retain holdouts.

## What remains live

`ClipDurationEstimator` still needs its speech head/tail, inter-speaker gap, model-bound resolution, silent-class
caps, dialogue splitting, action/concurrency analyzer, and single-key action/camera overhead ledger. Those protect
content and provider constraints even when a shorter estimate would score better on historical clips.

The older JIT timing experiment is not part of current estimation. `JitBenchmarkService` has no production caller
(only DI registration and tests), and `ActionCameraOverheadLedger.CalculateEffectiveSpeechWindowSec`,
`CalculateMaxSpeechWords`, `ExceedsSpeechCapacity`, plus their composite-ledger lookup are only called by tests.
They are cleanup candidates after confirming no external consumer depends on their public API. The active timing
telemetry path in `FilmJobService`, `GlobalTimingCalibrationService`, the admin timing endpoints, the classifier
used to label telemetry, `ActionConcurrencyAnalyzer`, and `ActionCameraOverheadLedger.GetOverheadSec` are not
obsolete.
