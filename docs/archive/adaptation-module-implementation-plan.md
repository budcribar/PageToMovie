# Implementation plan: `PageToMovie.Adaptation` module

**Status:** Phases 0–4 largely complete on master (2026-08-03); Phase 6 hardening ongoing  
**Goal:** Self-contained Stage‑1 adaptation library with **well-defined interfaces**, **no storage**, **injected dependencies**.  
**Related:** [runtime-and-mary-prompt-implementation-plan.md](./runtime-and-mary-prompt-implementation-plan.md), [film-provenance-critic-learning-architecture.md](./film-provenance-critic-learning-architecture.md)

---

## 1. Design principles (non-negotiable)

| Principle | Meaning |
|-----------|---------|
| **No storage** | No `ProjectStore`, no paths to `projects/`, no SQLite, no git inside Adaptation |
| **No product I/O** | No HTTP hosts, no YouTube, no media folders, no user DB |
| **Inject externals** | Chat, clock, optional telemetry via interfaces |
| **Pure inputs → pure outputs** | Book text + knobs in; fountain / vision_meta / estimates / reports out |
| **Orchestration stays in Engine** | Load book from disk, resolve saved target, save draft, jobs, UI |
| **Single Stage‑1 brain** | Production `ScreenplayService` and `ScreenplayBenchmark` call the **same** façade |
| **Versionable surface** | Entire Adaptation project (+ embedded prompts) = `engine_sha` / cache identity |

```text
                    ┌─────────────────────────────┐
   book text,       │  PageToMovie.Adaptation      │
   target minutes,  │  (DLL — pure Stage‑1)        │
   model id,        │                             │
   IChatClient  ──► │  Analyze → Convert → Check  │ ──► fountain, vision_meta,
                    │                             │     density, cast cross-check
                    └─────────────────────────────┘
                              ▲
                              │ ProjectReference
              Core (models/DTOs) + Abstractions chat
                              │
         Engine / Api / Benchmark  (I/O, store, jobs)
```

---

## 2. Target project layout

```text
host/
  PageToMovie.Core/                 # existing — shared models if needed
  PageToMovie.Adaptation/           # NEW class library net10.0
    PageToMovie.Adaptation.csproj
    Abstractions/
      IAdaptationChat.cs            # or reuse IChatClient from a thin shared place
      IAdaptationClock.cs           # optional; default SystemClock
      IAdaptationProgress.cs        # Action<string> wrapper
    Analysis/
      AdaptationDensity.cs
      BookTextAnalyzer.cs
      ClipDurationEstimator.cs      # if only Stage‑1 / density needs it
    Conversion/
      BookToFountainConverter.cs
      PromptCatalog.cs              # load embedded prompts
    Validation/
      CastPackageCrossCheck.cs      # if pure-text
      Fountain structural helpers as needed
    Runtime/
      NaturalRuntime.cs             # density → minutes (no ProjectStore)
    Contracts/
      AdaptationRequest.cs
      AdaptationResult.cs
      BookAnalysisResult.cs
      NaturalRuntimeEstimate.cs
    AdaptationService.cs            # public façade
    README.md                       # module boundary for agents
  PageToMovie.Engine/               # orchestration only for Stage‑1
  tools/ScreenplayBenchmark/        # references Adaptation (+ Engine if needed)
```

**Namespace:** `PageToMovie.Adaptation` (and sub-namespaces).  
**Forbidden project refs:** Engine, Api, Web, Fakes (except test project).  
**Allowed refs:** `PageToMovie.Core` (models only); packages only if converter truly needs them (prefer zero heavy packages).

---

## 3. Public contracts (façade)

### 3.1 Request / result (illustrative)

```csharp
public sealed class AdaptationRequest
{
    public required string BookText { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public int? TargetRuntimeMinutes { get; init; }  // null → use natural
    public string ModelId { get; init; } = "";       // planning model id from catalog (caller resolved)
    public double Temperature { get; init; } = 0.2;
    // No projectId, no paths, no userId
}

public sealed class AdaptationResult
{
    public required string Fountain { get; init; }
    public ProjectVisionMeta.Document? VisionMeta { get; init; }  // or Adaptation-owned DTO mapped at boundary
    public NaturalRuntimeEstimate Runtime { get; init; } = null!;
    public BookAnalysisResult Analysis { get; init; } = null!;
    public bool UsedHeuristicFallback { get; init; }
    public string PromptContentSha256 { get; init; } = "";
    public string? Notes { get; init; }
}

public sealed class NaturalRuntimeEstimate
{
    public int NaturalMinutes { get; init; }
    public int TargetMinutes { get; init; }   // natural if not overridden
    public string Mode { get; init; } = "natural"; // natural | reduced | custom
    public string Method { get; init; } = "";
    public int SourceWords { get; init; }
    // density δ, τ optional
}
```

### 3.2 Façade API

| Method | Responsibility |
|--------|----------------|
| `AnalyzeBook(string bookText)` | Kind, words, quality notes, natural minutes |
| `EstimateNaturalRuntime(string bookText)` | Density only |
| `ResolveTargetMinutes(bookText, int? override)` | Natural + optional override clamp |
| `BuildSystemPromptAsync(targetMinutes, ct)` | Embedded prompt + substitutions |
| `ConvertAsync(AdaptationRequest, IChatClient, progress, ct)` | Full Stage‑1 convert |
| `ConvertHeuristic(...)` | Offline / test path |
| `CrossCheckCast(fountain, castJson?)` | Optional pure validation |

### 3.3 Injected dependencies

| Dependency | Owner | Notes |
|------------|--------|--------|
| **Chat** | Caller supplies `IChatClient` (or `IAdaptationChat` adapter in Engine) | Adaptation never constructs HTTP clients |
| **Progress** | `IProgress<string>` / callback | Optional |
| **Time** | Optional `IAdaptationClock` | For determinism in tests |
| **Prompts** | Embedded resources in Adaptation csproj | Not read from arbitrary disk in production path; tests may override via interface later |

**Explicitly not injected:** `ProjectStore`, file system for projects, user keys (caller already configured chat).

---

## 4. What moves vs stays

### 4.1 Move into Adaptation (core)

| Item | Notes |
|------|--------|
| `AdaptationDensity.cs` | Pure |
| `BookTextAnalyzer.cs` | Pure |
| `ClipDurationEstimator.cs` | If density/analyzer depend on it |
| `BookToFountainConverter.cs` | Strip any residual path assumptions |
| Stage‑1 prompt embeds | `book_to_fountain.txt`, shared includes used by it |
| Pure fountain normalize/fix helpers used only by converter | From converter / small helpers |
| `CastPackageCrossCheck` (if pure) | Validation |

### 4.2 Split / re-home

| Item | Plan |
|------|------|
| `FilmRuntime.cs` | **Split:** pure `NaturalRuntime` / `ResolveTargetMinutes(text, override)` → Adaptation; disk/config read-write → Engine `ProjectFilmRuntime` using store |
| `ScreenplayService.CreateDraftFromBookAsync` | **Stay Engine:** load book, call `AdaptationService.ConvertAsync`, save draft, vision_meta, cache registry |
| `ProjectVisionMeta` | Prefer **Core** or Adaptation-owned document type; Engine maps to project files |
| `FountainParser` / `FountainStage1Importer` | Move only if Stage‑1-only and pure; else stay Engine until needed |

### 4.3 Stay in Engine

| Item | Reason |
|------|--------|
| `ProjectStore`, jobs, `FilmJobService` | I/O + orchestration |
| `BookPrepareService` (PDF/OCR) | I/O + vision providers; **output** is book text for Adaptation |
| `BookTextRegistryService` | Shared cache DB |
| Stage2, clips, cost, YouTube, cast portrait gen | Downstream |
| `FilmLengthCard` / UI | Calls Engine API, which uses Adaptation under the hood |

---

## 5. Action items (ordered)

### Phase 0 — Contract freeze (½–1 day)

| ID | Action | Done when |
|----|--------|-----------|
| **A0.1** | Write this plan + `PageToMovie.Adaptation/README.md` boundary (do / don’t) | Agents know the wall |
| **A0.2** | Define `AdaptationRequest` / `AdaptationResult` / `NaturalRuntimeEstimate` in a sketch (this doc or empty project) | Review sign-off |
| **A0.3** | Decide chat interface: reuse `PageToMovie.Engine.Abstractions.IChatClient` → **move interface to Core or Adaptation.Abstractions** so Adaptation does not reference Engine | No Engine ref from Adaptation |
| **A0.4** | List initial file move set (Phase 1–2) in PR template | Checklist exists |

**Exit:** Interfaces agreed; no large code move yet.

---

### Phase 1 — Project skeleton + pure analysis (1–2 days)

| ID | Action | Done when |
|----|--------|-----------|
| **A1.1** | Add `host/PageToMovie.Adaptation/PageToMovie.Adaptation.csproj` (`net10.0`), ref Core only | Builds |
| **A1.2** | Engine → ProjectReference Adaptation | Solution builds |
| **A1.3** | `git mv` `AdaptationDensity`, `BookTextAnalyzer`, `ClipDurationEstimator` (as needed) into Adaptation | Namespaces updated |
| **A1.4** | Fix all usings; temporary `using` aliases if needed | `dotnet build` green |
| **A1.5** | Move related unit tests; keep behavior identical | Tests pass |
| **A1.6** | Public `NaturalRuntime.Estimate(bookText)` wrapping density + analyzer | Benchmark/UI can call one API |

**Exit:** Density/analyzer live in Adaptation DLL; Engine/benchmark still work.

---

### Phase 2 — Converter + prompts (2–4 days)

| ID | Action | Done when |
|----|--------|-----------|
| **A2.1** | Move `IChatClient` (and minimal chat DTOs) out of Engine into Core or Adaptation so converter compiles without Engine | Clean refs |
| **A2.2** | `git mv` `BookToFountainConverter` into Adaptation | Builds |
| **A2.3** | Move embedded resource for `book_to_fountain` (+ shared) to Adaptation csproj; remove duplicate embed from Engine if unused | Railway-safe embed |
| **A2.4** | Remove/replace any ModelExecution coupling with narrow interfaces or keep only types needed inside Adaptation | No SQLite/YouTube packages on Adaptation |
| **A2.5** | Implement `AdaptationService.ConvertAsync` as thin wrapper over converter | Single entry point |
| **A2.6** | `ScreenplayService` calls Adaptation façade; **no** reimplementation | Production path identical outputs (spot-check Mary/Buster fixtures) |
| **A2.7** | ScreenplayBenchmark calls Adaptation for generation path | Same scores within noise |

**Exit:** Stage‑1 generation is 100% via Adaptation module.

---

### Phase 3 — Runtime boundary clean-up (1–2 days)

| ID | Action | Done when |
|----|--------|-----------|
| **A3.1** | Pure target resolve in Adaptation: `ResolveTarget(bookText, overrideMinutes?)` | ✅ `NaturalRuntime` + `AdaptationService.ResolveTargetMinutes` |
| **A3.2** | Engine `FilmRuntime` / project config: load/save target only; call Adaptation for natural | ✅ storage/orchestration only |
| **A3.3** | Book prepare writes natural from `AdaptationService.AnalyzeBook` | ✅ + `FilmRuntime.ApplyNaturalToMetaDictionary` |
| **A3.4** | Document: “retarget is Engine; natural math is Adaptation” | ✅ Adaptation README |

**Exit:** Storage for target minutes stays Engine; math stays Adaptation.

---

### Phase 4 — Validation + cast check (1 day)

| ID | Action | Done when |
|----|--------|-----------|
| **A4.1** | Move pure `CastPackageCrossCheck` (or equivalent) into Adaptation | ✅ `Validation/CastPackageCrossCheck` + façade |
| **A4.2** | Optional: deterministic “speakers ⊆ book names” heuristic for Mary-style books | ✅ `FindSpeakersMissingFromBook` + warnings |
| **A4.3** | Do **not** move cast portrait / ElevenLabs here | Boundary held |

**Exit:** Text-level package checks available without Engine.

---

### Phase 5 — Versioning, cache, benchmark identity (1–2 days)

| ID | Action | Done when |
|----|--------|-----------|
| **A5.1** | `engine_sha` / `adaptation_version` = hash of Adaptation assembly informational version **or** git tree of Adaptation project + embedded prompts | Stable string |
| **A5.2** | Benchmark cache key: `{model}_{prompt_sha}_{adaptation_sha}_temp{t}` | Code change busts cache without prompt change |
| **A5.3** | History fields: `PromptVersion`, `AdaptationVersion`, `AppHead` (optional) | Dashboard can filter |
| **A5.4** | Refuse benchmark if Adaptation sources or Stage‑1 prompts dirty | Same spirit as prompt gate |
| **A5.5** | AGENTS.md: “Stage‑1 logic only in PageToMovie.Adaptation” | Process |

**Exit:** Prompt-only cache lie is fixed; surface = whole module.

---

### Phase 6 — Hardening (ongoing)

| ID | Action | Done when |
|----|--------|-----------|
| **A6.1** | Architecture test: Adaptation csproj must not reference Engine | CI |
| **A6.2** | Architecture test: no `ProjectStore` symbol in Adaptation | ✅ source scan test |
| **A6.3** | Golden fixture tests: fixed book → structural fountain checks | ✅ `AdaptationGoldenFixtureTests` (Mary/Buster) |
| **A6.4** | Optional later: `IAdaptationChat` adapter if Engine chat API evolves | Clean |
| **A6.5** | Do **not** fold Stage2/clip gen into this module without a new plan | Scope lock |

---

## 6. Engine orchestration checklist (after extract)

`ScreenplayService.CreateDraftFromBookAsync` becomes:

```text
1. Read book_full.txt                          (Engine / store)
2. Load user target from config if any         (Engine FilmRuntime I/O)
3. analysis = Adaptation.AnalyzeBook(text)
4. runtime  = Adaptation.ResolveTarget(text, override)
5. result   = await Adaptation.ConvertAsync(request, chat, progress)
6. Save fountain + vision_meta                 (Engine)
7. Optional: book registry cache               (Engine)
8. Auto-git commit                             (Engine)
```

Import UI / `FilmLengthCard`: unchanged externally; API still Engine.

---

## 7. Testing strategy

| Layer | Tests |
|-------|--------|
| Adaptation unit | Density bands (Mary/Buster/TTH), clamp, heuristic convert smoke |
| Adaptation + fake chat | ConvertAsync with recorded responses |
| Engine integration | CreateDraftFromBook writes files |
| Benchmark smoke | `--book Mary` dry-run / cached |
| Architecture | Project reference rules |

---

## 8. Risks and mitigations

| Risk | Mitigation |
|------|------------|
| Large `BookToFountainConverter` move breaks build | Phase 1 first; converter in Phase 2 with green builds each commit |
| `IChatClient` lives under Engine | Move interface to Core/Adaptation first (A0.3 / A2.1) |
| Vision meta type duplication | Single DTO in Core or Adaptation; Engine serializes to disk |
| Behavior drift | Side-by-side fixture compare before deleting old path |
| Over-wide module | Explicit non-goals: Stage2, media, OCR prepare, cost |

---

## 9. Suggested sprint sequence

| Sprint slice | Deliverable |
|--------------|-------------|
| **Day 1** | A0 + A1 skeleton, density/analyzer moved, tests green |
| **Day 2–3** | A2 converter + façade; ScreenplayService wired |
| **Day 4** | A3 runtime split; import length still works |
| **Day 5** | A5 cache/history adaptation_sha; A6.1–2 architecture tests |

---

## 10. Definition of done (module)

- [x] `PageToMovie.Adaptation.dll` builds standalone with Core only (+ allowed packages)  
- [x] No `ProjectStore`, no project paths, no user DB inside Adaptation  
- [x] All Stage‑1 LLM conversion goes through `AdaptationService`  
- [x] Engine `BookToFountainConverter` is mapping-only (no pure-helper forwarders)  
- [x] Engine only orchestrates I/O + DI of chat  
- [x] Benchmark uses same façade + `adaptation_sha` in cache/history  
- [x] Unit + architecture tests green  
- [x] README boundary reviewed  

---

## 11. Non-goals

- Extracting Stage2 / shot planner / clip gen  
- Moving PDF OCR prepare into Adaptation  
- Microservice / separate repo (same solution, separate project is enough)  
- Rewriting prompts as part of the extract (behavior-preserving move first)

---

## 12. One-sentence summary

**Carve Stage‑1 into `PageToMovie.Adaptation` with request/result contracts and injected chat; keep storage and jobs in Engine; version the whole module for cache and benchmarks so prompts and heuristics version together without a hand-maintained file list.**
