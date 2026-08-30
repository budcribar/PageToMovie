# What the video model actually responds to

Measured findings about the clip-generation prompt, from debugging two defects on Mary19 over a
single long session: a continuation clip's narrator dropping the line's first word, and a subject
being restaged mid-continuation. Both were fixed. Almost every intermediate hypothesis was wrong,
and the wrong ones are as useful as the right ones — they are recorded here so nobody re-derives
them.

Everything below was measured against the provider's own extend endpoint with the real shipped
prompt, one variable at a time.

---

## 1. Never describe what the video input already carries

This was the answer three separate times in one session. It is the single most useful rule here.

A video-extend is generated from the previous clip's video. That video already contains the
setting, the lighting, the framing, the grade, the audio bed, and where everyone is standing.
Describing any of it again does **not** confirm it — it asks the model to produce it, and the model
obliges by re-establishing the shot rather than continuing it.

| what was duplicated | symptom | fix |
|---|---|---|
| the spoken line (`<Speech>` in the plan **and** `<Audio>` at gen time) | wasted budget; two editable surfaces, wrong one winning | Stage 2 stopped emitting `<Speech>` |
| the foley (`<Sound>` in the plan **and** `<Foley>` inside `<Audio>`) | narrator dropped the line's first word | Stage 2 stopped emitting `<Sound>` |
| the whole look (`<StyleLock>`, `<Setting>`, `<Lighting>`, `<Camera>`, `<Optics>`, `<Grade>`) on a path where the video shows it | subject restaged — thrown back across the room | dropped from the prompt on continuations only |

A *fresh* clip is the opposite case: it has no source video, so it must be told everything. Every
one of these fixes is gated on generation mode, never applied globally.

Prior art in the same family: `ead9ed4f` removed `<PreviousClip>` prose because re-describing the
previous clip made the model replay it.

## 2. Content beats direction — instructions lose

The model does not follow instructions about what *not* to do when the surrounding content says
otherwise. Every attempt to fix a defect by adding or rewording an instruction failed:

- `Start speaking: "It" — do not skip, delay, or swallow the opening word` was already in the prompt
  for every take that dropped the word.
- `Positions come from that frame: everyone and everything starts exactly where the previous clip
  left them` was already in the continuity block for every take that restaged the subject.

Both lost to content — a foley request over the same moment, and a look description that
re-established the shot. **If a defect is caused by something the prompt says, remove that thing.
Do not add a sentence telling the model to ignore it.**

## 3. Audio is one track, and it is contended

The model generates a single audio track. Requests for effects compete with the dialogue for the
opening moment, and the dialogue loses. Two requests for laughter against one for narration is
enough to swallow the line's first word.

On a continuation the effects are already in the source audio and continue on their own, so
`<Score>` and `<Foley>` are omitted there entirely.

## 4. A stated position is an instruction to place

Naming where something *is* makes the model put it there — even when it is already there, and even
when the phrase is a true description of the current frame. On Mary19 S02C02 the previous clip left
the animal well down the aisle; the action said "…standing small among the ink desk legs", which was
true; the extend moved it back to the front of the room.

`ContinuationActionClassifier` rewrites a continuation beat's action to say only what *happens*.
Chat is preferred; `EventsOnly` always runs on clips that actually continue so a miss cannot ship
a restage. Place-restating `blocking_notes` are not appended after the rewrite.
Note this was **not** sufficient on its own — see rule 1; the look blocks were the larger cause.

## 5. The model is stochastic. Single-trial A/B tests are worthless

The most expensive lesson. Several hours of one-shot comparisons produced a chain of confident
conclusions, and repeated runs falsified most of them:

- "the continuity block causes it" — did not replicate
- "the action's stated position causes it" — did not replicate
- "the anti-swallow instruction causes it" — did not replicate; the failing prompt did not contain it

**Run the same prompt at least three times before comparing two prompts.** Establish that the
baseline fails reliably; only then is a variant that succeeds meaningful. The findings that survived
were the ones with repeats behind them (0/2 → 3/3 → 3/3) or many real takes (five extends dropped the
word, two fresh generations kept it).

## 6. Test in the provider playground, not the pipeline

Playground runs cost nothing; pipeline takes cost money. Every prompt hypothesis should be settled
there first.

One trap: **what the plan holds is not what ships.** The generation-time blocks — `<Audio>`,
`<Continuity>`, `<Characters>`, `<CastCount>`, `<Identity>`, `<Clip>`, `<Negative>` — are assembled
by `ClipVideoPromptBuilder` and never appear in the blueprint's `visual_prompt`. Two experiments in
this session were void because the pasted prompt contained no dialogue line at all.

The take sidecar's `visual_prompt` field stores the **complete prompt as sent**. That is the only
thing worth pasting into a playground.

---

## Where this lives in the code

| Rule | Code |
|---|---|
| Mode-gated prompt assembly | `ClipVideoPromptBuilder.Build`, `DropWhatTheSourceVideoShows`, `BuildAudioBed` |
| Plan emits no duplicated speech/sound | `Stage2PlannerService.BuildVisualPrompt` |
| Legacy plans stripped at gen time | `ClipVideoPromptBuilder.SanitizeActionText` |
| Continuation action rewritten to events | `ContinuationActionClassifier` |
| Extend vs cut, including the staging test | `ExtendCutClassifier` (+ `host/evals/classifier_benchmarks/prompts/extend_cut/`) |

The original investigation, including five explanations ruled out before any of this,
is in [handoff-continuation-clip-planning](handoff-continuation-clip-planning.md).
