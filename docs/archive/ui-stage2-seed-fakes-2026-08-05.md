# Stage2 under fakes — seed notes (UITestingBranch)

**Date:** 2026-08-05  
**Result:** Stage2 **completes** under fakes once project models are set.

## Failure mode

```
Shot plan / Stage 2: no model selected.
Open Settings → Studio coverage and choose a model for this job.
```

`GET /api/projects/{id}/config` returned `"config":{}`.  
`ProjectModelSelection.RequireVideo` / `RequirePlanning` throw if unset.

Earlier “Waiting for resource lock…” was a transient queue message; the durable failure was missing models (and once an attempt before sign-off finished).

## Success

After setting config on `local/Stage2Seed`:

| Field | Value |
|-------|--------|
| stage2_ready | **true** |
| stage2_scenes | **5** |
| stage2_clips | **12** |
| blueprint | `blueprint.clips.grok.json` |

## Implication for item 1 vs 2

- **Item 1 done:** shot plan exists under fakes → Film strip *can* unlock (`CanScenes` = stage2 ready + clips).
- **Item 2 next:** Generate spend still gated on `Cast.ReadyForShots` (voice + locked image) in Scenes UI / API.
