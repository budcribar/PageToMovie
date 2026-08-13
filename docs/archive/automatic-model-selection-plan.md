# Automatic Model Selection, Managed Billing & Simulation Plan

> **Status:** Backlog / Future Architecture Design  
> **Last Updated:** 2026-07-28

This plan details the future migration of Film Studio from manual per-stage model pickers to an **Automatic Model Selection** system with managed billing, token telemetry, profit margins, spending caps, and a deterministic simulation test suite.

---

## Architecture & Strategy

```
┌────────────────────────────────────────────────────────┐
│             [ User UI: Quality Slider ]                │
└───────────────────────────┬────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────┐
│     [ Pre-run Solvency Check & Cost Estimator ]        │
│   (Checks: Est. Cost vs. Balance & Auto-Reload Rules)  │
└───────────────────────────┬────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────┐
│         [ Tier Policy Engine (QualityTierPolicy) ]     │
└───────┬───────────────┬───────────────┬────────────────┘
        │               │               │
        ▼               ▼               ▼
  [ Script/Plan ] [ Characters ] [ Video Gen ]  [ Clip Review ]
  (Budget LLM)   (Flux Schnell)  (Hunyuan/Grok)  (Gemini Pro)
        │               │               │              │
        └───────────────┼───────────────┴──────────────┘
                        │
                        ▼
┌────────────────────────────────────────────────────────┐
│      [ Token & Usage Telemetry + Billing Engine ]      │
│  (Emits tokens/secs -> Multiplies Margin -> Debits $)  │
└───────────────────────────┬────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────┐
│        [ Test & Simulation Harness (Fakes/LoadSim) ]   │
│   (Deterministic Token Mocks + Fake Stripe Webhooks)   │
└────────────────────────────────────────────────────────┘
```

## Key Design Pillars

1. **User Experience**:
   - Replace complex raw model dropdowns with a single **Cost vs. Quality Slider** (Presets: *Budget / Best Value*, *Balanced*, *Cinema Quality*, *Master / Production Standard*).
   - Provide upfront estimated total cost ($) for an entire film *before* production starts.

2. **Managed API Billing & Profit Margin**:
   - App manages provider API keys centrally.
   - User pays list rates + platform margin (`Billed USD = Raw API Cost × MarginMultiplier`, e.g., 1.25x for 20% gross margin).

3. **Credit Card Payments & Auto-Reload**:
   - Integrated Stripe payment processing (PaymentIntents & Customer Portal).
   - Support manual credit packages ($10, $25, $50, $100) and **Auto-Reload** (*"Refill $20 when balance drops below $5"*).

4. **Spending Caps & Safeguards**:
   - **Daily/Monthly Spending Caps**: Enforce user-defined maximum spend limits.
   - **Pre-Run Solvency Check**: Verifies whether current credit balance + auto-reload capacity is sufficient to finish the film run *before* burning API spend on half a movie.

5. **Deterministic Simulation & Verification Harness**:
   - Offline testing framework in `PageToMovie.Fakes` and `PageToMovie.LoadSim` to simulate generation costs, token counts, Stripe webhooks, auto-reloads, and solvency blocks without spending real API dollars.

---

## Proposed System Components

### 1. Payment Gateway & Auto-Reload Service

- `PaymentBillingService.cs`:
  ```csharp
  public record UserBillingSettingsDto(
      string UserId,
      double CurrentBalanceUsd,
      bool AutoReloadEnabled,
      double AutoReloadThresholdUsd,  // e.g. $5.00
      double AutoReloadAmountUsd,     // e.g. $20.00
      double DailySpendCapUsd,        // e.g. $50.00
      double MonthlySpendCapUsd,      // e.g. $200.00
      double CurrentMonthSpentUsd
  );
  ```

---

### 2. Telemetry & Token Usage Logging

- `TokenUsageTracker.cs`: Emitted by all API clients (Grok, Gemini, Anthropic, Fal):
  ```csharp
  public record ApiUsageEvent(
      string EventId,
      string UserId,
      string ProjectId,
      string Stage,               // "screenplay", "shotplan", "portrait", "video_gen", "clip_review"
      string ProviderId,          // "grok", "gemini", "anthropic", "fal"
      string ModelId,             // "grok-4.5", "veo-3.1", "fal-ai/flux/schnell"
      int? PromptTokens,
      int? CompletionTokens,
      int? ImageCount,
      double? VideoSeconds,
      string? Resolution,
      double RawCostUsd,          // Upstream API cost
      double BilledCostUsd,       // RawCostUsd * MarginMultiplier
      DateTime Timestamp
  );
  ```

---

### 3. Pre-Production Upfront Cost Estimator & Solvency Checker

- `CostEstimationCalculator.cs`:
  - **Script & Planning Tokens**: `(Story Character Count / 4) × multiplier`.
  - **Character Portraits**: `Estimated Character Count × 4 variations`.
  - **Video Generation**: `Estimated Shot Count × Avg Clip Duration (5s) × Model Rate ($/sec)`.
  - **Clip Review**: `Estimated Shot Count × Review Token Rate`.
- **Pre-Run Solvency Gate**:
  ```csharp
  public record SolvencyCheckResult(
      bool CanProceed,
      double EstimatedCostUsd,
      double AvailableBalanceUsd,
      bool RequiresAutoReload,
      string UserFacingMessage
  );
  ```

---

### 4. Deterministic Simulation & Testing Suite

- `FakeProviderBillingHarness.cs` in `PageToMovie.Fakes`:
  - `ConfigureTokenUsage(promptTokens, completionTokens)`
  - `ConfigureVideoGeneration(seconds, resolution, rawCostUsd)`
- `FakeStripeServer.cs` in `PageToMovie.Fakes`:
  - Mocks Stripe payment gateway events, card declines, and webhooks.

---

## Phased Implementation Roadmap

1. **Phase 1: Token & Usage Telemetry Logging + Profit Margin**
2. **Phase 2: Upfront Pre-Run Estimator & Movie Solvency Checker**
3. **Phase 3: Stripe Payment Integration & Auto-Reload**
4. **Phase 4: Quality Tier Policy & UI Slider**
5. **Phase 5: Comprehensive Billing & Load Simulation Suite**

---

## Verification & Financial Safety Plan

1. **Solvency Boundary Test**: User has $1.25 balance, estimate is $1.25 — verify job runs to completion.
2. **Mid-Job Auto-Reload Decline**: Auto-reload fails during Scene 5 — pipeline pauses gracefully without corrupting project state.
3. **Daily Spend Cap Gate**: Daily limit ($10.00) reached — subsequent scene calls blocked before calling provider APIs.
4. **Profit Margin Reconciliation**: Run 100 simulated film productions; verify `Sum(BilledUSD) == Sum(RawAPIUSD) * 1.25` down to $0.00.
