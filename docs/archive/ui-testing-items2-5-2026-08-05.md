# Items 2–5 results (UITestingBranch)

**Project:** `local/Items2to5_*` · **Fakes:** on · **Date:** 2026-08-05

## Item 2 — Cast locks

| Character | Voice | Lock image |
|-----------|-------|------------|
| Character_Narrator | OK | OK (`upload-ref`) |
| Character_Officer | OK | OK |

Generate with `requireLockedCharacters: true` was **accepted** (cast not blocking).

## Item 3 — Generate / double-submit

- Without `XAI_API_KEY`: job errors immediately even under fakes.
- With dummy key: gen-scene **done**.
- Double POST gen-scene: both accepted (no API-level single-flight).

## Item 4 — Home strip wait

Film step appears after wait; links to `scenes` when stage2 ready.

## Item 5 — Cost UI

Length input + Agree & Continue work after pipeline seed; navigates to scenes.
