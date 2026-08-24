# App evals

Classifier / label evaluation data and history for **Film Studio product code** — not story projects.

| Folder | Purpose |
|--------|---------|
| `screenplay_benchmark/` | 8-dimension screenplay adaptation & peer-evaluation benchmark suite + history charts |
| `classifier_benchmarks/` | AI vs baseline suite + history charts (ambient, cast, silent beat, species, …) |
| `beat_label_eval/` | Silent-beat `action_class` ground truth source (feeds classifier_benchmarks gold) |
| `heuristic_ai_eval/` | Earlier holdout drafts / ambient blind dumps (legacy companion to ClassifierBenchmarks) |

Tools: `host/tools/ScreenplayBenchmark`, `host/tools/ClassifierBenchmarks`, `host/tools/BeatLabelEval`, `host/tools/HeuristicAiEval`, `host/tools/AmbientBlind`.

Generated-project estimate-vs-actual timing calibration uses
[`host/tools/ClipDurationCalibration`](../tools/ClipDurationCalibration) and the
[clip-duration calibration guide](../../docs/clip-duration-calibration.md). Project media and duration sidecars
remain outside `host/evals`; the tool reads them in place and makes no paid API calls.
