# Wave 4 + G1 Synthesis — Closures, Async, Graceful Degradation

**Date**: 2026-07-15  
**Tracks**: [A4](../../tracks/Track-A4_Closures-Reabstraction.md), [A7](../../tracks/Track-A7_Async-Throws-Error-Carrier.md), [G1](../../tracks/Track-G1_Graceful-Degradation.md), [graceful-degradation-map](../../synthesis/graceful-degradation-map.md)

---

## Bottom line

| Track | Risk | New live P0 | Headline |
|-------|------|-------------|----------|
| **A4** Closures | **2/5** | **0** | Multi-bridge stack hardened; Layer1/2 + return SSOT hold |
| **A7** Async/throws | **2/5** | **0** | Ownership coherent; **do not merge** emitters; CSM cancel residual |
| **G1** Graceful degrade | **Day-1: 3/5** | n/a (product) | **Admission mature; packaging defaults + compile-but-dead decide day-1** |

**This is the first wave where product risk rises above “hygiene 2/5”.** G1 is the owner-priority result of the audit so far.

---

## G1 — Day-1 new library experience

| Scenario | Likely outcome today |
|----------|----------------------|
| Pure Swift, unsupported members only, wrapper compiles | **Compile-clean partial** + skip report |
| Wrapper compile fails | **Hard Error SWIFTBIND051** (default) → whole package dead |
| Mixed ObjC systemic parse fail | **Total abort** — no Swift-only degrade |
| Protocol reverse-dispatch heavy | Compiles; reverse **throws** or silent dead |

### What already works (do not break)

- `MemberValidationPipeline` + honest `SkipReason` / SkipTriage / workarounds  
- Emission-time drops vs pure emit-then-strip  
- TypeSkipPrePass, SWIFTBIND108 integrity, in-band contract gates  
- CSM `RoutedElsewhere` (engine reject > swiftc fail)  
- Suppressed-proxy **reporting** (surface still compile-but-dead)

### Top degrade opportunities (ranked)

| ID | Opportunity | Notes |
|----|-------------|--------|
| **G1-001** | Partial package mode when wrapper fails | Keep integrity hard; optional degrade path for day-1 |
| **G1-003** | Omit/hide produce-throw reverse surface | Already-known BA/BSA; better than public NSE |
| **G1-002** | Opt-in Swift-only continue on mixed ObjC fail | Mixed-degraded metadata |
| **G1-004** | Product scenario gate | Intentional unsupported shapes → exit 0 + clean compile |
| **G1-005/006** | Drive MissingWrapperSymbol / strip → 0; share TypeSkip predicates | Integrity + L4 |

Integrity that **must stay hard**: plan/emit symbol mismatch, false wrapper metadata, TN2435 packaging lies, RuntimeContract fraud.

Full map: `synthesis/graceful-degradation-map.md`.

---

## A4 / A7 (supporting)

- Closures: optional-as-escaping, GCHandle box transfer, `.All()` Layer2, `BuildCallbackReturnStatement` SSOT — clean  
- Async: shared Cleanup/result planner; merge rejected; candidates = CSM parent cancel maps CancellationError→exception only; L4 exact-duplicate extracts only  
- Already-known retained: Optional\<ObjC value\> closure args, NCB escaping-inner leak, UnsupportedClosure remainder  

---

## Implications for remaining waves

| Wave | Adjust |
|------|--------|
| W5 Parser | Focus on mis-admit that causes emit-then-break or wrong skip |
| W6 Runtime | Ownership already strong; light pass |
| W7 Packaging | **Elevate** — G1-001/SDK SWIFTBIND051 is packaging/product policy |
| W8 Tests | Add product scenario: “unsupported shapes → clean partial” |
| W10 S1 | Share TypeSkip predicates; dual-oracle closure PE/Foreign |

---

## Running product backlog seeds (G1-led)

1. Design **partial-success packaging policy** (owner decision) for wrapper fail  
2. Compile-but-dead reverse APIs → omit/EditorBrowsable/report  
3. Mixed ObjC fail → optional Swift-only continue  
4. BindingTests (or gate) for intentional-unsupported → clean package  
5. MissingWrapperSymbol ratchet to zero  
