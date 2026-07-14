# Wave 3 Synthesis — Generics / CSM / TypeDatabase

**Date**: 2026-07-15  
**Tracks**: [A6](../../tracks/Track-A6_CSM-Generics-PAT.md), [M3](../../tracks/Track-M3_TypeDatabase-Projection-Parity.md)

---

## Bottom line

| Track | Risk | New live P0 | Headline |
|-------|------|-------------|----------|
| **A6** CSM/generics | **2/5** | **0** | Crash-class CSM closed + fixture-pinned; residual undercount filters |
| **M3** TypeDB/projection | **2/5** | **0** | F15 shared predicate; visitor exhaustiveness; seed-drop parity held |

**CSM is not a silent-wrong-ABI minefield today.** Residual risk is **undercount** (legal members skipped) and **product caps** (multi-PAT, multi-generic parent), which is also an **L3 graceful-degrade** story (skip cleanly vs emit-then-swiftc-fail).

---

## Roadmap stale rows (code fixed; docs lag)

A6 re-verified fixed with tests:

- Class `returnsGenericParam` carrier wrap  
- MethodGenericBridge AllocHGlobal antipattern  
- Self substitution in CSM signatures  
- Primary `T:P&Q` composition intersection  
- Empty `_` external labels on CSM call sites  

**Still open / partial (already-known or candidate):**

| Item | Status |
|------|--------|
| Direct SameType sugar (`Data?` vs `Optional<…>`) | **Partial** — DA-W3-A6-001 candidate |
| Method-level RawGenericSig composition residual | Candidate |
| Multi-PAT boxing | Already-known intentional fail-closed |
| MusicKit multi-generic sectioned parent | Already-known low-pri |

---

## M3 residual

- Intentional dual: Handle optional ObjC vs class/reference optional oracles  
- ProtocolList AnyTypeFallback (Finding 21)  
- Candidates: ObjCRooted reverse setter passthrough; EnumHandler prefix-only ObjC check  
- L4: projection-only marshaler stays post-1.0  

---

## L3 takeaway for G1

Prefer **engine-side reject** (csmConformerRejections, SkipReason) over **wrapper swiftc fail** as the consumer experience for hard CSM shapes. Inventory which residual CSM paths still only fail at wrapper compile (A6 report).

---

## Pattern W1–W3

Hardened cores keep scoring **risk ~2/5, 0 new emission-live P0**. Audit value is **stale-doc correction**, **dual-oracle hygiene**, and **fixture targets**. Highest remaining owner leverage shifts to **G1 graceful degradation**, **test honesty**, and **simplification**.
