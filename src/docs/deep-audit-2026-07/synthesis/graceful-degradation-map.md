# Graceful Degradation Map (G1 draft)

**Status**: Draft after Track G1 (Wave 7); finalize in Wave 11 synthesis.  
**Canonical deep report**: [`../tracks/Track-G1_Graceful-Degradation.md`](../tracks/Track-G1_Graceful-Degradation.md)  
**Product goal**: Drop arbitrary xcframework → **compile-clean usable binding** missing unsupported surface + honest report — not CS*/swiftc total death.  
**Sharpie analogue**: skip report + clean compile (not freehand ApiDefinitions).

---

## Headline

**Generator-side skip admission is strong; package-level defaults and compile-but-dead still gate day-1.**  
Risk of current day-1 new-library experience: **3 / 5**.

---

## Integrity vs usability (one screen)

| Keep hard-fail | Prefer skip / continue |
|----------------|------------------------|
| SWIFTBIND108 plan↔emit symbol mismatch | Unsupported member/type shapes |
| False wrapper / TN2435 / pack slice lies | PAT, SwiftUI/Combine, indeterminate layout |
| RuntimeContract fraud | Module-internal / Pattern2 drops |
| Hook disconnect (062–065) | ObjC *member* drops with reason |
| Explicit arch contract (056) | Auto-dep unresolved (080 warn) |
| Primary input missing (no ABI/module) | Bridge slice fail (052) |

| Contested (policy) | Today |
|--------------------|--------|
| Wrapper compile fail | Default **Error** (`SwiftWrapperRequired=true` → 051) |
| Mixed ObjC systemic fail | **Abort entire** generation |
| Produce-throw public API | Compiles; throws at use |

---

## Admission stack (flow)

```
Input resolve ──strict-inputs?──► hard / soft degrade
       │
TypeSkipPrePass + SilentTombstoneRegistrar
       │
HandleBaseDecl type skips (SPI, View, supplement, …)
       │
MemberValidationPipeline (methods/properties)
  ├─ skip → ReportCollector + // Unsupported:
  └─ emit → handlers / wrappers
       │
ProtocolProxyEmissionPolicy
  ├─ Emit proxy
  └─ Suppress → produce-throw / consume-degrade / receiver-failfast + report
       │
WrapperSymbolContractGate (in-band rollback)
       │
SwiftWrapperPostProcessor (strip residual)
       │
Wrapper compile ──SDK──► 050 warn → 051 if required
       │
StrippedSymbolCSharpReconciler (co-gate C#)
       │
WrapperSymbolIntegrityGate ──fail──► exit 1 (108)
       │
binding-report.json ← SkipTriage (post-projection)
```

**Full table**: Track G1 §3 (A1–A25).

---

## Failure-mode cheat sheet

| Mode | Continues? | Consumer signal |
|------|------------|-----------------|
| Member/type skip | Yes | Report row, SWIFTBIND060/061 |
| Wrapper strip + co-gate | Yes | MissingWrapperSymbol (Review) |
| Wrapper give-up | Managed may exist | 050; **051 Error** default |
| SWIFTBIND108 | No | Generator exit 1 |
| Mixed ObjC parse fail | No | Exit; no Swift-only |
| Compile-but-dead | Yes | Runtime throw / silent; KnownLimitation |
| CS* from leak | No | Emit-then-break defect |

---

## Emit-then-break residual (watchlist)

| Priority | Residual | Mitigation today |
|----------|----------|------------------|
| High product | Produce-throw / reverse dead | SuppressedProxyReporting; prefer omit later |
| High product | Wrapper-required package kill | Soft flag exists; default true |
| Medium | TypeSkip dual-oracle drift | Mirror contract + tests |
| Medium | MissingWrapperSymbol growth | Integrity + strip tripwire |
| Low / positive | CSM undercount | Engine reject (good L3 form) |

Historically large emit-then-strip classes (proxy co-gater, Pattern2, parent-internal async/closure) are **emission-admitted**.

---

## Reporting = Sharpie analogue?

| Ready | Not ready |
|-------|-----------|
| SkipReason + disposition + ReviewItems | Consumer edit/allowlist re-gen |
| Workarounds per reason | ReviewCount=0 hides KnownLimitation dead API |
| Manifest projection post co-gate | Day-1 ritual docs |
| 060/061 path to report | Partial nupkg when wrapper dies |

---

## Ranked opportunities (owner-gated)

| Rank | ID | One-liner | Risk if wrong |
|------|-----|-----------|---------------|
| 1 | G1-001 | Soft partial package when wrapper fails | DllNotFound on wrapper APIs — need loud UX |
| 2 | G1-003 | Omit/hide produce-throw reverse surface | Layout/slot parity if over-omitted |
| 3 | G1-002 | Opt-in Swift-only on mixed ObjC fail | Must not claim Mixed falsely |
| 4 | G1-004 | Product scenario gate (unsupported → clean) | Fixture cost only |
| 5 | G1-005/006 | Shared TypeSkip predicates; strip→0 corpus | Over-skip if predicates too broad |

Details: Track G1 §9.

---

## What works well (keep)

- MemberValidationPipeline + ValidationRuleSet SSOT  
- TypeSkipPrePass / ancestor skip  
- Planning-time ConstrainedExtensionWrapper / GenericEnumCaseConstructor  
- ParentModuleInternalNoFallback  
- Contract gate + SWIFTBIND108 integrity  
- SkipTriage + workarounds + ObjC fold-in  
- SuppressedProxyReporting classification  
- CSM RoutedElsewhere for crashy open generics  

---

## Day-1 matrix (condensed)

| Input | Result |
|-------|--------|
| Pure Swift, skips only, wrapper OK | **Success path** |
| Wrapper fail, default SDK | **Hard fail 051** |
| Mixed ObjC systemic fail | **Hard abort** |
| Protocol reverse heavy | **Compile OK, reverse dead** |

---

## Cross-links

- Methodology L3: `../00-methodology.md`  
- Orchestration G1: `../00-ORCHESTRATION.md`  
- Codebase seed: `../00-codebase-map.md` §5  
- W2 L3 reverse: `../waves/W2-protocols/00-wave2-synthesis.md`  
- W3 L3 CSM: `../waves/W3-generics/00-wave3-synthesis.md`  
- Prior art BA-SUM / BSA-05: `../00-prior-art-index.md`  

**Finalize W11**: re-score day-1 risk after any policy decisions on 001–003; fold M2/B1 packaging notes.
