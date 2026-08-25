# Plan: catching the UI bugs the suite keeps missing

The suite is 43 files and it did not catch a single one of the bugs found on 2026-08-25. That is
not a volume problem. Every one of those bugs is a **lifecycle** bug, and the harness is built in a
way that structurally cannot express lifecycle. This plan fixes the harness first and the coverage
second, because more tests written the current way would miss the same things again.

## The bugs this plan has to catch

| # | Bug | Why it was invisible |
|---|---|---|
| 1 | Home disposed the app-wide `JobHubClient`, killing live updates for the session | No test ever loads Home and then navigates away |
| 2 | Two windows on one folder both wrote each clip, truncating each other | Every page gets its own context, so no two windows ever share a folder |
| 3 | Generated media never reached the folder | Assertions read the server's registry, not the folder |
| 4 | Progress bar never appeared | Tests wait up to 60 s for outcomes, so a dead socket looks like a slow one |
| 5 | Shot plan did not refresh after regen | Tests reload or deep-link between steps |
| 6 | Takes list showed 1 of 6; selecting a take played a different one | No test asserts *which* bytes a take resolves to |

## Why the suite misses them

**Tests deep-link past the app's own navigation.** `PipelineFlow.RunToGeneratedClipsAsync` enters at
`/adaptation/import`. Across all 43 files, no test loads `/` and then navigates in-app to do work.
Bug 1 needs exactly that sequence — mount Home, leave Home, then generate — so the one step that
breaks it is the step no test takes.

**One page per context, a new context per page.** `AppFixture.NewPageAsync` calls
`Browser.NewContextAsync` every time, and the OPFS shim it installs is scoped to the browsing
context. Two windows therefore get two different folders, and bug 2 cannot reproduce by
construction.

**Assertions read the server, not the browser.** `data-clips-on-disk` comes from the server's media
registry, which is populated by `RegisterMediaAsync`. In bug 3 registration happened while the
bytes did not land, so that counter was right and the folder was wrong.

**The suite is patient, and patience hides transport failures.** Waiting 60 s for an outcome cannot
distinguish "delivered over the socket" from "the socket was dead and a poll rescued it" from "a
reload fixed it". Bugs 1 and 4 both live in that gap.

**Console output is collected in 2 of 43 files and asserted in none.** `PageSweepTests` and
`PipelineE2ETests` capture console errors; nothing fails a test for them.

## Phase 0 — harness enablers

Nothing below is a test. These are the capabilities without which the tests cannot be written.

**0.1 Two windows sharing one folder.** Add `AppFixture.NewPageInContextAsync(IBrowserContext ctx)`
alongside `NewPageAsync`. OPFS is per-context, so two pages in one context share a media folder —
that is what makes bug 2 expressible. Keep the existing per-context default so current tests are
unaffected.

**0.2 See what the socket delivered.** `JobHubClient` now emits `[hub]`-prefixed console lines
(connect with the group joined, every `JobUpdated`, close reason). Add a `HubTrace` helper that
subscribes to `page.Console`, buffers those lines, and exposes `JobUpdatesReceived`,
`ConnectedGroups`, and `Closes`. This converts every "did it eventually work" assertion into a
"did it work *the way it is supposed to*" assertion.

**0.3 Assert on the folder, not the registry.** Add an `OpfsMedia` helper that reads a path out of
OPFS via `page.EvaluateAsync` and returns size plus SHA-256. Bug 3 is only detectable against the
actual bytes.

**0.4 Realistic entry.** Add `PipelineFlow.EnterViaHomeAsync(page)` that loads `/`, waits for it to
settle, and navigates in-app. Use it in the lifecycle tests below; leave the fast deep-link path for
tests that are not about lifecycle.

**0.5 Fail on console errors.** Move the `page.Console` / `page.PageError` capture from the two
files that do it into `NewPageAsync`, and assert an empty error list on dispose, with an explicit
allow-list for known third-party noise (`feature_collector.js` deprecation warnings and similar).

## Phase 1 — suite-wide invariants

Highest leverage in the plan: these turn six specific bugs into properties every test checks, so the
*next* bug of the same shape is caught by tests that were never written for it.

**I1 — live updates actually arrive.** Any test that runs a job asserts `HubTrace.JobUpdatesReceived
> 0` for that job id. Bugs 1 and 4 both fail this immediately, and so would any future regression
that silences the socket.

**I2 — the socket outlives navigation.** After a test navigates, assert there is no `[hub] closed`
without a following `[hub] connected`. This is bug 1 stated as a property rather than a scenario.

**I3 — registered media exists on disk.** After any generating test, every path the server has in
its media registry must exist in OPFS at ≥ 1 KB. That is bug 3, and it also catches the truncation
half of bug 2.

**I4 — distinct takes have distinct bytes.** Where a test produces more than one take of a clip,
their hashes must differ. This is bug 6 and the takes-6/7/8 symptom.

## Phase 2 — targeted regression tests

Each is named for the failure, not the feature.

1. `Hub_survives_leaving_the_home_page` — enter via Home, navigate to Film, generate one clip,
   assert I1 + I3. Fails against the pre-`94f4bfc4` build.
2. `Two_windows_on_one_folder_write_each_clip_once` — two pages in one context, both with the media
   folder connected, generate one clip, assert the file exists once at full size and both windows
   resolve it. Assert the losing window logged that it stood down rather than wrote.
3. `Each_take_resolves_to_its_own_bytes` — generate three takes of one clip, assert three distinct
   hashes and that selecting take N plays take N.
4. `Takes_count_matches_takes_on_disk` — the `Takes (1)`-of-6 bug.
5. `Progress_advances_while_a_job_runs` — with `VideoDelayMs` raised, assert the progress element
   appears and its value increases before completion.
6. `Regenerated_shot_plan_refreshes_without_a_reload` — regen, then assert the list updates with no
   navigation or reload.
7. `Extend_saves_only_the_new_tail` — the fake already concatenates predecessor + new fixture, so
   assert the saved file is shorter than the combined provider response.

## Phase 3 — fakes work this depends on

**3.1 Per-take distinct bytes (blocking for tests 2, 3, 4).** `FakeGrokVideoClient` returns one of
three fixture MP4s round-robin, so two takes of one clip can legitimately be byte-identical. While
that is true, "all takes render the same video" is indistinguishable from correct fake behaviour.
Give each generation a unique tail — a few bytes of the job/take id appended after the MP4 moov, or
a distinct fixture per take index — so identity is meaningful.

**3.2 A controllable delay.** `FakesOptions.VideoDelayMs` already exists; make sure the UI tests can
set it per test rather than per host, so test 5 can have a slow job without slowing the suite.

## Order of work

Phase 0 first and all of it — the enablers are small and every later item depends on them. Then
Phase 3.1, because three of the seven tests are unwritable without it. Then Phase 1, which is where
the durable value is. Phase 2 last: by then most of those tests are a few lines each, and two of
them (1 and 2) should be verified to *fail* against the commit before their fix, since a regression
test that has never been red has not been shown to test anything.

Follow the repo's batch-then-bisect rule while landing these: batch roughly eight before a full
suite run, and bisect only the failures.
