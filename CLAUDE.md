# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Film Studio** ("Nick and Me" / PageToMovie): an AI film pipeline that turns a book/PDF/Fountain screenplay into
cast-locked, shot-planned, AI-generated video scenes, with browser-side review/stitch/export. Product runtime is
**.NET only** (Blazor WASM UI + C# API/engine under `host/`) — **no Python runtime** is required for the product
path. The `scripts/` Python files are one-off maintenance/debug helpers, not part of the shipped product.

## Commands

All commands run from `host/` unless noted. **One process:** `PageToMovie.Api` hosts the UI. Do not start Web unless you are splitting ports on purpose.

Full product story: [docs/README.md](docs/README.md). Durable rules: [AGENTS.md](AGENTS.md).

```powershell
dotnet build PageToMovie.slnx

$env:XAI_API_KEY = "your-key"            # skip if using fakes
$env:PageToMovie__UseFakes = "false"
dotnet run --project PageToMovie.Api     # http://127.0.0.1:5088  (UI + API)

dotnet test PageToMovie.Tests            # free / default
# Paid: $env:PAGETOMOVIE_LIVE_API_TESTS = "1"; dotnet test PageToMovie.Tests --filter "Category=LiveApi"

$env:PageToMovie_USE_FAKES = "true"
dotnet run --project PageToMovie.Api
dotnet run --project PageToMovie.LoadSim -- --users 25 --duration 90 --scenario mixed
```

Playwright: start Api only; `WEB_URL` is the same origin (`http://127.0.0.1:5088`). See `host/playwright/README.md`.

## Architecture

### Solution layout (`host/`)

| Project | Role |
|---------|------|
| `PageToMovie.Core` | Shared DTOs/models and `Options` (no logic) |
| `PageToMovie.Adaptation` | Stage 1 façade (`AdaptationService`): book→Fountain conversion, density/runtime math, Stage 1 prompts, cast-package cross-check. Pure — no `ProjectStore`, paths, or HTTP (architecture-tested boundary; see `AGENTS.md` rule 10). Extracted out of `PageToMovie.Engine` starting 2026-08-03; the Adaptation→project vision-meta mapping now lives directly on `ProjectVisionMeta` in Engine (no separate `BookToFountainConverter` shim class as of 2026-08-13) |
| `PageToMovie.Fountain` | Fountain screenplay lexer/parser (`FountainParser`, `SpanFountainScanner`) — split out of Engine as its own project |
| `PageToMovie.Components` | Shared Razor component library (e.g. `LookPanelBase`, `CharacterLookPanel`/`LocationLookPanel`, ScreenplayEditor components) used by `PageToMovie.Web` |
| `PageToMovie.Engine` | Remaining product logic: job runner (`FilmJobService`), Stage 2 shot planning, ~20 AI classifiers, video prompt building, provider clients, stores. Orchestrates `PageToMovie.Adaptation` for Stage 1 rather than reimplementing it |
| `PageToMovie.Api` | Minimal-API REST + `/hubs/jobs` SignalR hub; hosts the WASM UI. **`Program.cs` is a large (~4k line) file holding essentially all route registrations** — search it by route path/verb rather than expecting a controller-per-resource layout |
| `PageToMovie.Web` | Blazor WebAssembly UI (`Components/Pages`) + client-side media tools (ffmpeg.wasm: stitch, silence-trim, frame sampling) |
| `PageToMovie.Fakes` | Fake `IChatClient`/`IImageClient`/`IVideoClient`/`IVisionClient` implementations + fixtures, swapped in via `PageToMovie:UseFakes` for spend-free dev/soak |
| `PageToMovie.LoadSim` | Concurrent virtual-user load client (Phase E soak testing) |
| `PageToMovie.Tests` | xUnit tests; `LiveApi/` subfolder is paid-provider tests gated by `[LiveApiFact]`/`[LiveApiTheory]` (skip cleanly unless both `PAGETOMOVIE_LIVE_API_TESTS` and the provider key are set) |
| `PageToMovie.UiTests` | C# Microsoft.Playwright browser UI tests (reuses Core/Engine to compute expected results instead of duplicating logic in JS/TS) |
| `host/tools/*` | Standalone eval CLIs (`ClassifierBenchmarks`, `BeatLabelEval`, `HeuristicAiEval`, `AmbientBlind`, `PlateOcrShortlist`, `CastBlind`) feeding `host/evals/` |

**Key architectural rule:** the API host never spawns native `ffmpeg`. All video compose/trim/frame-sampling
happens client-side in the browser via ffmpeg.wasm; the server only stores hashes/metadata for generated clips.

### Multi-provider AI clients

**Catalog SSoT:** all models and providers come from `host/PageToMovie.Core/config/models_catalog.json` (`providers[]` + `models[]`). Do not hardcode model ids, provider labels, or fallback model lists in C#/Razor — see root `AGENTS.md` (*Models & providers — catalog SSoT*). 


Each AI capability (chat, image, video, vision) has an interface in `PageToMovie.Engine/Abstractions/IModelClients.cs`
and a `MultiProvider*Client` (e.g. `MultiProviderChatClient`) that routes to the concrete provider client
(Grok/Anthropic/Gemini/etc.) based on the requested model id via `SupportedModelCatalog.ResolveOrDefault`.
Callers depend only on the interface (`IChatClient`, `IImageClient`, `IVideoClient`, `IVisionClient`) — they never
pick a provider directly. Unknown model ids fall back to Grok. When adding a new provider, add a concrete client
implementing the relevant interface(s) and register it in the corresponding `MultiProvider*Client` and
`SupportedModelCatalog`.

### The pipeline (book → movie)

Ingestion (`BookPrepareService`) → **Stage 1** (`AdaptationService`: index → max Fountain → **auto-enrich**; Look / Fit length optional) →
cast + plates → **Stage 2** shot plan → video → advisory auto-review → music → browser stitch.
Novels: index then write sequences (`file_id`). The 40k chunker is a fallback. See [max-master](docs/max-master-adaptation-plan.md).

### Jobs

Long-running work (Stage 1/2, scene gen, book prepare, character variants, clip auto-review, YouTube upload, etc.)
goes through `FilmJobService` → `JobStore`, tracked as `JobRecord`/`JobSnapshot` and pushed live to the UI over the
`/hubs/jobs` SignalR hub (`JobUpdated`, `JobLog` events). REST endpoints under `/api/jobs/*` start jobs and poll
status; `WorkerPools` bounds concurrency; `LockService` prevents conflicting concurrent jobs on the same project
(e.g. can't run stage2 while stage1 is in flight).

### Per-project data (`projects/<id>/`)

Each film is a project directory: `project.json` (id/title/file pointers), `pipeline_config.json`,
`pipeline_state.json` (includes `scene_dirty` flags for the learning-loop cascade), `source/` (imported text/cast
seeds), `blueprint.clips*.json` (Stage 2 shot plan), `assets/` (portraits, generated clips), `telemetry/`.
`projects/workspace.json` holds the single active project pointer (`ActiveProject`). Treat sample projects
(Buster2, TellTaleHeartV7, etc.) as eval/demo fixtures, not product requirements — see "General solutions only"
below.

### Prompts (`prompts/`)

Operator/product prompts are plain text files embedded at Engine build time (`book_to_fountain`,
`fountain_to_cast`, `cast_visual_literalize`, `clip_gen_rules`, `clip_auto_review` — edit in git and redeploy;
`PAGETOMOVIE_PROMPTS_DIR` overrides the directory locally). See `prompts/README.md` for the full file/role table
and the feedback-routing model (clip/stage2/stage1/verifier/engine layers) used by the learning loop.

## Product principles (from `AGENTS.md` — read that file for full detail)

These apply to *product code* under `host/` (Engine/Api/Web), not to one-off scripts or sample project data:

1. **Generalize, never hardcode.** No character names, book titles, page numbers, or story-specific
   strings/regex in Engine/Web/API code. Sample project fixes (editing files under `projects/<id>/`) are fine;
   the *code path* must work unmodified for the next book/cast. Ask before finishing a task: would this still
   work for a different book with different cast names?
2. **Prefer AI judgment over special-case lists.** Use prompts, cast metadata (`visual_lock`, `wardrobe_always`,
   `source_image_pages`), manifest `relevance`, and style locks — not growing if-ladders of anti-patterns.
3. **Deterministic guardrails stay hardcoded**: duration caps, model max clip length, prompt character budgets,
   cast-from-plan-only, spend-prevention gates.
4. **Don't build one-click full-movie automation yet.** The manual step-by-step path (approve screenplay → cast
   → shot plan → gen scene → review) is the near-term working mode; each full run costs real API money.
5. **Workflow UI copy is outcome-only, provider-neutral, and jargon-free.** Pages like Adaptation/Characters/
   Scenes/Review/Home must never mention provider names (Grok/Veo/Gemini/xAI), implementation mechanism
   (AI/vision/OCR/LLM/model/API), internal filenames (`blueprint.clips.json`, `scenes.json`), or pipeline jargon
   (plates→"pictures", seeds→"reference images", stage 1→"screenplay", stage 2/blueprint→"shot plan"). One fact
   in one place — never duplicate the same status/error in two UI surfaces. Provider names and model ids are only
   OK on the **Configuration** and **Cost** pages (Cost needs them to show per-model/per-vendor spend). Admin-only
   surfaces (job logs, "Details (admin)") may show technical detail. See `AGENTS.md` for the full banned-phrase
   table and the outcome-language mapping.

## Notable test/eval conventions

- `PageToMovie.Tests` runs free by default; anything tagged `Category=LiveApi` calls real provider APIs and costs
  money — never call paid APIs from a non-`LiveApi` unit test.
- `host/evals/` + `host/tools/{ClassifierBenchmarks,BeatLabelEval,HeuristicAiEval,AmbientBlind}` hold AI-vs-baseline
  classifier benchmark history for product classifiers — separate from story-project test fixtures.
- Prefer Release build + fakes + a single Api/LoadSim process pair for perf soaks (see `docs/loadsim-soak.md`).

## Response style

Keep responses short: lead with the result, skip background unless asked.
