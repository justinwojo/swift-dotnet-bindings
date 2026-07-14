# Mid-Audit Executive Summary — Deep Audit 2026-07

**Date**: 2026-07-16  
**Status**: Waves 0–5, G1, M2 packaging, T tests complete. Remaining: Runtime line-complete (W6), ObjC/SwiftUI/analyzers (W9), simplification rollup (W10), final backlog (W11).

---

## One-screen verdict

| Layer | Risk | Story |
|-------|-----:|-------|
| P/Invoke, layout/VWT, ARC, reverse-dispatch layout, CSM, TypeDB, closures, async | **~2/5** | **Hardened.** 0 new emission-live P0s across deep tracks. Dual-oracle hygiene + doc lag. |
| **Day-1 new library (G1 + M2)** | **3/5** | **Admission strong; package policy kills.** Default wrapper-required → total death on wrapper fail. |
| **Parser visibility (A8)** | **3/5** | Core fidelity strong; `nonisolated` / protocol-req visibility dual-oracle can **drop** public surface. |
| **Tests/gates (T)** | **3/5** | BindingTests skip honesty good; **unit mass theater** + **baselines.json mostly unenforced**. |

**Audit thesis update:** For this codebase, unlimited internal re-auditing of ABI cores has **diminishing P0 yield** (confirms roadmap “input-poor”). Highest leverage is **product policy (partial success)**, **visibility/admission honesty**, **test meaning**, and **simplification** — not another CallConv hunt.

---

## Owner-priority: graceful degradation

### Already good
- Member/type skip at emission with `SkipReason` + report  
- Integrity hard-fails where required (108, packaging truth)  
- Soft generator path for wrapper fail exists mechanically  

### The cliff
1. Generator may **soften** SWIFTBIND050 on wrapper fail  
2. SDK default **`SwiftWrapperRequired=true`** re-raises → **SWIFTBIND051 Error** → **no usable package**  
3. Mixed ObjC systemic parse → **total abort** (no Swift-only)  
4. Suppressed reverse-dispatch → **public compile-but-dead** (throw/silent)

### Ranked opportunities (not implemented)
| Rank | ID | Action class |
|-----:|----|--------------|
| 1 | G1-001 / M2-001 | Partial package policy when wrapper fails (document soft ritual short-term; product default medium) |
| 2 | G1-003 | Omit/hide produce-throw reverse APIs |
| 3 | G1-002 | Opt-in Swift-only on mixed ObjC fail |
| 4 | G1-004 + T | Product fixture: unsupported kitchen → clean partial |
| 5 | G1-005/006 | Strip / MissingWrapperSymbol → 0; shared TypeSkip |

---

## Top new technical candidates (fixture-worthy)

| ID | Claim | Severity |
|----|-------|----------|
| **DA-W5-A8-002** | `public nonisolated` excluded from PublicMemberNames → false internal → **property drop** | P1 candidate |
| **DA-W1-A2-001** | HasFloat/BoolFields ignore Optional\<Float/Bool\> | P2 candidate |
| **DA-W5-A8-003** | Subscripts skip visibility classification | P2 candidate |
| A8-001 | Protocol req implicit public (already-known, mitigated) | P1 already-known |
| M2 stamp/fingerprint residuals | stamp before success; gen fp omits dll | P2 candidates |

---

## Tests honesty (T)

| Good | Bad |
|------|-----|
| BindingTests skips specific; 0 MonoJitCrash; strip tripwire 0 | Mega unit tests = `Assert.Contains` blob theater |
| compile-only fail-closed on real legs | `baselines.json` keys mostly **not enforced** (only `wrapper_stripped_count`) |
| Issue-1 attribution gated | No PartialSuccessKitchen product scenario |
| EnsureGeneratorBuilt fingerprint-fresh (docs lag) | Coverage matrix not CI-automated |

---

## Stale documentation (code ahead of docs)

- constraints.md: legacy async CT incomplete → **fixed**  
- Roadmap F8 Vtables closure-skip only → **refuted**  
- Multiple CSM medium rows → **fixed in code**  
- inout blittable writeback “missing” → **cdecl path fixed**  
- EnsureGeneratorBuilt “never rebuilds stale” → **may be fixed** (T report)  

Wave 10/11 should produce a **docs-drift checklist**, not re-open fixed bugs.

---

## Completed track index

| Track | Path | Risk |
|-------|------|-----:|
| A1 P/Invoke | tracks/Track-A1_* | 2 |
| A2 Layout | tracks/Track-A2_* | ~2–3 |
| A3 ARC | tracks/Track-A3_* | ~2–3 |
| A5a/b/c Protocols | tracks/Track-A5* | 2 |
| A6 CSM | tracks/Track-A6_* | 2 |
| M3 TypeDB | tracks/Track-M3_* | 2 |
| A4 Closures | tracks/Track-A4_* | 2 |
| A7 Async | tracks/Track-A7_* | 2 |
| **G1 Degrade** | tracks/Track-G1_* + synthesis/graceful-degradation-map | **3 day-1** |
| **A8 Parser** | tracks/Track-A8_* | **3** |
| **M2 Packaging** | tracks/Track-M2_* | **3** |
| **T Tests** | tracks/Track-T_* | **3** |

Wave maps: `00-codebase-map.md`, W1–W4 syntheses under `waves/`.

---

## Recommended next steps (audit remaining)

1. **W6** Runtime line-complete (confirm A3; small surface)  
2. **W9** ObjC + SwiftUI bridge + analyzers  
3. **W10** S1 simplification rollup + constraints.md drift pass  
4. **W11** Final `work-items-backlog.md` ranked for owner promotion  

**Optional early implement (owner-gated, not started):** PartialSuccessKitchen fixture + document `SwiftWrapperRequired=false` ritual — highest ROI if you want a tangible win before full audit ends.
