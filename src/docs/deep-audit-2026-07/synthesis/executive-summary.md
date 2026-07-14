# Executive Summary — Deep Audit 2026-07

**Program complete**: 2026-07-15 → 2026-07-16  
**Mode**: Read-only analysis; no production code changes  
**Root**: [`src/docs/deep-audit-2026-07/`](../)

---

## What we did

Multi-wave, multi-lens audit of the full tool surface (~1,800 in-scope files / ~705k LOC including tests):

| Wave | Focus | Outcome |
|------|--------|---------|
| 0 | Map, prior art, file ledger | Architecture + don’t-re-chase index |
| 1 | P/Invoke, layout/VWT, ARC | Risk ~2/5; 0 new live P0 |
| 2 | Reverse-dispatch layout/receivers/fill | Layout SSOT sound; CT edge **fixed** (docs stale) |
| 3 | CSM / TypeDB | Crash-class closed; undercount residual |
| 4 + **G1** | Closures, async, **graceful degradation** | **Day-1 product risk 3/5** |
| 5 | Parser/demangler | Visibility dual-oracle **3/5** |
| M2 | SDK/packaging | Confirms wrapper-required cliff |
| T | Test honesty | Runtime good; unit/baselines theater |
| 6 | Runtime line-complete | ~2/5 mature |
| 9 | ObjC / SwiftUI / Apple / analyzers | Capability-limited, not crash-prone |
| 10 | Simplification + dual oracles | 40 L4 rows + constraints drift |
| 11 | This synthesis + backlog | Owner promotion ready |

Lenses applied throughout: **correctness**, **meaningful gates**, **graceful degradation**, **simplification without capability loss**, **AI maintainability**.

---

## Bottom line for the owner

### 1. The dangerous ABI/ownership cores are healthy

P/Invoke contracts, struct/VWT projection, ARC/SafeHandle design, reverse-dispatch **layout SSOT**, CSM main path, TypeDB/projection parity, closures, async ownership, and the managed runtime all scored **~risk 2/5** with **no new emission-live P0 double-free / wrong-CallConv / slot-shift** confirmed in this pass.

That **validates** the roadmap “input-poor, not bug-poor” thesis **for crash-class generator bugs**. Further broad ABI re-audits will rediscover latents and dual-oracle hygiene, not a gold rush of P0s.

### 2. The highest-value gap is product-shaped: day-1 partial success

You asked for Objective-Sharpie-like “still usable if incomplete.”  

**Generator admission already does a lot of that** (skip + report).  
**Packaging and public-surface policy still don’t:**

| Scenario | Today |
|----------|--------|
| Unsupported members, wrapper OK | Compile-clean partial ✅ |
| Wrapper compile fails | **Default SWIFTBIND051 → package dead** ❌ |
| Mixed ObjC systemic fail | **Total abort** ❌ |
| Reverse-dispatch suppressed | Compiles but **throw/silent dead API** ❌ |

See [`graceful-degradation-map.md`](graceful-degradation-map.md) and Track G1/M2.

### 3. Second-highest: honesty layers (visibility + tests)

- **Parser visibility**: `public nonisolated` and protocol-req / subscript gaps can **drop** public API (A8).  
- **Tests**: BindingTests skip culture is honest; **mega unit tests** and **unenforced baselines.json keys** are theater (T).  

### 4. Simplification is real and safe-to-queue

Not “delete features” — dual-oracle consolidations, dead code delete, TypeSkip/vtable/cdecl phase sharing. Full ranked list: [`simplification-opportunities.md`](simplification-opportunities.md).

### 5. Docs lag code in several places

constraints.md legacy CT, roadmap F8, several CSM rows, inout writeback — **code fixed, docs still warn**. Fixing docs prevents agent thrash.

---

## Ranked action (from backlog)

**Immediate (if you want wins without large risk):**

1. Document `SwiftWrapperRequired=false` exploration ritual  
2. Patch constraints.md CT incomplete paragraph  
3. PartialSuccessKitchen fixture design  
4. Investigate/fix `public nonisolated` visibility drop  
5. baselines.json: enforce or delete dead keys  

**Strategic (owner decisions):**

1. Partial package default/policy when wrapper fails  
2. Produce-throw reverse surface omit/hide policy  
3. Mixed ObjC Swift-only continue opt-in  

Full table: [`work-items-backlog.md`](work-items-backlog.md).

---

## What not to do next

- Another undirected full-generator “find all bugs” audit expecting many P0s  
- Merge the parallel async emitters  
- Soften integrity gates (108, TN2435, false HasWrapper)  
- Re-audit all BindingAudit libraries from scratch  

---

## Artifact map

| Path | Role |
|------|------|
| [`../00-ORCHESTRATION.md`](../00-ORCHESTRATION.md) | Waves, concurrency, decisions |
| [`../00-methodology.md`](../00-methodology.md) | Lenses L1–L5, severity |
| [`../00-codebase-map.md`](../00-codebase-map.md) | Architecture map |
| [`../00-prior-art-index.md`](../00-prior-art-index.md) | Don’t re-chase |
| [`../00-file-coverage-ledger.md`](../00-file-coverage-ledger.md) | ~1799 files inventory |
| [`../tracks/`](../tracks/) | Per-track deep reports |
| [`work-items-backlog.md`](work-items-backlog.md) | Ranked implement queue |
| [`graceful-degradation-map.md`](graceful-degradation-map.md) | Day-1 degrade map |
| [`simplification-opportunities.md`](simplification-opportunities.md) | L4 catalog |
| [`00-mid-audit-executive-summary.md`](00-mid-audit-executive-summary.md) | Mid-point snapshot |
| [`open-questions.md`](open-questions.md) | Owner decisions still open |
| [`refuted-claims.md`](refuted-claims.md) | Don’t re-chase this pass |

---

## Success metrics recap

| Metric | Result |
|--------|--------|
| Correctness map of cores | ✅ Mature (~2/5) |
| Meaningful gates | ⚠️ Runtime good; unit/baselines weak |
| Graceful degradation | ⚠️ Admission strong; package policy weak (3/5 day-1) |
| Simplification inventory | ✅ 40 ranked opportunities |
| Complete map | ✅ Waves through 10 + ledger seed |
