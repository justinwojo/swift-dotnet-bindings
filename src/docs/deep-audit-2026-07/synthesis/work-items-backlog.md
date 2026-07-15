# Work Items Backlog — Deep Audit 2026-07

> **⚠️ SUPERSEDED by [`../00-AUDIT-VERIFICATION.md`](../00-AUDIT-VERIFICATION.md) §3.** That worklist is the single source of truth for the fix program. This backlog's ranking was written pre-verification; several of its top items were refuted or re-scoped (e.g. DA-W5-A8-002 refuted, G1-003's SB0006 arm already ships, S1-37 has live callers). Read any row here only through the verification's §3/§4 corrections and §9 discount rules. Per-row `[Verification 2026-07-16]` notes flag the ones that moved.

**Status**: Ranked for owner promotion. **Not implemented.**  
**Scoring**: severity × reachability × consumer impact × fixture/effort cost  
**Sources**: All tracks under `../tracks/` + G1/M2/T/W10 rollups.

Legend: **P** = product/policy · **B** = bug/correctness · **G** = gate/test · **S** = simplification · **D** = docs drift

---

## Tier 0 — Do first (product + cheap correctness)

| # | ID | Type | Item | Why | Effort | Evidence |
|---|-----|------|------|-----|--------|----------|
| 1 | **G1-001 / M2-001** | P | **Partial-success packaging policy** when wrapper fails | Day-1 cliff: soft 050 then default 051 kills package | M (policy + docs + optional default) | Track-G1, Track-M2, graceful-degradation-map |
| 2 | **G1-004 + T** | G | **PartialSuccessKitchen** fixture/gate | Proves unsupported shapes → exit 0 + clean compile + report | S–M | Track-T, G1-004 |
| 3 | **DA-W5-A8-002** | B | **[Verification 2026-07-16: REFUTED — see 00-AUDIT-VERIFICATION §4.1]** ~~Fix `public nonisolated` visibility false-internal → property drop~~ — unreachable: swiftc canonicalizes to `nonisolated public` in generated `.swiftinterface`, the only form the walker consumes; 0 corpus hits for the failing order. Do not implement. | ~~P1 candidate~~ Refuted | — | Track-A8, §4.1 |
| 4 | **D1/D2** | D | Patch **constraints.md** legacy async CT “incomplete” (fixed) | Agents re-open fixed work | XS | W2 A5b, W10 |
| 5 | **Short-term G1** | P/D | Document ritual: `SwiftWrapperRequired=false` for exploration | Unblocks users without code default change | XS | M2 |

---

## Tier 1 — High consumer / gate value

| # | ID | Type | Item | Why | Effort |
|---|-----|------|------|-----|--------|
| 6 | **G1-003** | P/B | **[Verification 2026-07-16: re-scoped — see 00-AUDIT-VERIFICATION §4.2]** Produce-throw already compile-errors via `[Obsolete(error:true)]` SB0006 (ships today, stronger than the proposed `EditorBrowsable`). Only the **consume-degraded** arm survives: silent at compile time, mapped to `KnownLimitation` (`SkipDisposition.cs:124`, `ReviewCount=0`). Scope to that arm. | Compile-but-dead → bug reports | M |
| 7 | **G1-002** | P | Opt-in **Swift-only continue** on mixed ObjC systemic fail | Total abort today | M |
| 8 | **DA-W8-T4-001** | G | Enforce or delete **dead baselines.json keys** | Theater baselines | S |
| 9 | **DA-W1-A2-001** | B | `HasFloatFields`/`HasBoolFields` unwrap **Optional** | CallConvSwift self under-fire | S + fixture |
| 10 | **G1-005** | B/G | Drive **MissingWrapperSymbol / strip** toward 0 | Integrity + day-1 cleanliness | ongoing |
| 11 | **S1-05** | S/B | **VisibilityClassifier** SSOT (protocol req + nonisolated + subscripts) | Unifies A8-001/002/003 | M |

---

## Tier 2 — Dual-oracle / simplification (capability-preserving)

| # | ID | Type | Item | Risk class | Effort |
|---|-----|------|------|------------|--------|
| 12 | **S1-13** | S | Delete dead `ProtocolProxyEmitter.Helpers.GetMethodKey` | byte-identical | XS |
| 13 | **S1-06** | S | Extract optional concrete-class Path 3 (M3) | byte-identical | S |
| 14 | **S1-01** | S | Share TypeSkipPrePass ↔ handler skip predicates | behavior-preserving | M |
| 15 | **S1-02** | S | Collapse `MethodEmitsVtableField` → layout IncludesMethod | behavior-preserving | S |
| 16 | **S1-03/04** | S | Reverse-dispatch width + hand-enumerators → VtableLayout | behavior-preserving + fixtures | M |
| 17 | **S1-08** | S | GSF/enum case phases → CdeclSignatureContract | behavior-preserving + fixtures | M |
| 18 | **S1-07** | S | CGFloat/Optional spare-bit → AppleFrameworkRegistry | behavior-preserving | M |
| 19 | **A1 dual** | S | Document intentional CdeclLowering vs PInvoke dual | docs | XS |
| 20 | **W6 L4** | S | ExistentialContainer0–8 consolidation | post-1.0 style | L |

Full list: [`simplification-opportunities.md`](simplification-opportunities.md) (40 rows).

---

## Tier 3 — Fixture-close candidates / latents

| # | ID | Item | Notes |
|---|-----|------|-------|
| 21 | DA-W5-A8-003 | Subscript visibility classification | P2 |
| 22 | DA-W3-A6-001 | SameType sugar Data? vs Optional | CSM undercount |
| 23 | A7 CSM parent cancel | CancellationError → TrySetException only | candidate |
| 24 | A3 nested escaping inner | Intentional leak verify | already-known design |
| 25 | A2 mixed-indirect tuples | Roadmap latent | needs max-case |
| 26 | W9 CreateAsync parity | Latent CS0030 if complex enum leaf | already-known |
| 27 | W9 SB1002 property FN | Analyzer candidate | low |
| 28 | M2 stamp-before-success / gen fp | Soft packaging honesty | candidates |

---

## Tier 4 — Explicit non-goals / rejected

| Item | Why |
|------|-----|
| Full async-emitter merge | Roadmap rejected; A7 re-confirmed |
| Layout from skip sets | Slot-shift regression |
| Merge Mono/AOT collection factories | Behavioral dual |
| Full BindingAudit library re-walk | Prior art; delta only |
| 5th upstream Mono issue without repro | Doctrine |
| Capability-typed projection mega-refactor | Post-1.0 / deferred |

---

## Suggested work streams (if executing later)

### Stream A — Day-1 experience (G1)
1. Docs ritual (`SwiftWrapperRequired=false`)  
2. PartialSuccessKitchen fixture  
3. Policy decision: soft default vs opt-in partial mode  
4. Produce-throw surface policy  
5. Mixed ObjC continue opt-in  

### Stream B — Visibility honesty (A8)
1. nonisolated PublicMemberNames  
2. VisibilityClassifier SSOT  
3. Protocol req implicit public (finish mitigations)  

### Stream C — Gate honesty (T)
1. baselines.json real or delete  
2. Reduce mega-test string theater gradually  
3. PartialSuccess + optional float field fixtures  

### Stream D — Dual-oracle hygiene (S1)
1. Dead key delete  
2. TypeSkip / vtable field / cdecl phase consolidations  
3. constraints.md drift pass  

---

## Promotion checklist (when implementing)

- [ ] Owner picks stream + tier items  
- [ ] Red fixture first for B items  
- [ ] Rebuild generator Debug dll before regen  
- [ ] `nuke test` + `nuke binding-tests` (device if ABI)  
- [ ] Do **not** soften integrity 108 / TN2435 / false HasWrapper  
