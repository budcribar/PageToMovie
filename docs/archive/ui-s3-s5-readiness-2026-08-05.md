# S3–S5 readiness audit (UITestingBranch)

**Generated:** 2026-08-05T18:21Z · **Branch:** `UITestingBranch` · **Fakes:** on  
**Project:** `local/S3S5_*` via API import-fountain + sign-off + stage2

Full machine report: `artifacts/ui-audit/s3-s5-readiness-report.md`

## Setup used

1. `POST /api/projects`  
2. `POST .../adaptation/import-fountain` (Tell-Tale Heart fixture) → draft, `signed=false`, `readyForShots=false`  
3. UI probe (S3)  
4. `POST .../screenplay/sign-off` → approved  
5. UI probe (S4)  
6. `POST /api/jobs/stage2` → queued; status stayed `stage2_ready=false`, clips=0 (resource lock / incomplete under fakes)  
7. UI probe (S5)

## Results

### S3 — Fountain draft, **not** signed

| Check | Result | Notes |
|-------|--------|--------|
| API import-fountain | PASS | 4 scene headings, signed=false |
| Strip Cast/Estimate/Film on Home | FAIL / inconclusive | `studio-step-*` not found on first Home paint (likely strip not mounted until full-studio chrome / client readiness refresh) |
| Characters page | PASS | Loads; blocked copy unclear |
| Agree on Cost | PASS | Not present/enabled without readiness path |
| Generate on Scenes | PASS | Not freely enabled |

### S4 — After **sign-off**

| Check | Result | Notes |
|-------|--------|--------|
| API sign-off | PASS | ok, 4 scenes, 2 characters |
| Strip **Estimate** enabled → `cost` | PASS | |
| Strip **Cast** enabled | PASS | |
| Strip **Film** still disabled | PASS | Correct until stage2 clips |
| Agree on Cost enabled | FAIL | Control missing or disabled after sign — investigate Cost page binding |
| Generate still gated | PASS | No Generate enabled without clips/cast locks |

### S5 — Stage2 / Film ready

| Check | Result | Notes |
|-------|--------|--------|
| Stage2 job accepted | PASS | Queued C# planner |
| Stage2 completes with clips | **No** | `stage2_ready=false`, scenes=0, clips=0; log “Waiting for resource lock…” |
| Strip Film after stage2 | PASS | Still disabled (consistent with no clips) |
| Generate enabled path | FAIL | Cannot exercise double-submit until stage2+cast ready under fakes |

## Implications before code fixes

1. **S4 strip gating works** after sign-off (Estimate/Cast open, Film closed) — important product behavior confirmed.  
2. **S3 strip visibility** needs a reliable Home/studio chrome wait in tests (not necessarily a product bug).  
3. **Agree & Continue** after sign-off still flaky/missing — ties to Cost/project-id binding (same family as missing length card).  
4. **S5 cannot finish** until fakes complete stage2 (or tests seed stage2 artifacts) **and** cast voice/image locks satisfy `CanScenes`.  
5. Do **not** start P0 UI fixes from incomplete S5 evidence; S4 strip behavior is the solid readiness finding.

## Next tests (still on this branch)

- [ ] Seed or finish stage2 under fakes → re-check Film strip + Generate  
- [ ] Lock fake cast voices/images → `CanScenes`  
- [ ] S5 double-submit when Generate finally enables  
- [ ] S3 strip after explicit navigation + `RefreshReadiness` / longer Blazor wait  
- [ ] Cost Agree + length card after sign-off with hard project activate in UI  
