# Handoff: continuation clips are planned as if the previous clip never happened

## The problem

Mary19, Scene 2. C1 ends with the lamb coming through the door at the **back** of the schoolroom.
C2 was generated as a correct video-extend from C1 — and the lamb is at the **front**, already among
the desks. The extend machinery is not at fault. Take 09's own prompt asks for exactly what was
rendered:

```
<Action>:      THE CHILDREN twist in their seats and point. They laugh and clap at
               the snow-white lamb standing small among the ink desk legs
<Camera>:      Medium desk-row shot, 35mm lens, camera slowly pushes in on laughing
               children and the small lamb among desk legs, all grounded with headroom.
<Continuity>:  This is a seamless EXTENSION of the provided previous video. Pick up from
               its last frame. Same character identity, wardrobe, lighting, and location.
               Natural progressive motion only — do not invent a new establishing shot…
```

`<Action>` places the lamb among the desks. `<Continuity>` says to continue from a frame where it is
at the door. The two contradict each other inside one prompt, and the model resolved it in favour of
the Action. Nothing in the plan describes the lamb *travelling* from door to desks.

**So the defect is in the shot plan, not in generation.** Stage 2 writes each clip's action from the
screenplay beat without knowing where the previous clip left the cast, so a continuation clip
happily opens on a new spatial arrangement.

## What is already ruled out

Do not re-derive these — each cost a debugging round already:

- **Not a provider cache.** Takes 6/7/8 had three distinct request ids and three different byte
  counts.
- **Not the identity reseed.** Fixed in `f91628c1`; the job log now reads `[Identity] Cast set shrank
  to a subset … — continuing`.
- **Not a missing extend.** The job log reads `[Continuity] Imagine video-extend from S02C01
  (file_…)` and `mode=video-extend … refs=0`.
- **Not the slice.** Fixed separately in `f2b19866` — that bug clipped the first spoken word, worth
  a fraction of a second, nowhere near a room's width.
- **Not `<PreviousClip>` prose re-embedding.** Fixed in `ead9ed4f`.

## Constraints this fix must respect

From `AGENTS.md` and the project's standing rules:

1. **No story-specific strings in Engine/Web/API code.** No "lamb", no "schoolroom", no cast names.
   The fix must work unchanged for the next book.
2. **Prefer AI judgment over special-case lists** — prompts and metadata, not a growing if-ladder of
   spatial phrases.
3. **No generation-time special cases.** Fix the planner and surface the defect through
   `ShotPlanLint`; never patch a plan defect at generation. (`host/PageToMovie.Engine/ShotPlanLint.cs`
   already has rules reading `<Cast>` and `<StyleLock>` tags — a continuity rule belongs beside them.)

## Where to look

| What | Where |
|---|---|
| Plan assembly, `BuildVisualPrompt`, tag emission | `host/PageToMovie.Engine/Stage2PlannerService.cs` |
| The `<Continuity>` block for extends | `host/PageToMovie.Engine/ClipVideoPromptBuilder.cs` |
| Lint rules | `host/PageToMovie.Engine/ShotPlanLint.cs` |
| Field tag names | `host/PageToMovie.Core/Utils/PromptFieldTags.cs` |
| Stage-2 prompt template | `prompts/` (embedded at Engine build time; `PAGETOMOVIE_PROMPTS_DIR` overrides locally) |

## The open design question

Two directions, and the first is probably right:

1. **Give the planner the predecessor's end state.** When planning clip N of a scene, pass what clip
   N-1's action left on screen, and require the action for N to continue from it rather than restate
   a fresh arrangement. This is a Stage-2 prompt-template change plus threading the previous beat's
   action through. Costs a shot-plan regeneration to take effect.
2. **Lint it after the fact.** A `ShotPlanLint` rule that flags a continuation clip whose `<Action>`
   places an already-on-screen character somewhere the predecessor did not leave them. Cheaper, but
   detect-only, and phrasing this generically without a phrase list is the hard part.

They are complementary — 1 prevents, 2 catches. Worth deciding whether the lint can be written
without drifting into the special-case list rule 2 forbids.

## Verification notes

- `UseFakes=true` gives a full offline run with login bypass; see `CLAUDE.md`.
- The user runs the MAIN repo at `C:\Users\budcr\source\repos\PageToMovie`. **Work only in the clone
  at `C:\Users\budcr\source\repos\claude\PageToMovie`** — never build, test, or edit the main one.
- `dotnet test PageToMovie.Tests` from `host/` is free. Never call paid provider APIs from a
  non-`LiveApi` test.
- Batch roughly eight changes before a full suite run, then bisect only the failures.
- Branch before committing; push only when asked. End commit messages with
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

## Still pending from the previous session

- A redeploy is needed before the user can confirm the measured-seam fix (`f2b19866`) actually
  restores the first spoken word.
- `host/PageToMovie.UiTests/PLAN-lifecycle-coverage.md` — an unstarted plan for making the UI suite
  able to catch this class of bug. Committed, not begun.
- A background task is fixing a duplicate terminal job publish and a doubled `mode=` token in the
  `[Grok] Submit` log line. Unrelated to this work.
