# Catalog self-test & verification dates (2026-08-05)

## Dates on each model row
| Field | Meaning |
|-------|---------|
| `lastVerifiedAt` | ISO date when the row was last checked for complete required fields |
| `pricingLastReviewedAt` | ISO date when cost fields were **last reviewed** against the vendor (audit review cadence, not git-edit time) |
| `pricingNotes` | Source / policy for cost numbers |

## Self-test API
- `SupportedModelCatalog.ValidateEnabledModels()` → list of errors
- `SupportedModelCatalog.EnsureEnabledModelsComplete()` → throws if any error

## When it runs
- After loading the real catalog from file or embedded resource (not browser soft-empty shell)
- CI / unit tests: `SupportedModelCatalogSelfTest`

Synthetic `TryLoadFromJson` catalogs used in unit tests are **not** auto-validated (so incomplete fixtures still work). Call `ValidateEnabledModels` explicitly when testing the self-test itself.

## Required fields (enabled models)
By capability: tokens + costs (Chat/Vision), duration/refs/prompt + video costs (Video), image costs (Image), audio duration/prompt (Audio), voice prompt (Voice).
