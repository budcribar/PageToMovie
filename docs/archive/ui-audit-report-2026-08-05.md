# UI audit report (fakes mode)

**Passes 1–4:** prior · **Pass 5 (continue matrix):** 2026-08-05T17:26Z

**Base:** `http://127.0.0.1:5088` · `useFakes=true`

## Product decisions

| Topic | Decision |
|-------|----------|
| **`/demo`** | **Public — terms not required** |
| Pass 5 observation | Terms modal **still** on `/demo` → **confirmed bug** |

## Pass 5 results (17 checks, 6 failures)

| ID | Result | Detail |
|----|--------|--------|
| T3 demo no terms modal | **FAIL** | Modal on public `/demo` |
| T3 demo has content | PASS | body has content under modal |
| T4 studio Home shows terms | PASS | |
| N1 `/film` | **FAIL** | blank main |
| N1 `/billing` | **FAIL** | blank main |
| N1 unknown route | **FAIL** | blank main (NotFound not shown) |
| T1 API create without terms | **FAIL** | HTTP 200 allowed |
| accept terms | PASS | |
| S2b import fountain | PASS | file set; prepare/convert may still be async |
| S2b length input after import | **FAIL** | still missing |
| screenplay / characters / scenes / review load | PASS | |
| S9 Delete under Manage | PASS | Delete button visible |
| N2 strip Film | PASS | `javascript:void(0)` + `is-disabled` without shots |

Artifacts: `artifacts/ui-audit/continue-tests-report.md`, `cont-*.png`

## Open P0 (browser-confirmed)

1. **Demo requires terms incorrectly**  
2. **Unknown routes blank** (Router NotFound not effective)  
3. **API project create without terms**  
4. **Cost film-length input missing** even after project + import attempt  

## Verified OK

- Studio terms gate + accept + reload persistence (pass 4)  
- Empty create name disabled; UI create works  
- Manage → Delete control present  
- Strip Film disabled when not ready  
