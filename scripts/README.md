# Scripts

Optional one-off and maintenance tools. Prefer **Film Studio** under `host/` (Blazor UI + API jobs) for product workflows.

Run from the **repo root** when a script expects workspace-relative paths.

## Product path (preferred)

| Path | Role |
|------|------|
| `host/PageToMovie.Api` | REST + jobs (Stage 1/2, gen, remux, cast, learning) |
| `host/PageToMovie.Web` | Operator UI |
| `host/PageToMovie.Engine` | Native pipeline |

See repo-root `README.md` and `host/README.md`.

## Tools in this folder

Historical / ad-hoc helpers may still live here (including older Python utilities). They are **not** required to run Film Studio. Prefer API jobs and the Adaptation pages for book prepare, Stage 1/2, and generation.

## Two-stage adaptation (concept)

Stage 1 (screenplay) is implemented natively in `host/PageToMovie.Adaptation` (`AdaptationService` façade);
Stage 2 (shot plan) in `host/PageToMovie.Engine`'s `Stage2PlannerService`. The old
`scripts/two_stage_adaptation/` Python helpers were removed; the product path is native Adaptation/Engine.
Prompt sources and schemas: `prompts/`.
