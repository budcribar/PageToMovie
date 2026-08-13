# Admin models catalog UI (2026-08-05)

Route: `/admin/models-catalog` (Admin → Models Catalog)

## Actions
| Action | Behavior |
|--------|----------|
| **Add** | Form for id, capability, provider, capability-specific limits/costs |
| **Edit** | Same form; apply to table |
| **Review** | Stamps `lastVerifiedAt` + `pricingLastReviewedAt` to today (UTC yyyy-MM-dd); prompts for pricingNotes if empty |
| **Delete** | Confirm, remove from table |
| **Validate all** | `POST /api/admin/models-catalog/validate` — runs `ValidateEnabledModels` on draft JSON without saving |
| **Save & hot-apply** | Pre-validates, then `PUT /api/admin/models-catalog` → `SaveCatalogJson` + reload self-test |

## Review dates
- **lastVerifiedAt** — row completeness last reviewed
- **pricingLastReviewedAt** — cost figures last reviewed against vendor

Save will fail if enabled models are missing required fields (server self-test).
