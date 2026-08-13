# Blazor component refactor — progress & metrics

**Goal:** reduce oversized Blazor page files (a source of UI bugs — too much markup +
logic + shared mutable state in one file) by extracting reusable components into a
dedicated Razor Class Library. Branch: `refactor/blazor-components` (superseded by
`refactor/blazor-components-v2`, which merges this work forward onto current master —
the original branch was 38 commits stale).

## Baseline (master @ de32bd2b)

`Components/Pages/*.razor`: **28,722 lines across 40 pages**, only **4** shared components.

| Page | Baseline lines |
|------|---------------:|
| Scenes.razor | 5,232 |
| Characters.razor | 2,977 |
| Review.razor | 2,733 |
| Configuration.razor | 2,212 |
| Home.razor | 1,932 |
| Admin.razor | 1,874 |

## Done so far (verified: build 0 errors, tests pass, fakes-browser smoke)

1. **New RCL `PageToMovie.Components`** — a dedicated project for shared, dependency-free
   UI components (Web references it; components keep `@namespace PageToMovie.Web.Components`
   so no consuming pages needed edits).
2. **Moved 5 pure presentational components** into it: `CapabilityLockedControl`, `CostPie`,
   `PasswordToggleButton`, `PromoCard`, `StatCard`. (Service-dependent shared components —
   `FilmLengthCard`, `StudioProcessStrip`, `VisualMediumCard`, `VoiceCaptureStep`,
   `CostLegend` — stay in Web; moving them needs DI abstraction.)
3. **New reusable `ConfirmModal`** component; applied to the delete-scene and delete-clip
   dialogs in `Scenes.razor` (testids preserved).

Component count: **4 → 6 shared** (+ pattern for more). Scenes.razor: 5,232 → 5,210.

## Findings that change the plan

- The "~63 modals in Scenes" figure was a **loose-grep overcount** (matched every `modal`
  CSS class). Scenes actually has **7** Bootstrap modal dialogs; the other big pages
  (Characters/Review/Configuration/Admin/Home) use **no** Bootstrap modals. So `ConfirmModal`
  has limited additional reach.
- The real file-size reductions therefore require extracting **large, state-heavy sections**
  (e.g. Scenes' clip-editor modal, the scene-row loop body → `SceneCard`, Characters' cast
  panels). Those rewire `[Parameter]`/`EventCallback`/two-way bindings and are **regression-prone**
  — best done with a human able to click through the result, not blind.

## Option B executed (2026-08-07 overnight, autonomous — no real-time visual review available)

Chose **Option B (code-behind split)** over Option A for unattended work: Option A's
state-heavy extractions were explicitly flagged above as regression-prone and needing "a
human able to click through the result, not blind" — not appropriate to attempt while the
user was asleep. Option B is fully compiler-validated with no behavior change, so it's safe
to execute and verify without a real-time visual review.

Split every big page's `@code { }` block into a `PageName.razor.cs` partial class
(`public partial class PageName { ... }`), keeping the `.razor` file to markup + directives:

| Page | Before | .razor after | .razor.cs | Reduction |
|------|-------:|-------------:|----------:|----------:|
| Scenes.razor | 5,290 | 2,236 | 3,044 | 58% |
| Characters.razor | 3,067 | 1,058 | 2,026 | 65% |
| Review.razor | 2,752 | 1,047 | 1,717 | 62% |
| Configuration.razor | 2,212 | 717 | 1,507 | 68% |
| Home.razor | 1,943 | 699 | 1,259 | 64% |
| Admin.razor | 1,857 | 945 | 924 | 49% |

Verified after every split: full solution build (0 errors), non-UI suite (1607/1608 — only
the pre-existing unrelated `AutoTextMergerTests` failure), and a fakes-browser pass loading
all six pages (Home incl. Manage panel, Configuration, Characters, Review, Scenes, Admin) —
all render and behave identically to before the split.

**Gotcha found while doing this — not every `@code` member can move.** A `.razor.cs`
partial class compiles as plain C#, but some `@code` members contain *inline Razor markup*
(`RenderFragment X => __builder => { <div>...</div> };` or `RenderFragment X => @<div>...</div>;`),
which only compiles via the Razor source generator, never as plain C#. Found 3 such members
across the 6 pages (`Scenes.GenPartialAlert`/`GenErrorAlert`, `Characters.RenderVoiceEditor`,
`Home.ImportPanel`) — each left behind in a small residual `@code { }` block at the end of
its `.razor` file with a comment explaining why, instead of moving to the `.cs` partial.
Before doing this split on another page, `grep -n "__builder =>\|@<"` its `@code` block first.

Second batch, same night, same process:

| Page | Before | .razor after | .razor.cs | Reduction |
|------|-------:|-------------:|----------:|----------:|
| AdminModelsCatalog.razor | 991 | 420 | 583 | 58% |
| Login.razor | 854 | 441 | 424 | 48% |
| AdaptationScreenplay.razor | 837 | 302 | 547 | 64% |
| AdminUsers.razor | 660 | 383 | 289 | 42% |

No inline-markup `@code` members in this batch (checked each with
`grep -n "__builder =>\|@<"` first, per the gotcha above — none found). Two new
missing-using gotchas surfaced by the compiler (both one-line fixes, not logic bugs):
`JsonObject`/`JsonNode` need `System.Text.Json.Nodes`; `QueryHelpers` needs
`Microsoft.AspNetCore.WebUtilities`; `AuthOptions` needs `PageToMovie.Core.Options`.
Verified the same way: full build, non-UI suite 1607/1608, fakes-browser pass on all
four pages (AdminModelsCatalog's edit/validate/delete buttons, AdminUsers' grant/block/
delete buttons, Login's already-authenticated redirect, AdaptationScreenplay's editor).

**10 of the biggest pages now split.** Remaining pages under ~600 lines are lower
priority — same mechanical process applies if/when worth doing.

## Still open (needs the user — Option A)

The state-heavy extractions from the "Findings" section above (Scenes' clip-editor modal →
`ClipEditorModal`, scene-row loop body → `SceneCard`, Characters' cast panels) are unchanged
by this session's work — they still need a human clicking through the result, not blind
unattended execution. Queued, not started.
