# Track A5c — StaticInit + WitnessDispatch + Fillability

| Field | Value |
|-------|--------|
| **Wave** | 2 |
| **Track** | A5c |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (post-`VtableLayout` SSOT: layout/fillability split is deliberate and tested; residual risk is intentional null slots + dual index axes, not silent slot-shift) |
| **Confidence** | **high** on StaticInit / Vtables / fillability contract; **high** on WitnessDispatch string-first + eligibility index lockstep; **medium** on cross-module parent package-coupling without a live multi-package fixture |
| **Lenses** | L1 (slot fill / index), L3 (null reverse slots), L4 (rebuild / dual walks), L5 (axis confusion) |

## Headline

**After `VtableLayoutBuilder`, reverse-dispatch layout no longer consults C# skip sets.** Same-module StaticInit and Receivers fill from `MethodSlotIndexByKey` and leave unfillable Included slots **null**; cross-module parent cctors intentionally use **empty** skip sets and fill every `Includes*` slot. Roadmap **F8** (as a *struct-size / skip-set layout divergence*) is **refuted by current code** — residual null-fn-pointer on force-unwrap for fillability-skipped Included members is the **documented fillability model**, already-known as compile-but-dead / reverse-dead surface (BA / BSA), not a new slot-corruption P0. WitnessDispatch keeps a **separate** forward SBW index axis (`EffectiveWitnessSlotKey`) with string-first property branches and secondary C# projection gates; those look sound.

---

## 1. Scope & method

### In scope

| File | Role |
|------|------|
| `ProtocolProxyEmitter.StaticInit.cs` | Child + cross-module parent cctor / `InitializeVtable` / local+Swift assignment |
| `ProtocolProxyEmitter.Vtables.cs` | `{P}SwiftVTable` / `{P}LocalVTable` field layout |
| `ProtocolProxyEmitter.Receivers.cs` (fillability loop only) | Receiver emit + fillability filters (A5b owns body detail) |
| `ProtocolProxyEmitter.CrossModuleParent.cs` | Parent scaffolding; empty skip-set reset |
| `VtableLayout.cs` / `ProtocolVtableMembers.cs` | Layout SSOT + `MethodSlotIndexByKey` |
| `WitnessDispatchEmitter.cs` | Forward SBW accessors; eligibility; string/blittable gates |
| `Handler/ProtocolHandler.cs` (skip-set population) | How `_skipped*` / `_closureSkipped*` are filled |
| `Handler/ExistentialBypassEmitter.cs` | Light touch — forward bypass only |
| `.claude/rules/constraints.md` reverse-dispatch section | Dual-axis contract |

### Out of scope (sibling tracks)

- EveryProtocol extension body / fan-out plan internals → **A5**
- Receiver marshalling bodies / lifetime → **A5b / A3**
- Projected-key one-core (AF05) deep walk → **A5 / C2**

### Method

1. Read methodology, codebase map, prior-art, roadmap F8 / fan-out latent.  
2. Branch-read layout vs fillability vs StaticInit same-module vs cross-module.  
3. Cross-check WitnessDispatch eligibility index vs InterfaceImpl SBW call sites.  
4. Tag already-known; only file deltas / residual hazards with evidence.

---

## 2. Architecture snapshot (post-Finding-8 / Bug-21)

### Two layers that must never be merged

| Layer | Oracle | What it decides |
|-------|--------|-----------------|
| **LAYOUT** | `VtableLayoutBuilder.Classify*` → `IncludedSlots` / `MethodSlotIndexByKey` | Field exists; slot index; width. **Ignores** `_skippedMethodKeys`. Keeps AnyType-unprojectable + raw-distinct existential overloads. |
| **FILLABILITY** | `_skippedMethodKeys` + raw-signature collapse (`EffectiveRawKey`) + projected-C# collapse (`EffectiveProjectedKey`) | Whether to emit `Receive_*` and assign `Func_*` / `func_*`. Unfillable → **null slot**, same struct size as Swift. |

`ProtocolProxyEmitter.Vtables.cs:12–44` states this explicitly: gating layout on skip sets / projected keys would shrink C# below Swift → slot shift → device SIGSEGV.

### Same-module StaticInit flow

```text
InitializeVtable (lock)
  → RunClassConstructor(ancestor proxies)   // same-module only
  → if _setVtableEmitted: EmitChildVtablePopulation
       local: Includes* + !skipped* + raw/projected dedup → Func_* = &Receive_*
       pin GCHandle
       swift: same fillability → func_* = (IntPtr)local.Func_*
       Set{P}_vtable
  → foreach cross-module parent: EmitCrossModuleParentVtableInit
  → _vtableInitialized = true
```

Slot index for methods: **one** `VtableLayoutBuilder.Build(...).MethodSlotIndexByKey` map shared by local and swift assignment loops (`StaticInit.cs:273–278`, `667–671`).

### Cross-module parent flow

`EmitCrossModuleParentScaffolding` **zeros** all skip sets for each parent (`CrossModuleParent.cs:114–118`), then:

- Emit parent Swift/local vtable structs (layout SSOT)  
- Emit receivers with `applyVtableMembershipFilter: true`  
- Child `InitializeVtable` still runs `EmitCrossModuleParentVtableInit` even when the child itself is Set-vtable-less (empty marker) — so inherited reverse dispatch still wires.

Comment contract: last child cctor to write the module-global parent `_p_vtable` wins; all receivers use covariant `IProtocolProxyImpl<DEP.IParent>` — last-write-wins is correct (`CrossModuleParent.cs:90–92`).

### Two index axes (do not unify)

| Axis | Key | Consumers |
|------|-----|-----------|
| **Reverse / vtable** | `EveryProtocolEmitter.GetMethodKey` (label-inclusive, async-sensitive) via `VtableLayoutBuilder.GetSlotKey` | Swift `{P}_vtable`, C# struct fields, receivers, StaticInit |
| **Forward / SBW** | `ProtocolMethodDisambiguator.EffectiveWitnessSlotKey` → label-blind `WitnessDispatchEmitter.GetMethodKey` except disambiguated label-only pairs | `EmitWitnessDispatchFunctions`, InterfaceImpl method index, SwiftObject P/Invoke decls |

Documented at `WitnessDispatchEmitter.cs:2545–2571` and `VtableLayout.cs:68–69`. Conflating them is the classic Bug-21 class.

---

## 3. Files reviewed (depth)

| Path | Depth | Notes |
|------|-------|-------|
| `ProtocolProxyEmitter.StaticInit.cs` | **reviewed-deep** | Full 773 LOC |
| `ProtocolProxyEmitter.Vtables.cs` | **reviewed-deep** | Full layout emitters |
| `ProtocolProxyEmitter.CrossModuleParent.cs` | **reviewed-deep** | Skip reset + Set entry points |
| `ProtocolProxyEmitter.Receivers.cs` | **reviewed-deep** (fill loop `:16–137`) | Bodies deferred to A5b |
| `ProtocolProxyEmitter.InterfaceImpl.cs` | partial | SBW index + skip/stub interaction |
| `VtableLayout.cs` / `ProtocolVtableMembers.cs` | **reviewed-deep** | Classify* + MethodSlotIndexByKey |
| `WitnessDispatchEmitter.cs` | **reviewed-deep** on gates / index / string-first | Full file ~2.6k; accessor bodies sampled |
| `Handler/ProtocolHandler.cs` | skip-set population only | `:304–472` |
| `Handler/ExistentialBypassEmitter.cs` | light | No reverse-dispatch surface |
| `VtableLayoutBuilderTests.cs` / `ProtocolProxyEmitterTests.cs` | tests | Fillability + skip-but-consume fixtures |

---

## 4. Findings

### DA-W2-A5c-001: Roadmap F8 layout claim is obsolete (refuted as stated)

- **Severity**: P1 (historical) → **status update**
- **Status**: `refuted` (original “Vtables consults only `_closureSkippedMethodKeys`” layout bug) + residual `already-known` (null fillability)
- **Confidence**: high
- **Lenses**: L1, L5
- **Reachability**: N/A for refuted form; residual `emission-live` for AnyType-skipped reverse
- **Claim**: Roadmap latent F8 describes `Vtables.cs` consulting only `_closureSkippedMethodKeys` (not `_skippedMethodKeys`), producing an unassigned field Swift still force-unwraps. **Current code:** `Vtables.cs` consults **neither** skip set — only `layout.IncludedSlots` (`Vtables.cs:45–60`, `87–101`). Skip sets are **fillability-only** (`Vtables.cs:16–17`, tests `ProtocolProxyEmitterTests.EmitProxyClass_ClosureSkippedMethod_OmitsVtableSlotEntirely` comments at `:5426–5430`).
- **Evidence**:
  - Non-dispatchable closures: `ClassifyMethod` → `ExcludedNonDispatchableClosure` → no Included field; index still consumed (skip-but-consume) — pinned by fixture expecting `func_cleanup_1` not `_0`.
  - Interface-skipped but layout-Included (AnyType / projected collision): StaticInit `if (_skippedMethodKeys.Contains(collapsingKey)) continue` (`StaticInit.cs:301–302`, `379–380`) leaves null; struct size still matches Swift.
- **Residual (not F8-as-written)**: Solo EveryProtocol paths still force-unwrap (`func_*_get!` at `EveryProtocolEmitter.cs:3019` et al.). A fillability-null Included slot → Swift nil-unwrap / SIGSEGV if that requirement is reverse-dispatched. That is the **intentional** “Swift keeps slot, C# cannot fill” model documented on Receivers (`Receivers.cs:66–80`), same class as BA “compile-but-dead” / reverse-dead surface — **do not re-file as new layout corruption**.
- **Prior art**: roadmap F8; BA-SUM EveryProtocol theme; constraints.md reverse-dispatch SSOT.

### DA-W2-A5c-002: `_skippedMethodKeys` vs `_closureSkippedMethodKeys` roles (not a latent divergence)

- **Severity**: P3 (docs clarity)
- **Status**: `refuted` as accidental divergence; intentional dual role
- **Confidence**: high
- **Lenses**: L5
- **Claim**: The two sets are not competing layout oracles. ProtocolHandler:
  - Gate/dedup skips → `_skippedMethodKeys` only (`ProtocolHandler.cs:399–420`)
  - Interface-only **non-dispatchable** closure surface → **both** (`:443–444`)
  - ObjC optional → `_skippedMethodKeys` only (`:464`) + no reverse slot (pre-skip)
- **Consumers**:
  | Site | Uses |
  |------|------|
  | Vtables layout | neither |
  | StaticInit / Receivers fillability | `_skippedMethodKeys` only |
  | InterfaceImpl stubs | if skipped **and** closureSkipped → NotSupported stub (`InterfaceImpl.cs:163–178`) |
- **Invariant**: under ProtocolHandler population, `_closureSkippedMethodKeys ⊆ _skippedMethodKeys`. Fillability does not need the closure set; interface stubs do.
- **Prior art**: none as a defect; update F8 text if roadmap is edited.

### DA-W2-A5c-003: Child StaticInit Swift assignment loop omits raw-key dedup re-check

- **Severity**: P3
- **Status**: `candidate` (behaviorally null-safe today)
- **Confidence**: high that outcome is null; medium that future edit could diverge
- **Lenses**: L4, L5
- **Claim**: Local assignment (`StaticInit.cs:280–314`) applies `emittedRawKeys` + `emittedCSharpKeys`. Swift assignment (`:359–385`) clears only `emittedCSharpKeys` and **does not** consult `emittedRawKeys`. For existential overload pairs (two slots, one raw collapse key), local fills only the first; swift still emits `func_*_{idx2} = (IntPtr)_localVTable.Func_*_{idx2}` for the second → **null→null**, compiles, matches fillability intent.
- **Evidence**: `StaticInit.cs:359–385` vs local `301–313`; cross-module swift loop similarly lacks raw-key re-check (`:738–758`) while local has it (`:703–707`).
- **Probe**: Unit fixture already covers single receiver for raw collapse (`ProtocolProxyEmitterTests` ~`:1658`, cross-module `EmitProxyClass_CrossModuleParent_TwoExistentialOverloadsSameRawKey_EmitsSingleReceiver`). Assert swift initializer does not reference a never-assigned local name (would be CS) — currently assigns default null field, so green.
- **Suggested simplification (L4)**: Share one “should fill method slot?” predicate used by local + swift + receivers, or re-apply the same three filters on both assignment loops. Risk: byte-identical / behavior-preserving.

### DA-W2-A5c-004: Cross-module parent fillability uses empty skip sets by design

- **Severity**: P2
- **Status**: `candidate` (package-coupling hazard; no live multi-nupkg fixture in this track)
- **Confidence**: medium
- **Lenses**: L1, L5, L3
- **Claim**: Parent scaffolding resets skip sets to empty (`CrossModuleParent.cs:114–118`). Parent StaticInit therefore fills **every** `IncludesMethod` / property / subscript slot and receivers are gated only by `ProtocolVtableMembers` (`applyVtableMembershipFilter: true`). Child’s ProtocolHandler never computed parent skip sets.
- **Implication**:
  - Positive: reverse-dispatch for inherited DEP.Parent requirements is not under-filled by child-local AnyType decisions.
  - Hazard: receivers call `impl.{Method}` on `DEP.IParent`. If the **published parent package** omitted a method the child’s TypeDB now treats as Included+projectable (or the reverse), the child binding can **fail compile** (CS1061) or fill a slot the parent package would have left null.
- **Evidence**: `CrossModuleParent.cs:99–132`, `StaticInit.cs:627–708` comments (“Cross-module parents have empty skip sets… IncludesMethod alone drives layout”).
- **Probe**: Two-package fixture: parent nupkg skips AnyType method; child inherits parent + emits xm scaffolding; build child.
- **Prior art**: none as a named latent; related to multi-binding EveryProtocol metadata comments in StaticInit (`:38–50`).

### DA-W2-A5c-005: WitnessDispatch string-first + struct/class gates — verified correct

- **Severity**: n/a (positive verification)
- **Status**: `reviewed` / no defect
- **Confidence**: high
- **Lenses**: L1
- **Claim**: Property getter emission checks `IsTypeBlittable || IsStringType` **before** class/struct/collection/existential branches (`WitnessDispatchEmitter.cs:219–250`). `IsClassReturn` / `IsStructReturn` **explicitly reject** String (`:819–834`) so Swift.String (frozen+RefFields) cannot fall into the indirect struct path. Constraints.md “string FIRST” rule holds.
- **Secondary C# gates**: `InterfaceImpl.EmitMethodImplementation` re-validates projected types for BlittableOrString / throwing / existential / class / struct / bound-generic (`InterfaceImpl.cs:1440–1530`) and degrades to SB0003 without P/Invoke when C# projection disagrees with Swift-side dispatchability — prevents EntryPointNotFound when TypeDB degrades to AnyType after index assignment.
- **Index lockstep**: Eligible methods always consume SBW index even if `ClassifyMethodDispatch == NotDispatchable` (`WitnessDispatchEmitter.cs:276–287` increment before kind branch; InterfaceImpl `methodIndex++` at `:151` before skip/stub). Matches “index for symbol naming, emit only when dispatchable.”

### DA-W2-A5c-006: Same-signature closure/async fan-out — already-known latent

- **Severity**: P1 if triggered
- **Status**: `already-known`
- **Confidence**: high (mechanism in EveryProtocol; out of A5c ownership for fix design)
- **Lenses**: L1
- **Reachability**: `latent` (roadmap)
- **Claim**: Owner/sibling fan-out not threaded into closure-param / closure-return / async emitters; non-owner-only C# impl → owner force-unwraps nil field. **Not re-investigated as novel.**
- **Prior art**: roadmap “Same-signature closure/async method fan-out gap”; abi-coverage-grid notes.

### DA-W2-A5c-007: Solo-path force-unwrap vs fillability-null slots (L3 note)

- **Severity**: P2 (product degrade)
- **Status**: `degrade-opportunity` / `already-known` theme
- **Confidence**: high
- **Lenses**: L3, L1
- **Claim**: Multi-branch fan-out uses `if let fn = vtable.func_…` + `fatalError` else (`EveryProtocolEmitter.cs:3039–3048`). Single-branch still uses `!` (`:3019`). Any fillability-null Included slot on a solo protocol hits hard unwrap rather than a branded `fatalError` message. Fan-out “forceSafeFanOut” path exists for filtered peers but not generically for “C# left slot null.”
- **Suggested direction**: Prefer nil-checked dispatch (or always safe fan-out shape) for reverse methods that may be fillability-null — loud, branded failure instead of SIGSEGV. Needs fixture: protocol with AnyType-skipped requirement still reverse-reachable via existential cast (if any path can invoke it).
- **Prior art**: BA compile-but-dead; EveryProtocol forceSafeFanOut comments.

### DA-W2-A5c-008: Repeated `VtableLayoutBuilder.Build` per protocol (L4)

- **Severity**: P3
- **Status**: `simplification`
- **Confidence**: high
- **Lenses**: L4
- **Claim**: Layout is rebuilt independently in EmitSwiftVtableStruct, EmitLocalVtableStruct, Receivers method loop, child StaticInit, cross-module StaticInit (×N parents). Pure/stateless so **correctness-safe**; cost is CPU + dual-walk mental load.
- **Suggested simplification**: Thread one `VtableLayout` through proxy emission for a protocol (and per parent). Risk class: behavior-preserving; needs no fixture if reference-equal field order asserted by existing ArtifactParityGate Gate 3.
- **Do not do if**: caching mutates layout mid-emission or reuses across protocols without keying.

### DA-W2-A5c-009: Stale comment on skip-key population (InterfaceImpl)

- **Severity**: P3
- **Status**: `confirmed` (doc drift only)
- **Confidence**: high
- **Lenses**: L5
- **Claim**: `InterfaceImpl.cs:142–143` says ProtocolHandler populates skip sets with `GetMethodSignatureKey`. Actual ProtocolHandler uses `ProtocolMethodDisambiguator.EffectiveRawKey` (`ProtocolHandler.cs:379`), which is label-inclusive for disambiguated pairs. Implementation of consumers also uses `EffectiveRawKey` — **behavior aligned**; comment stale.
- **Prior art**: none.

### DA-W2-A5c-010: ExistentialBypassEmitter (light) — no reverse-dispatch interaction

- **Severity**: n/a
- **Status**: `reviewed`
- **Confidence**: high
- **Claim**: Bypass is for **forward** struct ctor / instance methods with defaulted existential / unsupported optional-closure params. Does not touch vtables, StaticInit, or WitnessDispatch. No A5c hazard.
- **Evidence**: `ExistentialBypassEmitter.cs:8–22`.

### DA-W2-A5c-011: Cross-module ancestor `typeof` cctor gap — mitigated for reverse path

- **Severity**: P3 residual
- **Status**: `already-known` / documented gap
- **Confidence**: high
- **Claim**: `EmitAncestorProxyCctorInit` **skips** cross-module ancestors (`StaticInit.cs:469–476`) — no compile-time `typeof` on foreign proxy. Reverse path for cross-module parents is the **dedicated** Set{Module}_{Parent}_vtable scaffolding, not ancestor RunClassConstructor. Same-module inheritance still depends on ancestor cctors (`:151–162` crash repro narrative). Design is consistent; gap is only “foreign assembly typeof,” not missing xm reverse wiring.
- **Prior art**: comments in StaticInit itself.

### DA-W2-A5c-012: WitnessDispatch / reverse axis confusion risk (L5 hazard map)

- **Severity**: P2 if an edit unifies wrongly
- **Status**: `hazard` (maintainability; not a current defect)
- **Confidence**: high
- **Lenses**: L5
- **Claim**: Three key helpers look similar (`GetMethodKey` ×2 domains, `EffectiveRawKey`, `EffectiveWitnessSlotKey`, `EffectiveProjectedKey`). Correct fillability uses raw+projected; correct reverse index uses layout/GetMethodKey; correct SBW uses EffectiveWitnessSlotKey. A “cleanup” that uses projected key for layout or MethodSlotIndexByKey lookup re-opens Finding-8 corruption.
- **Mitigations already present**: VtableLayoutBuilderTests; ArtifactParityGate vtable field parity; heavy comments on each walk; constraints.md SSOT paragraph.
- **Suggested simplification**: Do not merge axes; optionally rename APIs (`GetReverseSlotKey` / `GetForwardWitnessKey`) in a non-behavior rename pass.

---

## 5. Fillability vs `MethodSlotIndexByKey` matrix

| Walk | Index source | Pre-skip (no lookup) | Layout filter | Fillability filters |
|------|--------------|----------------------|---------------|---------------------|
| Vtables (Swift/local structs) | `slot.SlotIndex` from IncludedSlots | n/a (model) | Included only | **none** |
| Receivers | `methodSlotIndices[slotKey]` | ctor / static / @objc optional | `IncludesMethod` | skipped + raw + projected |
| Child StaticInit local | same map | same | Includes* | skipped + raw + projected |
| Child StaticInit swift | same map | same | Includes* | skipped + projected (**not** raw — A5c-003) |
| XM parent StaticInit local | parent map | same | Includes* | raw + projected (skip sets empty) |
| XM parent StaticInit swift | parent map | same | Includes* | projected only |
| WitnessDispatch (forward) | independent `methodIndex++` | ineligible (ctor/static/optional/mixed-generic protocol) | n/a | ClassifyMethodDispatch emit gate |

**KeyNotFound safety**: `MethodSlotIndexByKey` omits SlotIndex &lt; 0 (pre-skip). Fill walks pre-skip the same three before lookup — pinned by `VtableLayoutBuilderTests` (~`:281–313`).

---

## 6. WitnessDispatch checklist (A5c slice)

| Check | Result |
|-------|--------|
| Property string-first | **Pass** (`IsTypeBlittable \|\| IsStringType` before struct path) |
| String not StructReturn | **Pass** (`IsStructReturn` / `IsClassReturn` reject String) |
| Property setter class/struct | **No dispatch** — only blittable/string setters (`:262–269`) → SB0003 / non-dispatch path on C# |
| Mixed-generic protocol | Eligibility false → no SBW symbols; InterfaceImpl SB0003 stubs (`IsMethodWitnessDispatchEligible` `:1083–1098`) |
| Async methods | `NotDispatchable`; still may consume reverse vtable slot if IncludesMethod (async reverse is S13 real-async path on reverse axis — separate from forward SBW) |
| Throwing + optional existential | Blocked (`:441–442`) |
| Secondary C# projection gates | Present on InterfaceImpl |
| Label-only overload split | EffectiveWitnessSlotKey + tests documented |

---

## 7. L3 / L4 notes (program lenses)

### L3 — Graceful degradation

| Pattern | Observation |
|---------|-------------|
| Interface skip + layout keep | Honest skip on interface / report; reverse slot null — not compile-broken C# |
| Closure non-dispatchable | Layout omits field; interface NotSupported stub — good degrade |
| Forward non-dispatchable | SB0003 Obsolete + throw; no dangling SBW P/Invoke when secondary gates fire |
| Reverse null slot | Solo `!` is harsh; multi-branch fatalError is better — A5c-007 |
| Empty / read-only proxy | Suppresses own Set-vtable population; avoids EntryPointNotFound (`StaticInit.cs:164–184`, `CrossModuleParent` empty for read-only) |

### L4 — Simplification candidates

1. Single `ShouldFillMethodSlot(method, protocol, skipSets, rawSeen, projectedSeen)` shared by Receivers + StaticInit×2.  
2. Cache one `VtableLayout` per protocol emission (A5c-008).  
3. Rename reverse vs forward key APIs (A5c-012) — capability-preserving rename only.  
4. **Do not** merge forward SBW index with reverse vtable index.  
5. **Do not** drive layout membership from `_skippedMethodKeys` (would re-open F8-class corruption).

---

## 8. Prior-art tagging

| Item | Treatment |
|------|-----------|
| Roadmap F8 null-fn / skip-set layout | **Refuted as written**; residual fillability nulls → already-known |
| Same-signature closure/async fan-out | already-known (A5c-006) |
| Existential overload orphan receiver (Firestore CS1503) | Fixed; StaticInit/Receivers raw+projected guards + tests |
| Inout ObjC-bridgeable dead slot | already-known (roadmap medium); layout retains slot intentionally |
| BA EveryProtocol compile-but-dead | already-known product theme |
| AF05 projected-key one-core | orthogonal; fillability still uses EffectiveProjectedKey |

---

## 9. Test coverage vs gaps

| Covered well | Gap |
|--------------|-----|
| VtableLayout index rules (ctor/static/optional/skip-but-consume/async width) | Multi-package cross-module skip-set coupling (A5c-004) |
| Closure non-dispatchable omits field + skip-but-consume index | Assert StaticInit swift loop raw-dedup parity (A5c-003) |
| Existential raw-key single receiver (same + xm) | Solo-path force-unwrap vs fillability-null branded error (A5c-007) |
| ArtifactParityGate C#↔Swift vtable field stems | — |
| WitnessDispatch label-only split + trailing index shift | — |

---

## 10. Counts

| Metric | Count |
|--------|------:|
| Production files deep-reviewed | 9 |
| Production files light / partial | 2 |
| Findings total | 12 |
| Refuted / positive verification | 3 (001 form, 002, 005) |
| Already-known | 3 (006, 007 theme, 011) |
| Confirmed new (doc / hazard) | 2 (009, 012) |
| Candidates | 2 (003, 004) |
| Simplification / degrade | 2 (008, 007) |
| P0 confirmed new | **0** |
| P1 new emission-live | **0** |

---

## 11. Risk summary

| Risk | Rating | Why |
|------|--------|-----|
| Slot-shift corruption (layout vs fill) | **Low** | VtableLayout SSOT + skip sets fillability-only + unit/parity gates |
| Null reverse slot crash | **Medium** (known) | Intentional for unfillable Included members; solo `!` harsh |
| Cross-module wrong fill | **Low–medium** | Empty skip by design; package interface coupling unfixtured |
| SBW index drift | **Low** | Shared eligibility + EffectiveWitnessSlotKey + secondary gates |
| Fan-out closure/async | **Latent** | Roadmap; not A5c novel |

**Overall track risk: 2 / 5.**

---

## 12. Recommended owner follow-ups (no implementation in this audit)

1. **Docs**: Rewrite roadmap F8 to “fillability-null + solo force-unwrap” or mark fixed-with-residual.  
2. **Optional harden**: Share fill predicate across local/swift assignment loops (A5c-003).  
3. **Optional fixture**: Parent package skip × child xm fill (A5c-004).  
4. **L3**: Prefer nil-checked reverse dispatch for fillability-null slots (A5c-007).  
5. **L4**: Cache `VtableLayout` per protocol emission pass (A5c-008).  
6. Leave fan-out latent on roadmap until max-case fixture.

---

## 13. Exit checklist (A5c)

| Item | Status |
|------|--------|
| StaticInit same-module fillability read | ✅ |
| StaticInit cross-module parent cctor | ✅ |
| Vtables layout vs skip sets | ✅ |
| MethodSlotIndexByKey vs filters | ✅ |
| WitnessDispatch string-first / gates | ✅ |
| F8 / fan-out prior-art tagged | ✅ |
| ExistentialBypass light | ✅ |
| Report path under deep-audit tracks | ✅ |
| Production edits | none (read-only) |
