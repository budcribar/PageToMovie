# Model lifecycle migration checklist

Updated: 2026-08-02

This is the authoritative completion checklist for migrating adaptation operations to the shared validated model lifecycle. Check an item only when its implementation is merged, its offline tests pass, and the model-call inventory reflects the resulting state.

## 1. Catalog-aware offline baseline

- [x] Reject model IDs absent from `models_catalog.json`.
- [x] Require explicit project model selections for project-scoped jobs.
- [x] Configure offline fixtures with enabled catalog entries.
- [x] Isolate process-global catalog reload tests from concurrent readers.
- [x] Pass the complete offline suite with no paid endpoint calls.

**Phase status: complete.**

## 2. Shared structured-operation lifecycle

- [x] Separate transport retry from semantic corrective attempts.
- [x] Define parser, validator, deterministic fallback, attempt, and result contracts.
- [x] Record model, prompt version, behavior versions, input hash, response hash, attempts, and terminal source.
- [x] Support deterministic recorded-response replay.
- [x] Add required-data gates and deterministic manifests for large structured artifacts.
- [x] Test primary success, correction, fallback, cancellation, hashing, and artifact validation.

**Phase status: complete.**

## 3. Stage 1 adaptation

- [x] Require a catalog-selected planning model.
- [x] Treat Fountain and `VISION_META` as one adaptation package.
- [x] Validate non-empty Fountain and scene coverage before accepting Stage 1.
- [x] Persist Stage 1 provenance and validation artifacts.
- [x] Use derivation-safe shared caching for complete non-heuristic packages.
- [x] Move the primary book-to-Fountain request into an `IModelOperation` adapter.
- [x] Move structural repair requests into focused corrective lifecycle attempts.
- [x] Move vague-heading and generic-speaker repairs into versioned operations.
- [x] Move missing/malformed `VISION_META` repair into a versioned operation.
- [x] Remove duplicate converter-local retry/parse/fallback orchestration after parity tests pass.
- [x] Add recorded primary/correction/fallback replay for the complete Stage 1 package.

**Phase status: complete.**

## 4. Cast extraction

- [x] Require a catalog-selected planning model.
- [x] Require versioned schema and non-empty `character_seed_tokens`.
- [x] Persist cast extraction input/output hashes and validation findings.
- [x] Preserve model-selected cast membership; do not invent omitted characters.
- [x] Move the direct `IChatClient.CompleteAsync` request into a cast `IModelOperation`.
- [x] Extract cast JSON parsing into an `IModelResponseParser`.
- [x] Add domain validation for stable keys, membership, descriptions, species, and source references.
- [x] Implement a focused correction request containing exact missing/invalid fields.
- [x] Define an explicit terminal policy for unresolved cast errors without provider switching.
- [x] Migrate visual-literalization and wardrobe-lock model passes or inventory them separately.
- [x] Add recorded primary/correction/failure replay for cast extraction.

**Phase status: complete.**

## 5. Stage 2 planning

- [x] Require a catalog-selected video model.
- [x] Require `stage2_meta` and non-empty scenes before accepting the plan.
- [x] Persist Stage 2 model identity, hashes, and validation findings.
- [x] Preserve immutable source dialogue and pronunciation annotations.
- [x] Inventory every Stage 2 classifier as a distinct operation with schema/version ownership.
- [x] Route every Stage 2 classifier request through `ValidatedModelOperation`.
- [x] Standardize requested-ID coverage validation and focused missing-ID correction.
- [x] Validate cross-references among scenes, clips, cast, dialogue, wardrobe, and continuity.
- [x] Record per-classifier attempts and fallback source in the aggregate Stage 2 manifest.
- [x] Remove classifier-local retry/default implementations after parity tests pass.
- [x] Add recorded aggregate Stage 2 replay covering partial classifier responses.

**Phase status: complete.**

## 6. Multimodal review

- [x] Apply structural validation before saving clip and movie review artifacts.
- [x] Persist review artifact hashes and validation findings.
- [x] Reject silent model/provider switching when the selected model cannot review video.
- [x] Split clip review into observation and judgment result schemas.
- [x] Move clip review's direct vision request into a validated model operation.
- [x] Move movie chunk observation requests into a validated model operation.
- [x] Move movie synthesis requests into a separate validated model operation.
- [x] Preserve evidence/frame identities, uncertainty, and unavailable status in validators.
- [x] Add focused correction for malformed or incomplete review JSON.
- [x] Record selected model, frame hashes, attempts, and terminal source in review manifests.
- [x] Add recorded clip and movie review replay, including unsupported-video and malformed-response cases.

**Phase status: complete.**

## 7. End-to-end offline lifecycle replay

- [x] Add Mary Had a Little Lamb as a small arbitrary-story fixture.
- [x] Replay and validate a representative cast package with deterministic provenance.
- [x] Record a complete Stage 1 primary/correction package for Mary.
- [x] Replay Stage 1 → cast extraction → Stage 2 without network access.
- [x] Replay multimodal observations/judgments using recorded response fixtures.
- [x] Assert stable book, derivation, prompt, response, and output hashes across two runs.
- [x] Assert cache hit behavior makes zero model-client calls on the second run.
- [x] Assert changed prompt/model/temperature/schema invalidates only affected artifacts.
- [x] Produce one aggregate replay manifest linking every operation and artifact.

**Phase status: complete.**

## 8. Close the model-call inventory

- [x] Update every inventory row with owner, operation name, prompt/schema version, and migration status.
- [x] Remove obsolete “Phase 1 baseline” and stale recovery descriptions.
- [x] Confirm no adaptation operation bypasses the shared lifecycle with direct client calls.
- [x] Confirm deterministic namespaces have no model-client, HTTP, or indirect model-operation dependencies.
- [x] Decide and document whether the compile-time boundary is a separate assembly or an equivalent analyzer/test.
- [x] Check all completion items in `MODEL_CALL_INVENTORY.md` only after repository-wide verification.
- [x] Run the complete offline suite and benchmark self-test from a clean commit.
- [x] Record the final commit and verification counts in the reliability document.

**Phase status: complete.**

## Completion gate

The migration is complete only when all eight phase statuses are complete, all boxes above are checked, the working tree is clean, and the pushed commit passes the complete offline suite plus the zero-cost benchmark self-test.
