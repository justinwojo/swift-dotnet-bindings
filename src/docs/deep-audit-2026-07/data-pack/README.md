# Data Pack — Quantitative Evidence for Fix Workers

**Purpose**: Machine-oriented evidence extracted **after** the qualitative deep-audit waves.  
**Mode**: Read-only. No production fixes.  
**Date**: 2026-07-16  

Parent audit: [`../synthesis/executive-summary.md`](../synthesis/executive-summary.md) · Backlog: [`../synthesis/work-items-backlog.md`](../synthesis/work-items-backlog.md)

---

## Index

| # | File | What workers get |
|---|------|------------------|
| 00 | [00-skipreason-catalog.md](00-skipreason-catalog.md) | Full SkipReason enum + BindingTests triage (312 rows, Review=1, produce-throw 32) |
| 01 | [01-diagnostic-encyclopedia.md](01-diagnostic-encyclopedia.md) | **98** SWIFTBIND/SB IDs: hard/warn/soft + G1 kill relevance |
| 02 | [02-emit-then-break-inventory.md](02-emit-then-break-inventory.md) | ~51 emission loci classed (produce-throw intentional, SB0006, …) |
| 03 | [03-gates-baselines-ci.md](03-gates-baselines-ci.md) | CI vs theater: only `wrapper_stripped_count` of 5 baselines.json keys enforced |
| 04 | [04-validation-corpus-skip-heatmap.md](04-validation-corpus-skip-heatmap.md) | Multi-lib: 7356 skips / 22.5%; top=UnsupportedSignature 1420 |
| 05 | [05-visibility-nonisolated-evidence.md](05-visibility-nonisolated-evidence.md) | Host **explicitly** rejects `public nonisolated func` from PublicMemberNames |
| 06 | [06-churn-hotspots.md](06-churn-hotspots.md) | Top edit-frequency files since 2025 |
| 07 | [07-cli-ambient-state-inventory.md](07-cli-ambient-state-inventory.md) | 64 CLI options, 7 ambient collectors, MSBuild props |
| 08 | [08-partial-success-kitchen-design.md](08-partial-success-kitchen-design.md) | 12 skip shapes + 2 controls + Scenario A/B soft wrapper |
| 09 | [09-unsupported-signature-taxonomy.md](09-unsupported-signature-taxonomy.md) | US-* sub-buckets; not one epic |
| 10 | [10-netunavailable-missingwrapper.md](10-netunavailable-missingwrapper.md) | NetUnavailable curated types; MissingWrapper two legs |
| 11 | [11-parity-skip-surface-baselines.md](11-parity-skip-surface-baselines.md) | Parity clean vtable; 8 forward known-missing; skip-surface 73 |
| 12 | [12-sb-diagnostic-family.md](12-sb-diagnostic-family.md) | SB0001–6 poison spectrum + default NoWarn |
| 13 | [13-duplicate-signature-taxonomy.md](13-duplicate-signature-taxonomy.md) | DupSignature 450: fixed vs by-design residuals |
| 14 | [14-unsupported-closure-matrix.md](14-unsupported-closure-matrix.md) | Closure Layer1/2; residual vs fixed; validate 600 |
| 15 | [15-unit-test-theater-metrics.md](15-unit-test-theater-metrics.md) | Mega-test Contains% up to 98%; contrast semantic tests |
| 16 | [16-dead-code-todo-inventory.md](16-dead-code-todo-inventory.md) | NotImplemented, dead helpers, second metadata model |
| 99 | [99-wall-assessment.md](99-wall-assessment.md) | **Soft wall declared** — when to stop finding |

---

## Headline numbers for workers

| Fact | Value |
|------|------:|
| BindingTests PublicSurfaceLost | 296 |
| BindingTests ReviewCount | **1** |
| BindingTests produce-throw site tokens | ~32 |
| Validate skip rate | **22.5%** |
| Validate UnsupportedSignature | **1420** |
| Validate MissingWrapperSymbol | **64** |
| Validate NetUnavailableType | **780** |
| Diagnostic IDs documented | **98** |
| baselines.json theater keys | **4 of 5** |
| Mega-test Assert.Contains (SwiftUIBridge tests) | **606** in 10.7k LOC |
| CLI options | **64** |
| Validation libraries in json | **66** |

---

## How to use with fix workers

1. Pick backlog Tier 0 item  
2. Open **matching data-pack** for file:line + counts + sub-buckets  
3. Prefer **US-*** / G1-*** IDs over free-form “fix skips”  
4. Do **not** re-audit ABI cores (risk 2/5, 0 new P0) unless data-pack cites new reachability  
