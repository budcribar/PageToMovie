# Agent instructions — NickAndMe / Film Studio

Durable project rules for coding agents (including after session restart).  
Follow these unless the user explicitly overrides them for a task.

---

## Product north star (always decide with this in mind)

**Main goal:** Film Studio produces **excellent** results on **any** input story with **minimal user input**.

Ideal experience: the user **selects a story** (and connection settings if needed) and gets an **excellent film** — without hand-editing fountain, tuning duration regexes, fixing cast swaps, or nursing prompt retries.

Implications for every design/code choice:

1. **Generalize** — mechanisms must work for the next book/cast without product code edits.
2. **Automate judgment** — prefer AI prompts / metadata / style locks for semantic decisions; avoid never-ending verb/name/outfit lists.
3. **Deterministic for budgets and safety** — duration caps, model max clip length, 4096 prompt budget, cast-from-plan-only, gates that prevent wasted spend.
4. **Minimize operator steps (long-term)** — ideal end state is select story → excellent film. **Do not rush full auto-runs** until the manual path is proven: each full pipeline burns real API $; agents and humans should finish **import → strong scene 1** (and a few more titles by hand) before building “one-click produce film.”
5. **Near-term working mode** — deliberate, manual steps (approve screenplay, cast, rebuild shot plan, gen scene, review, fix product code). Prefer small general pipeline fixes over batch automation or soak scripts that re-gen whole movies.
6. **Before finishing a task**, ask: *Would a first-time user still get a strong film from a different story without us patching this?* If not, fix the pipeline, not the one project.

### Build the full screenplay — cut later (max master)

**Theory:** it is much easier to **cut** scenes than to **invent** them. We do not know the user's cut (120‑minute feature, miniseries, “just the wanderings”). So we run the expensive adapt **once**, keep a complete master, and every shorter film is a **view**.

Implications:

1. **Max master is the product.** Stage‑1 writes `screenplay.max.fountain` (and its index) covering the **whole book**. Do not collapse to a runtime target during first write. `reduced` / `custom` / Fit Length are **trim** only.
2. **Err long, not short.** Too many boards is a trim problem. Missing Nekyia / cattle / a chapter is a regenerate problem. Soft scene-band notes are warnings, not generation caps.
3. **Logical scenes, not 40k text slices.** The master is planned as an **index** (act → sequence → scene cards: heading, loc, cast, beat, book anchors), then **written from those cards** with the book attached by `file_id`. Arbitrary character-count chunks are a transport fallback, not the quality path.
4. **Reuse, don't re-adapt.** Users download / fork the max Fountain (and index). Changing length or dropping episodes must not re-read Homer. Trim is cheap and reversible (snapshot first).
5. **One-shot when it fits; index+batched writes for novels.** Short books: one file_id pass. Novels: index (small output) → write sequences in batches (not 175 one-scene calls, not 8 fat text chunks). File_id single-pass that times out may fall back to index+write, not to blind 40k slices.
6. **Hierarchy makes trim cheap.** Drop a sequence (Telemachus in Sparta) rather than random lines. Estimate / miniseries / 120‑minute cut are filters on the same index.

Canonical plan: [`host/docs/max-master-adaptation-plan.md`](host/docs/max-master-adaptation-plan.md). Aligns with Mary4 D0 (`screenplay.max` as shareable artifact).

---

## General solutions only (any book / any cast)

**Film Studio is a product for arbitrary stories and casts — not a single-title app.**

When debugging or implementing against a sample project (e.g. Buster / Buster2 / NickAndMe / TellTaleHeartV4):

1. **Ship general mechanisms**, not title-specific branches.
   - Prefer prompts, cast metadata (`source_image_pages`, `description`, `visual_lock`, `wardrobe_always`),
     manifest `relevance`, and book reference images.
   - **Do not** hardcode character names (`Buster`, `Momma`, …), book titles, page numbers,
     outfit beats (pajamas), or epithets (`noodle head`) in Engine / Web / API product code.
2. **Sample data fixes are data, not product code.**
   - Editing `projects/Buster2/...` (or any one project) to re-seed plates or clean descriptions is fine
     for the user’s current project.
   - The **code path** that attaches plates, builds cast, generates portraits, etc. must work for the next book without edits.
3. **No growing special-case lists.**
   - Avoid regex / if-ladders of story-specific anti-patterns.
   - Prefer AI prompt scrubbing, style locks, and image refs over one-off string rules.
4. **Comments and examples** in code should say “hero animal”, “supporting cast”, “text-only page” —
   not a specific character name — unless documenting a unit-test fixture.
5. **Models catalog is the single source of truth (no provider/model assumptions in code).**
   - **Canonical file:** `host/PageToMovie.Core/config/models_catalog.json` (runtime may also serve it via `/api/models` / `/api/models/catalog-json`).
   - **Must live in the JSON (not C#):** models (`id`, `displayName`, capability, endpoints, pricing, enablement), **providers** (`providers[]` with `id`, `label`, `aliases`), per-model `provider` / `providerId` / `providerLabel`, capability defaults (`capabilities[].defaultModelId`), task rankings, env key names.
   - **Code must not invent providers or models.** No hard-coded lists of model ids (e.g. `grok-imagine-video`, `eleven_voice_clone`), no `InferProviderIdFromModelId` / `startsWith("grok")` heuristics, no hard-coded provider label maps (`"grok" => "xAI"`), no Settings “fallback” model cards when the catalog fails to load. If it is not in the catalog, it is not real — leave UI empty / surface an error.
   - **Provider vs model:** *Provider* = who holds the key (e.g. **Suno API (sunoapi.org)**, **AI Music API (aimusicapi.ai)**, **xAI**). *Model* = product (e.g. **Suno v5.5**, **Suno**, **Grok Imagine Video**). Never treat “Suno” as a provider name in code or UI copy for key slots.
   - **Resolve only through catalog APIs:** `SupportedModelCatalog` / `GET /api/models` — `Find`, `ForCapability`, `DefaultModelIdForCapability`, `FirstEnabledVoiceCloneModelId`, `ProviderLabelFor`, `NormalizeProviderId` (alias table from JSON). UI defaults = `capabilities[].defaultModelId` or first enabled catalog model for that capability/provider.
   - **Adding or changing a model/provider:** edit **`models_catalog.json` only** (plus wire a client if the API shape is new). **Do not** add model/provider rows or default id strings in `Configuration.razor`, `PageToMovieOptions`, `ProjectModels`, or service classes to “make the UI work.”
   - **Stale project config:** if a saved model id is not in the catalog, drop/reset to catalog default or `none` — do not keep serving dead ids.
6. **Discussion-first workflow for questions.**
   - When the user asks questions or proposes architectural concepts, **always discuss and answer the questions first** before making code edits or triggering test runs.
   - Avoid running lengthy test suites or jumping straight to code modifications during exploratory discussions.
7. **Before finishing a task**, ask: *Would this still work for a different book with different cast names?*
   If not, generalize.
8. **Single source of truth for constants, code logic, and components.**
   - All regular expressions, magic strings, file path patterns, and configuration keys must be defined in **exactly one location** (e.g. `ClipFileNaming`, `SupportedModelCatalog`, constants classes).
   - **Do not** duplicate code logic, helper methods, UI components, regexes, model defaults, or path format strings across multiple files.
   - Prefer shared utilities, general methods, reusable Blazor/C# components, and centralized templates so that any behavior is defined and maintained in only one place.
   - **Search before you write.** Before adding a helper, gate, predicate, regex, prompt, or component, grep for an existing one that already does it (or nearly) and **reuse or extend it** — never hand-roll a parallel copy. If the same rule must run in several places, extract one canonical method/predicate and call it from each site; do not re-implement the decision per call site. (A rule cloned across N sites is a latent bug: a fix or exemption added to one copy silently misses the others — e.g. the "on-screen character needs a locked reference image" decision lived in three gates, and a group-exemption added to one left the others still blocking generation.)
9. **Strict Operator Control for Paid AI Endpoint Tests.**
   - Coding agents must **never** run test suites, scripts, or benchmarks that hit live paid AI model endpoints (Grok, Gemini, Veo, Claude) under automated agent control.
   - All tests that make live network calls to AI model APIs must be placed in `PageToMovie.Tests.LiveApi` and decorated with `[LiveApiFact]` / `[LiveApiTheory]` so they are excluded from default `dotnet test` runs.
   - Live API tests run **only** under explicit human operator command with `PAGETOMOVIE_LIVE_API_TESTS=1`.
10. **Stage‑1 adaptation logic lives only in `PageToMovie.Adaptation`.**
   - Book → Fountain conversion, density / natural runtime math, Stage‑1 prompts, and pure cast package cross-checks belong in **`host/PageToMovie.Adaptation`**.
   - **Do not** reimplement Stage‑1 heuristics or prompts in Engine/Web/Api. Engine may only **orchestrate** (load book, inject `IChatClient` / `IBookFileSession`, save fountain / vision_meta, jobs).
   - Call sites should prefer **`AdaptationService`** (façade). Thin Engine wrappers that forward to Adaptation are temporary compatibility only.
   - Module **must not** reference `ProjectStore`, project paths, SQLite, YouTube, Stage2, or media folders. Architecture tests enforce the boundary.
11. **Cache provider data files — never resend a large artifact you already uploaded.**
   - Books, screenplays, locked plates, and clip videos that go to a Files / Responses / edit API must ride a **stable `file_id`** (content SHA-256 + expiry), not be pasted or base64'd on every call.
   - See **Provider file cache** below. A new chat/image/video path that inlines `book_full.txt`, `screenplay*.fountain`, or image bytes is a bug unless Files is unavailable (then fall back and log).

Buster (and other fixtures) are **eval / demo projects**, not product requirements.

---

## Models & providers — catalog SSoT (mandatory)

**File:** [`host/PageToMovie.Core/config/models_catalog.json`](host/PageToMovie.Core/config/models_catalog.json)

| JSON | Role |
|------|------|
| `providers[]` | Key-holders: `id` (e.g. `grok`, `suno`, `aimusicapi`), `label` (UI), `aliases` |
| `models[]` | Selectable models: `id`, `displayName`, `capability`, `provider`, `providerId`, `providerLabel`, endpoints, costs, `requiredEnvKeys`, flags |
| `capabilities[]` | Studio jobs + `defaultModelId` |
| `taskRankings` | Optional ranking lists for planning tasks |

**Rules for agents (non-negotiable):**

1. **Do not hardcode model ids or provider names/labels in product code** for pickers, coverage, defaults, or “if catalog empty” UI. Read the catalog (or `/api/models`).
2. **Do not infer provider from model id** (`startsWith("fal")`, etc.). Use `providerId` / `provider` from the model row + `providers[].aliases`.
3. **Do not invent synthetic/fallback models** so the Settings page looks populated. Empty catalog → error or empty lists, not fake Grok/Fal rows.
4. **Optional “None”** for music/voice is UI state (feature off), not a catalog provider.
5. **New model?** Add/enable a row in JSON (and a client only if the HTTP API is new). **New reseller/provider?** Add `providers[]` + model rows with that `providerId` — do not special-case labels in Razor/C#.
6. Engine defaults in options/DTOs that still contain literal model strings are **tech debt** — prefer `SupportedModelCatalog.DefaultModelIdForCapability(...)` / project config; do not add more literals.
7. **Model-agnostic, capability-driven — never branch on which model is selected, never invent defaults for one.** No `if (model == "veo-3.1")` / `switch` on a model id, and no per-model code paths. Ask the catalog row what a model can do (`supportsVocals`, `supportsReferenceImages`, `minClipDurationSeconds` / `absMaxClipDurationSeconds`, endpoints, costs, flags) and drive behavior off those fields. When a project has **no** model selected, surface the "choose a model" error — do not fabricate bounds, ids, or a fallback model on its behalf (see `ClipDurationEstimator.ResolveBoundsForModel`, which throws rather than guessing). **The bar: swapping the selected model at runtime — a project-config change or a catalog edit — must just work, with zero code changes.**

Loader: `SupportedModelCatalog.TryLoadFromJson` / `EnsureLoaded`. WASM hydrates via `GetModelsCatalogJsonAsync` before relying on static catalog APIs.

See also: `host/docs/supported-models.md`.

---

## Provider file cache (cheaper, faster API runs)

**Rule:** if an artifact is large and reused, **upload once, attach by id**. Do not paste the book, the full screenplay, or re-base64 a plate/clip on every call.

Our `book_id` is **internal** (SHA of the text). Providers do not know it. The thing they reuse is their **`file_id`** (xAI Files, clip `source_file_id`, etc.). Persist that handle next to the content hash and expiry; skip upload when the hash still matches and the handle is unexpired (~1h safety margin).

### How to do it

| Artifact | Canonical handle | Implementation |
|----------|------------------|----------------|
| Book text | xAI `file_id` on `book_id` | `BookTextRegistryService` + `XaiBookFileSession` / `IBookFileSession` |
| Screenplay (full-length / draft) | project `file_id` keyed by SHA | `ProjectXaiArtifactFiles` (`source/xai_artifact_files.json`) |
| Clip video (for edit / extend) | `source_file_id` on the take | `GrokVideoClient` storage + `ClipSidecarService` |
| Exact-repeat chat (classifiers, same prompt) | on-disk completion cache | `CachingChatClient` (hash of model + prompts; skip if temperature > 0) |
| Book → Fountain conversion | derived artifact on `book_id` | `BookTextRegistryService` adaptation_conversion cache |

**Call shape:** short instruction + `input_file` ids (Responses / Files). `chat/completions` has **no** file slot — if the model is xAI/Grok, prefer Responses. If Files is down, fakes mode, or a non-file provider, fall back to inline and **log** it.

**Do not** invent a second cache beside these tables. Extend `ProjectXaiArtifactFiles` or the book registry.

### Audit — still inlining (fix when you touch the path)

| Call | Today | Cache it as |
|------|--------|-------------|
| Enrich | Files path (book + `screenplay.max`); fallback still pastes 40k book + full fountain | Done for Grok; keep fallback last-resort |
| Stage 1 book → Fountain (primary) | Book `file_id` + `previous_response_id` | Done for Grok when book does not fit inline |
| Stage 1 merge / loc / name / narration repairs | Fountain `file_id` (`screenplay.stitch`) via `IFountainFileSession`; fallback inlines | Done for Grok |
| Look / reskin | Files path (`screenplay.max` file_id); fallback inlines | Done for Grok |
| Fit length / trim | Files path (`screenplay.max` file_id); fallback inlines | Done for Grok |
| Cast extract | Files path (book + `screenplay.fountain` file_ids); fallback inlines | Done for Grok |
| Stage 2 + beat classifiers | Scene/beat snippets only — **do not** attach the full screenplay (would re-bill 200k tokens per classifier) | `CachingChatClient` for exact repeats |
| Scene music scoring | Scene setting snippet, not the full fountain | Leave inline |
| Character / location look gen | Locked plates as local bytes / data-URI | Image API has no `file_id` slot — leave bytes; never re-upload an unchanged plate when a handle exists |
| Video edit / extend | Clip `source_file_id` when present | Done — do not regress to base64 |

When adding a new chat/image/video call: **grep for an existing file handle first.** If the body would include `book_full.txt`, `screenplay*.fountain`, or a previously generated image/video, attach the stored id.

---

## UI copy principles (operator-facing Blazor / product UI)

Apply to **workflow pages** users operate day to day: Adaptation, Characters, Scenes, Review, Home, and similar.  
**Configuration** (and a dedicated connection/settings area) may name providers and models when the user is *choosing* them.  
**Cost** may also name providers and model ids — spend has to be attributable to what actually generated it.

### 0. No commentary or decision process on the UI

- **Do not** put agent/dev thinking on the page: why a control exists, what the backend does next, ranking rules, caps (“top 3 go to the API”), scrubbing notes, “after X, do Y” tutorials.
- **Do not** duplicate status (same error in a banner *and* a job card; same lock state on list *and* detail strip).
- Labels = short outcomes (**Save look**, **Generate → compare**, **Find characters**). Tooltips only when a label is ambiguous.
- Project selection lives on **Home** — do not re-add project pickers on workflow pages unless the user asked for multi-project on that screen.

### 0a. No redundant messages on the UI

- **One fact, one place.** Never show the same status twice (e.g. badge **ready** *and* alert “Shot plan ready”, or success “Cast ready…” *and* a second “Cast ready” banner).
- Prefer **actionable next step** or **counts** over repeating a word like “ready” when the primary control already implies state (button says **Rebuild shot plan** → no green “ready” badge needed).
- Success flash **or** next-step CTA — not both stacking the same outcome.
- Help lines that restate the step strip or button labels (“Pin cast… then build… then generate…”) are noise — drop them unless they add a non-obvious constraint.
- When state is already visible (list green/red, header counts, enabled/disabled nav), do not add another sentence that only restates it.

### 0b. Technical job details are Admin-only

Operators see **short outcome status** only (e.g. “Creating portrait…”, “Portrait generation failed. Try again.”).

**Admin only** (collapsible “Details (admin)” or admin badge views):

- Job log lines such as `Character design (C# / Grok image API)…`, seed paths, `Seed mode=explicit · refs=3/3`, model names, HTTP bodies, file names under `assets/…`.
- Full job cards with kind badges (`stage2`, `done`), multi-line logs, and **My jobs** lists on Scenes / Adaptation.
- Stacking the same error three times (list row + Current message + red alert) is forbidden — **one** operator-facing error surface.

Never show raw engine progress dumps to non-admin users on Home, Characters, Scenes, or Adaptation.

On **Scenes**: operators never see leftover Stage 2 / adaptation job cards. Show only compact **Generating…** / error for active clip gen or remux; admin keeps the full job panel.

### 1. Outcome only — not mechanism

- Describe **what the user gets** (imported source, screenplay, cast, portraits, clips, movie draft).
- Adaptation flow: **Import** (screenplay file / PDF / TXT) → **Screenplay** (edit + **approve**) → **Shot plan**.
- The editable screenplay draft is the source of truth; Stage 1 / cast / shots unlock after **Looks good — continue** (sign-off).
- **Do not** explain *how* it is done: no “AI”, “vision”, “OCR”, “LLM”, “model”, “chat”, “API”, or “the system uses …”.
- Users do not care whether a step is AI, deterministic, or ffmpeg under the hood.

### 2. No provider branding on workflow UI

- Do **not** hardcode **Grok**, **Veo**, **Gemini**, **xAI** on workflow pages.
- The user may have selected **VEO** (or another provider) in Configuration — UI must stay neutral.
- Provider names belong on **Configuration** (or Settings) when selecting video/portrait services, and on **Cost**
  when breaking down spend by vendor/model.

### 3. No project filenames or paths

- Do **not** show `scenes.json`, `blueprint*.json`, `book_full.txt`, `pipeline_config.json`, asset paths, etc. in operator copy.
- Say “this project”, “screenplay”, “shot plan”, “saved” instead.

### 4. No pipeline jargon

| Avoid | Prefer |
|-------|--------|
| plates / book plates | book pictures |
| seeds | reference images / pictures |
| scene bible / Stage 1 | screenplay (or Step 2 — Screenplay) |
| clip plan / blueprint / Stage 2 | shot plan (or Step 3 — Shot plan) |
| VOICE LOCK | voice style |
| Sort plates with Grok | **Find characters** |
| Re-sort with Grok | **Find characters again** |
| Generate with Grok | **Generate portraits** |
| ffmpeg / concat / composite path | Play / combine clips / export movie (browser tools) |

### 5. Keep it short

- One plain sentence of help is enough.
- Prefer button labels that are verbs + outcome (**Find characters**, **Generate portraits**, **Build shot plan**).
- Connection failures: one place (“Connect service” / Settings) — not “XAI_API_KEY” on every page.

### Phrases banned on workflow pages

`Grok`, `Veo`, `Gemini`, `xAI` (except Configuration pickers and Cost spend breakdowns),  
`AI`, `vision`, `OCR`, `LLM`, `model`, `chat`, `API key`,  
`plates`, `seeds`, `bible`, `blueprint`, `pipeline`, `VOICE LOCK`,  
`*.json`, `book_full.txt`, `ffmpeg`, `PdfPig`, `C#`, service class names.

### Configuration and Cost exception

On **Configuration** / admin runtime settings it is OK to:

- Label providers (Grok / Veo / …) for selection.
- Show model IDs as field *values*.
- Still avoid dumping raw filenames in primary labels when a friendly name works (“Shot plan file” under Advanced is OK if needed).

On **Cost** it is OK to:

- Label providers and model ids next to the spend they generated (e.g. “grok-imagine-video: $70.44”).
- Still avoid raw internal field/table names (e.g. say “tracked spend”, not `cost_ledger`) and other non-provider jargon.

### About / developer docs

Slightly more technical language is OK on **About** or a collapsible “For developers” section — not on Adaptation / Characters / Scenes / Review.

---

## Razor UI file size & split rules

Goals for Blazor `.razor` markup size so agents and humans keep pages navigable. **Code-behind** (`.razor.cs`) is counted separately — a lean markup file with a larger partial class is fine.

### Size targets

| Kind of file | Soft target | Hard ceiling |
|--------------|------------:|-------------:|
| Shared RCL (`PageToMovie.Components/Shared/*`) | 80–150 | **200** |
| Page-local extract (`Pages/Foo.Bar.razor`) | 100–250 | **350** |
| Page shell (`Pages/Foo.razor`) | 200–350 | **500** |
| Layout (`MainLayout`, `NavMenu`) | 300–400 | **500** |

- **New work:** prefer the soft target.
- **Existing pages:** hard ceiling **500** for shells; only split when the extract has a clear name and boundary.
- Do **not** force everything under 300 — that produces parameter-heavy or opaque `CascadingParameter` children that are harder to read than a cohesive ~400-line block.

### When to split

Split when you can name the piece as a **logical unit**:

- A modal or dialog (`Admin.TestEmailModal`, `Scenes.GenerateConfirm`)
- A tab body (`Review.PlayTab`)
- A section card / panel (`Home.CheckpointsPanel`, `Admin.JobsSection`)
- A table vs inspector (`Scenes.ClipTable`, `Scenes.ClipInspector`)
- A wizard step (`SimpleVoice.PickPhase`)

Do **not** split only to hit a number. If the extract needs 15+ parameters or most of the parent’s private state, fix the state shape first (small view-model or cascade), then extract.

### Naming & wiring

| Convention | Rule |
|------------|------|
| File name | Dotted page-local: `Review.PlayTab.razor`, `Scenes.ClipTable.razor` |
| Markup tag | Dots → underscores: `<Review_PlayTab />`, `<Scenes_ClipTable />` |
| Dense parent state | `<CascadingValue Value="this" IsFixed="true">` + `[CascadingParameter] public ParentType Host` |
| Parent members used by children | `internal` (same assembly), not `private` |
| Static helpers | Call as `ParentType.Method`, not `Host.Method` |
| Services in children | Prefer `@inject` in the child (`Caps`, `Session`, `L`, `MediaFolder`, `Engine`) over `Host.Session` when injects are private on the parent |

Shared, reusable controls belong in `PageToMovie.Components/Shared/` with a stable public parameter API. Page-only chrome stays under `Pages/` with the dotted name.

### Preserve behavior

- Keep every existing `data-testid` string **exactly**.
- Do not change operator-visible copy unless the task asks for it (see **UI copy principles** above).
- `dotnet build host/PageToMovie.Web/PageToMovie.Web.csproj -c Release` must stay **0 errors** after a split.

### Prefer this order of extraction

1. Modals / confirms  
2. Tab bodies or wizard phases  
3. Large cards / collapsible sections  
4. Tables vs detail inspectors  
5. Only then: further subdivision of a child that is still over its ceiling

### Anti-patterns

- Copy-paste of the same large parameter block to three call sites — use one `RenderFragment` factory or one child component.
- Extracting markup while leaving the only consumers unable to compile because members stayed `private`.
- New shared RCL controls that embed page-specific workflow copy; keep those page-local.
- Parallel “cleanup” renames unrelated to the split in the same change set.

### Related assignment notes

Current over-ceiling backlog and agent pairing live in the working tree under split-assignment notes when present (e.g. four-agent over-500 plan). Prefer those for *what* to extract next; this section is the durable *how* and *how large*.

---

## Related docs

| Doc | Purpose |
|-----|---------|
| `host/docs/north-star-checklists.md` | Checklist A (AI-call feedback loop, active) + Checklist B (pre-UI-consolidation, on hold) — cross-session status |
| `host/evals/README.md` | App eval root (not story projects) |
| `host/evals/screenplay_benchmark/README.md` | 8-dimension screenplay adaptation & peer-evaluation benchmark guide |
| `host/evals/classifier_benchmarks/README.md` | Classifier AI vs baseline suite; history, model/prompt matrix, charts |
| `host/evals/beat_label_eval/README.md` | Silent-beat action_class ground truth + model comparison |
| `host/evals/heuristic_ai_eval/` | Legacy holdout / ambient blind dumps |
| `host/docs/perf-findings-2026-07.md` | Multi-user perf soak findings; optimization paused; files→DB notes |
| `host/docs/async-io-pass-plan.md` | Async I/O multipass status |
| `host/docs/loadsim-soak.md` | How to run LoadSim |

---

## Regression testing workflow — batch then bisect

For multi-feature work sessions (several independent features/fixes landing before a full regression pass), don't
run the full UI+non-UI suite after every single change — batch first, bisect only on failure:

1. **Batch before testing.** Land at least ~8 independent features/fixes, then run the full regression suite
   (non-UI unit tests + UI/Playwright tests) once for the whole batch.
2. **On failure, don't re-run everything — bisect the batch, not the suite.** Split the batch in half (e.g. 4
   features), re-run **only the tests that failed** (not the full suite) against that half. A pass isolates the
   fault to the other half; keep halving (4→2→1) until the single feature/commit responsible is identified. This
   turns an O(features × suite) re-run cost into O(log(features) × failed-tests) — far cheaper than re-running the
   whole suite at every split.
3. **Look at likely files/history first.** Before bisecting blindly, check which of the batched changes touched
   the files/paths implicated by the failing test's stack trace or assertion — often narrows it immediately
   without needing a full bisect.
4. **Read the error message before bisecting.** A clear exception/assertion often points straight at the change
   without needing to split anything — bisect is the fallback when the error alone doesn't localize it.
5. **Rerun a failing test at least once before trusting it as a real regression.** Flaky UI tests (timing/race
   conditions) can look like a bisect signal but aren't — confirm reproducibility before spending bisect cycles on
   it.
6. **Bisection isolates a fault; it doesn't replace the final full run.** Once the culprit is fixed, still run the
   complete suite once more before calling the batch done — bisection narrows *where* to look, it doesn't
   guarantee nothing else in the batch also regressed something the bisect path didn't re-check.

---

## Ephemeral migration & cleanup lifecycle rule

When performing data/folder/schema migrations via temporary code blocks (such as startup migration hooks in `Program.cs` or one-time DB patches):

1. **Temporary status**: Treat one-time migration code as temporary runtime scaffolding.
2. **Verify & Remove**: Once the deployment completes and the user confirms/verifies the data state in production, **immediately remove the one-time cleanup code** in the next commit.
3. **No Code Cruft**: Never leave one-time data fix-up scripts or legacy migration hooks running indefinitely in production codebase paths.

---

## Server Diagnostics & Log Retrieval (Railway / Production Debugging)

When debugging runtime behavior on the live Railway server across coding agent sessions:

1. **Log Export Zip Endpoint**:
   - URL: `/api/admin/logs/export` (also accessible via operator key query parameter `?me=<OPERATOR_SECRET>` or header `X-Admin-Key`).
   - Downloads a `.zip` archive containing:
     - `system_info.json` (machine name, OS, active project ID, timestamp)
     - `job_logs.json` (active job snapshots & multi-line log histories)
     - `edit_logs/` (`edit_log.json` for all projects)
     - `artifact_index/` (`artifact_index.json` for all projects)
     - `prompts/` (all generated `*.prompt.txt`, `*.meta.json`, and `*.clip.json` files)

2. **Live JSON Log State**:
   - URL: `/api/admin/logs`
   - Returns active job state, system info, and project list.

3. **Admin Dashboard Button**:
   - The Admin page (`/admin`) includes a **📥 Download Server Logs** button in the header.

---

## Platform Architecture & Pipeline Integrity Rules

### 1. Persistent OAuth & Credential Sanitization
- **SQLite Token Store**: Never use ephemeral `FileDataStore` for Google/YouTube OAuth tokens. Use `SqliteDataStore` pointing to persistent volume storage (`/data/pagetomovie.db`) so OAuth refresh tokens survive app restarts, container updates, and Railway deploys.
- **Strict Credential Trimming**: Always sanitize OAuth environment variables (`ClientId`, `ClientSecret`, `RedirectUri`) using `.Trim(' ', '"', '\'', '\r', '\n', '\t')` before passing them to Google API flows to prevent `invalid_client` HTTP 400 errors caused by surrounding quotes or trailing whitespace.
- **YouTube 15-Minute Capping**: Unverified YouTube channels cap video uploads at 15 minutes (YouTube API accepts the upload initially, but background processing deletes the video with *"Video removed because it was too long"*). Channel operators can unlock uploads up to 12 hours via one-time phone verification in *YouTube Studio → Settings → Channel → Feature eligibility → Intermediate features*.

### 2. Creator Auto-Public Publishing Workflow
- **Minimal Steps for Creators**: When an authenticated project owner or admin publishes a film cut, automatically mark `status = "public"` and launch background YouTube publishing immediately. Do **not** force project creators to manually approve their own self-published films in the Admin moderation queue.

### 3. Video Concat & Clip Disjointness
- **Disjoint Media Gathering**: When collecting media for full-movie stitching, strictly prefer individual clip files (`scene_01_clip_01.mp4`, `scene_01_clip_02.mp4`) per scene over scene composites (`scene_01.mp4`). Never combine scene composites with individual clips for the same scene (prevents duplicated playback).
- **Export Cache Invalidation**: Force a fresh, scratch assembly of all current scene clips on export/publish. Never re-use old in-memory browser Blob URLs (`_clientWipUrl`) when new clips or scene updates have occurred.

### 4. AI Payload HTML Tag Sanitization
- **Sanitize LLM HTML Wrappers**: LLMs (Grok/Gemini) generating structured JSON fields (such as review notes or executive summaries) occasionally output raw `<p>...</p>` or `<br/>` HTML tags. Always run AI string payloads through an HTML pre-pass (`MarkdownHelper.Render` / `MarkdownHelper.StripHtml`) to clean raw tags into Markdown before HTML rendering, preventing literal `&lt;p&gt;` text from displaying in the UI.

### 5. Navigation & Public UI Boundaries
- **Logged-Out Navigation Isolation**: Public gallery visitors (`/demo`) must never see operator configuration or administration controls. Keep `/configuration` and admin routes strictly wrapped inside `@if (Session.IsLoggedIn)` in navigation components (`NavMenu.razor`).

### 6. Unrestricted Creator Publishing & YouTube Quota Reliance
- **Rely on YouTube Quota Management**: Do not block authenticated project owners or admins with artificial local daily publish limits (e.g. 2 demos per 24 hours). YouTube API quotas and Google platform rate limiters natively prevent video spamming. Server-side per-IP rate limiting is deferred to post-launch backlog.

### 7. Client-Side Media Ownership & Minimal Server Transfer (CRITICAL PRINCIPLE)
- **Client Storage Priority**: All generated `.mp4` video files and media assets live on the **CLIENT SIDE** (browser Cache API / IndexedDB / local storage directory). They are **NOT** kept on the server.
- **Never Assume Server Disk Has MP4 Files**: Agents must **NEVER** diagnose 404 errors as "file missing on server" or tell the user to regenerate clips on the server when media lives on the client side. Always resolve client local storage / browser Cache API handles (`GetLocalBlobUrlAsync`) first.
- **UI & API Control Rule**: Never block UI features (such as background music scoring, scene playback, or export) based on server-side `ClipsOnDisk == 0` or missing server MP4 files. The client owns its media assets.
- **On-Demand Server Transfer Only**: `.mp4` files are uploaded to the server strictly on-demand:
  1. When publishing/uploading a full film to YouTube.
  2. When generating a scene continuation (`extend_previous`) that requires the previous scene's video file on the server for AI continuation.

---

*Last updated: 2026-08-13 — P3 write from index; P2 beat sheet; P1 fountain file_id; P0 honest runtime.*


## Stage‑1 prompt tokens (book → Fountain)

- Template: [`prompts/book_to_fountain.txt`](prompts/book_to_fountain.txt) (currently **v4**: Fountain + `VISION_META` + `ADAPTATION_REPORT`).
- Loaded via `AdaptationPromptPack` (embedded resource; override with `PAGETOMOVIE_PROMPTS_DIR`).
- **Every** `{{TOKEN}}` must be substituted by `ApplyPromptTokens` / `AdaptationPromptTokens` before any model call. Leftovers throw.
- UI-bound values (e.g. visual medium, runtime target) flow: UI → project preference / request → tokens → prompt.
- After editing the prompt file, **rebuild** `PageToMovie.Adaptation` or point `PAGETOMOVIE_PROMPTS_DIR` at repo `prompts/`.

