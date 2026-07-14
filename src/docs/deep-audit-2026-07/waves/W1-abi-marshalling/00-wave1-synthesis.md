# Wave 1 Synthesis — ABI / Marshalling Core

**Date**: 2026-07-15  
**Tracks**: [A1](../../tracks/Track-A1_PInvoke-ABI-Contract.md), [A2](../../tracks/Track-A2_Struct-Layout-VWT.md), [A3](../../tracks/Track-A3_ARC-Ownership-Lifetime.md)

---

## Bottom line

| Track | Risk | New emission-live P0 | Headline |
|-------|------|----------------------|----------|
| **A1** P/Invoke | **2/5** | **0** | ≥20 BindingTests pairs MATCH; residual = dual-oracle hygiene |
| **A2** Layout/VWT | **Medium** | **0** | Core frozen/resilient/EI/TypeLowering hardened; strongest new lead = Optional unwrap on float/bool flags |
| **A3** ARC/lifetime | **Medium** | **0** | Ownership map coherent; residual intentional leaks + finalizer edges + known Mono |

Wave 1 supports the roadmap thesis for this layer: **the ABI/ownership core is production-hardened**, not a minefield of undiscovered P0s. Value delivered is **ownership maps**, **dual-oracle inventory**, **taxonomy × coverage**, and a short candidate list for fixtures — not a fix sprint.

---

## Cross-track themes

### 1. Dual oracles dominate residual risk (L4 / L5)

| Dual path | Tracks | Severity class |
|-----------|--------|----------------|
| Enum case `resultPtr`-last outside `CdeclSignatureContract` | A1 | P2 hazard (consistent today) |
| GSF `EmitGenericStaticDispatchMethod` hand-rolled phases | A1 | P2 |
| `CdeclParamMapper` vs `PInvokeEmitter` classifiers | A1 | By design; document/consolidate |
| CGFloat/Optional spare-bit domain split | A2 | P2 dual-oracle |
| `HasFloatFields`/`HasBoolFields` no Optional unwrap | A2 | P2 candidate — **best new fixture target** |
| Projected key vs vtable slot (deferred to Wave 2) | — | Still highest reverse-dispatch risk |

### 2. Defense-in-depth is real (L1 good news)

A1 documented: `SelectCallingConvention`, `WrapperSymbolContractGate`, `AbiContractChecker` CC-001…004, `CdeclSignatureContract` phase order, cdecl uses plain `IntPtr` for self/error/sret (not Swift* under Cdecl).

A2: fail-closed layout skips, EI decline for Optional\<Bool\> frozen, eightbyte mixed float decline, cdecl frozen `inout` writeback **runtime-green** (`TestIncrementPoint`) — roadmap text partially stale.

A3: Design B2, PayloadConstructionSemantics, finalizer trampolines for Mono `!ji->async`, issue #40 UnknownObject retain for async self.

### 3. Already-known correctly not re-filed as new P0s

Mono issues 1–4 + SafeHandle async lifetime; mixed-indirect generic tuples; named String tuple residual; AF13 write-backs; NestedClosureBridge intentional escaping-inner leak.

### 4. Best fixture targets from Wave 1 (for later work-items)

| ID | Fixture | Why |
|----|---------|-----|
| DA-W1-A2-001 | `@frozen struct` with `Float?`/`Bool?` fields + instance methods | Flag under-fire → possible CallConvSwift self misclass |
| A1 GSF phases | Max generic static dispatch if not already covered | Dual phase-order path |
| A3 | Nested escaping inner box / throwing-closure error soft assert | Intentional-leak verification |
| A2 mixed-indirect tuples | `(T, Int)` return | Roadmap already; still no max-case |

### 5. L3 graceful degradation (light in Wave 1)

A2 notes fail-closed layout **skip** as correct degrade. A1 integrity gates stay hard (good). Deeper G1 still Wave 7.

### 6. L4 simplification seeds

- Enum case + GSF into `CdeclSignatureContract` or shared phase table  
- Document CGFloat dual domain as intentional SSOT with tests  
- NestedClosureBridge leak documentation vs GCHandle pool  
- (Not: merge async emitters — prior art rejected)

---

## Running backlog draft (not prioritized for implement yet)

| Priority seed | Item | Status |
|---------------|------|--------|
| High fixture | Optional float/bool field flags (A2-001) | candidate |
| Medium L4 | Unify cdecl phase oracles (A1) | confirmed hazard |
| Medium L2 | Fix stale roadmap/KeyPath comments on inout writeback | docs |
| Low | Mixed-indirect tuples | already-known latent |
| Watch | A3 finalizer skip when metadata handle 0 | candidate |

---

## Ledger

Wave 1 marked ~80+ production files as reviewed-deep across the three track “files read” lists (see each track §). Full ledger status flip deferred to batch update after Wave 2 to avoid thrash.

---

## Wave 2 readiness

**Go.** Reverse-dispatch / EveryProtocol is the historically highest compile-but-dead and vtable-shift risk. Split:

- **A5a** — `EveryProtocolEmitter` + `VtableLayout` membership/index  
- **A5b** — `ProtocolProxyEmitter.Receivers` + projected vs raw keys + async CT  
- **A5c** — `StaticInit` + `WitnessDispatchEmitter` + fillability filters  

Do **not** assign one agent the whole 7.4k EveryProtocolEmitter file alone.
