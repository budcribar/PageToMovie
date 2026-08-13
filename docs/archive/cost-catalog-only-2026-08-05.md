# Cost rates — catalog only (2026-08-05)

## Policy
- All USD rates for estimates/ledger math come from `models_catalog.json`.
- Engine has **no** `Fallback*` cost constants.
- Missing required price fields → `InvalidOperationException` (do not invent numbers).
- Each model should carry `pricingNotes` citing where rates were taken (URL / date / policy).

## Fields
| Field | Use |
|-------|-----|
| input/outputCostPerMillionTokens | Chat/Vision |
| videoCostPerSecondByResolution | Duration-priced video |
| videoBaseCostByResolution | Flat $/generation (Fal) |
| videoReferenceImageCost | Per ref on video (0 if not separately billed) |
| videoExtendCostPerSecond | Required if supportsVideoContinue |
| imageCostPerImage | Image |
| costPerMinuteUsd / costPerThousandCharsUsd / costPerCloneUsd | Voice/LipSync |

## Grok (2026-08)
- xAI publishes generation pricing, not separate ref/extend line items.
- Catalog: `videoReferenceImageCost: 0`, `videoExtendCostPerSecond: 0.07` (720p planning rate) + `pricingNotes`.
