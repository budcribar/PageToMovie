# Handoff: continuation clips are planned as if the previous clip never happened

**Status: fixed in the planner, 2026-08-25. Needs a shot-plan rebuild per project to take effect,
and a paid benchmark run to confirm the new prompt beats the old one.** Original write-up below,
then what changed.

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

---

## What the fix turned out to be

The handoff's design question was "give the planner the predecessor's end state" (prevent) vs "lint
it after the fact" (detect). Both, but the prevention landed one layer earlier than expected.

Stage 2 does not author clip actions — `BuildVisualPrompt` copies the beat's `visual_event`
verbatim. What Stage 2 *does* decide is whether a clip extends at all, and that decision was already
being made by an AI classifier with the previous beat in hand: **`ExtendCutClassifier`**. It writes
`cut_decision` per beat, and `ForceNone` short-circuits on it, so it is authoritative for every
non-first clip.

It was asking the wrong question. The shipped prompt (v1) read, in full:

> `extend: same place continuous business, small gesture, should blend from previous clip tail.`

Same place, small gesture — both true of the Mary19 pair. Nothing asked whether the beat *opens
where the previous beat ended*. Two starvations made it worse: the payload truncated `prev_visual`
to 40 tokens and `visual_event` to 50 (the benchmark harness sends 160/200), and it never sent
`same_location` even though the prompt reasons about it.

### The displaced subject is the one that isn't acting

Read C2's action again and notice who does what:

> THE CHILDREN twist in their seats and point. They laugh and clap at the snow-white lamb
> **standing** small among the ink desk legs

Every verb belongs to the children — twist, point, laugh, clap — and every one of them is
continuous small motion in the same room. The lamb does nothing at all. It is *described*, in a
prepositional clause, standing somewhere it has never been shown to reach.

So a staging test that reads the acting subject finds a textbook extend and passes the beat. The
subject that crossed the room is the one with no verb. The prompt therefore has to say, explicitly,
that a described position is a staging claim and every named subject gets compared — not just the
one the sentence is about. Without that clause the rewritten prompt would very likely relabel this
exact pair `extend` all over again.

It also settles the bridge question: there is no journey to describe. The beat does not have the
lamb travelling, it has the lamb already arrived and still.

### Changes

| Change | File |
|---|---|
| Prompt asks the staging test: an extend continues the previous last frame, so a beat whose subjects are somewhere the previous beat never showed them reach is a `hard_cut`; an extend cannot teleport anyone or skip a journey | `ExtendCutClassifier.SystemPrompt` |
| …and compares **every named subject**, not just the acting one — a subject merely described standing/sitting somewhere new has moved just as surely, and that is the case the real beat was | `ExtendCutClassifier.SystemPrompt` |
| Payload budgets raised to the benchmark's 160/200 tokens; `same_location` now sent | `ExtendCutClassifier.BuildChunkPayload` |
| Every beat stamped `cut_decision_rule` — the prompt version, or `"heuristic"` when the classifier is off | `ExtendCutClassifier` |
| Stage 2 copies that onto each clip as `continuity_rule` | `Stage2PlannerService.PlanSingleClip` |
| New lint rule `continuation_unchecked`: an `extend_previous` clip with no `continuity_rule` came from a plan built before the staging test — rebuild it. **Advisory**, so it logs without staling clips the user would then pay to regenerate | `ShotPlanLint` |
| `Finding.Advisory`; `ProjectStore` stales only on non-advisory findings | `ShotPlanLint`, `ProjectStore` |
| The refiner rewrites continuation from camera framing alone, so a clip it turns *into* a continuation drops its inherited stamp and the lint reports it unchecked | `ShotPlanRefiningClassifier` |
| `<Continuity>` now says positions come from the last frame and the action is what happens next *from there*. This does not fix a bad plan — it decides which way the model leans when an old plan still contradicts itself, and holding position beats teleporting | `ClipVideoPromptBuilder` |
| `v4_staging.txt` + meta added to the extend_cut eval corpus, byte-identical to the shipped prompt; benchmark default points at it; `v1_product.meta.json` no longer claims to match the shipped prompt | `evals/classifier_benchmarks/prompts/extend_cut/`, `tools/ClassifierBenchmarks` |
| Operator reference gained the same staging rule (documentation only — not on the product path) | `prompts/stage2_shot_planner.txt` |

### The resolution is a cut, not a bridge

The alternative was to keep the extend and prepend a bridging clause so the clip shows the travel.
Rejected on two counts. Stage 1 never wrote any travel — the beat has the lamb *standing*, already
arrived — so Stage 2 would be inventing a story event outright, and the invented motion would have
to fit inside the clip's duration alongside the beat's own action. And a cut is what film grammar
does with an off-screen traversal anyway. `ForceNone` → fresh gen with locked plates: no
contradiction, no invention.

### Tests

`PageToMovie.Tests/ContinuationStagingTests.cs` (11 assertions across prompt content including the
described-subject clause, the prompt↔benchmark-file drift guard, the beat stamp, the stamp reaching the blueprint end-to-end, the
refiner's stamp handling, and the three lint cases), plus two continuity-block cases in
`ClipVideoPromptBuilderTests`. Full free suite: 2713 passed, 0 failed.

### Not verified

- **No paid run.** The new prompt has not been benchmarked against gold. Run
  `dotnet run --project host/tools/ClassifierBenchmarks -- run --tasks extend_cut --prompts v1_product,v2_grounded,v3_speaker_cue,v4_staging`
  to see whether the staging test costs accuracy on the speaker-cue cases v2/v3 were tuned for.
  The shipped prompt is v1-plus-staging, deliberately *not* v3-plus-staging: v2 and v3 were never
  shipped and their last recorded run was a tie, so folding them in was a bigger behaviour change
  than this bug warrants. That is a separate decision, and the benchmark is how to make it.
- **No rebuilt plan looked at.** The fix only reaches a project on the next Stage 2 rebuild.

## Constraints this fix respected

1. **No story-specific strings in Engine/Web/API code.** The prompt names no character, place or
   phrase; the lint reads structured plan fields only.
2. **Prefer AI judgment over special-case lists.** The staging test is a prompt rule, not a phrase
   ladder. The heuristic baseline (`BaselineHardCut`) was deliberately left alone — it is the eval
   comparison baseline.
3. **No generation-time special cases.** Nothing is patched at gen; the lint surfaces and the
   rebuild fixes.

## Verification notes

- `UseFakes=true` gives a full offline run with login bypass; see `CLAUDE.md`.
- The user runs the MAIN repo at `C:\Users\budcr\source\repos\PageToMovie`. **Work only in the clone
  at `C:\Users\budcr\source\repos\claude\PageToMovie`** — never build, test, or edit the main one.
- `dotnet test PageToMovie.Tests` from `host/` is free. Never call paid provider APIs from a
  non-`LiveApi` test.
- `PageToMovie.UiTests` does not compile on this branch and did not before this work either:
  `AppShellTests.cs(78)` uses `PageGetByTextOptions.IgnoreCase`, which the pinned Playwright no
  longer has. Unrelated, untouched.
- Branch before committing; push only when asked. End commit messages with
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

## Still pending from the previous session

- A redeploy is needed before the user can confirm the measured-seam fix (`f2b19866`) actually
  restores the first spoken word.
- `host/PageToMovie.UiTests/PLAN-lifecycle-coverage.md` — an unstarted plan for making the UI suite
  able to catch this class of bug. Committed, not begun.
- A background task is fixing a duplicate terminal job publish and a doubled `mode=` token in the
  `[Grok] Submit` log line. Unrelated to this work.
