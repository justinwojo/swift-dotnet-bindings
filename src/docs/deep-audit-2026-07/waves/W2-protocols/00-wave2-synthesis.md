# Wave 2 Synthesis — Protocols / Reverse Dispatch

**Date**: 2026-07-15  
**Tracks**: [A5a](../../tracks/Track-A5a_VtableLayout-EveryProtocol.md), [A5b](../../tracks/Track-A5b_ProtocolProxy-Receivers.md), [A5c](../../tracks/Track-A5c_StaticInit-WitnessDispatch.md)

---

## Bottom line

| Track | Risk | New live P0 | Headline |
|-------|------|-------------|----------|
| **A5a** VtableLayout | **2/5** | **0** | SSOT sound; residual dual oracles on width/hand-enumerators |
| **A5b** Receivers | **2/5** | **0** | AF05 CT (incl. legacy) + orphan raw-key dedup **closed**; stale docs claim otherwise |
| **A5c** StaticInit/Witness | **2/5** | **0** | Layout independent of skip sets; F8 as written **refuted**; null fillability is the residual product risk |

**Reverse-dispatch layout corruption (slot shift) is largely a solved class** after `VtableLayoutBuilder`. Remaining protocol risk is **product-level compile-but-dead / null reverse slots** (honest skip vs force-unwrap crash if reverse-hit), not silent field renumbering.

---

## Cross-track themes

### 1. Layout SSOT held up (L1 good)

- Membership / pre-skip / skip-but-consume / raw-key collapse / async split unit-pinned  
- Layout **not** gated on projected C# keys  
- Swift `{P}_vtable` and C# Swift/Local vtable mirrors walk the same model  
- Fillability walks use `MethodSlotIndexByKey` and leave unfillable Included slots **null** (intentional)

### 2. Important prior-art / docs corrections

| Claim in docs/roadmap | Wave 2 reality |
|----------------------|----------------|
| Legacy blocking async receiver missing `default(CancellationToken)` | **Fixed** (`Receivers.cs` + BindingTests) — constraints.md **stale** |
| F8: Vtables consult only `_closureSkippedMethodKeys` | **Refuted** as written post-VtableLayout |
| Orphan receivers for collapsed overloads | **Fixed** (`emittedRawKeys`) |
| BindingAudit EveryProtocol compile-but-dead | Still valid **product** class when layout-Included + C# unfillable → null + Swift force-unwrap |

### 3. Residual findings worth backlog (not fire-drill)

| ID theme | Severity | Notes |
|----------|----------|-------|
| `MethodEmitsVtableField` drifted vs `ClassifyMethod` | P2 | Nested @objc existential exclusion missing; stale “SSOT” comments |
| Width dual oracle (Swift field emit vs GetWidth) | P2 | Low reachability; debug/empty-tuple params |
| Hand-enumerators parallel to layout | L4 | Match today; edit hazard |
| Dead `Helpers.GetMethodKey` | L4 | Delete, don’t reuse |
| Cross-module empty skip sets | P2 candidate | Multi-nupkg TypeDB skew |
| Null reverse slot → solo crash | L3/already-known | Prefer honest skip or safe stub over force-unwrap |

### 4. Graceful degradation (L3) angle

Wave 2 reinforces G1 themes:

- **Good:** unfillable slots stay null rather than inventing wrong receivers (post raw-key fix)  
- **Weak:** Swift side may still **force-unwrap** a null reverse entry → crash if reverse-dispatch hits that requirement  
- **Product goal:** for new libraries, prefer **omit reverse conformance / suppress proxy honestly** over “layout present + null + crash on use”

---

## Wave 3 readiness

**Go** — Generics/CSM (A6) + TypeDB/projection (M3). Many roadmap medium rows are CSM filter/sugar issues — agents must tag **already-known** aggressively and hunt only for *new* dual-path or emit-then-break shapes.
