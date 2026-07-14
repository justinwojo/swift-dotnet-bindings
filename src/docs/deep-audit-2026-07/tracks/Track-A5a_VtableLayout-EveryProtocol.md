# Track A5a — VtableLayout / EveryProtocol layout oracle

| Field | Value |
|-------|--------|
| **Wave** | 2 |
| **Track** | A5a |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (layout SSOT is production-hardened; residual dual oracles are width/fan-out hygiene, not live slot-shift) |
| **Confidence** | **high** on membership/index/SSOT for the three layout walks; **medium** on MethodEmitsVtableField / width dual-oracle reachability |
| **Lenses** | L1 (slot ABI), L2 (oracle tests), L3 (compile-but-dead), L4 (hand-allocators / dual oracles), L5 (stale comments vs code) |

## Scope (A5a only)

**In:** reverse-dispatch **layout** — membership, index consume rules, slot width, Swift `{P}_vtable` field emission, C# mirror **names** (struct field order), `VtableLayout` / `VtableLayoutBuilder` / `ProtocolVtableMembers`, unit pins + ArtifactParity Gate 3 role.

**Out (A5b / A5c):** `ProtocolProxyEmitter.Receivers` fillability, projected-key vs raw-key fill filters, `StaticInit` assignment loops, full ProtocolProxy surface, WitnessDispatch **forward** SBW axis (separate index space — documented, not re-audited here).

---

## 1. Method

1. Read methodology, codebase map, prior-art, Wave 1 synthesis, constraints.md VtableLayout / projected-key traps.  
2. Deep-read `VtableLayout.cs`, `ProtocolVtableMembers.cs`, layout-related slices of `EveryProtocolEmitter.cs` (`EmitProtocolVtableStruct`, extension index lookup, `EnumerateProtocolMethodsForDispatch` / `EnumerateIndexedSubscripts`, `GetMethodKey`, `MethodEmitsVtableField`, `GetWidth` consumers, `EmitMethodVtableField`).  
3. Compare C# mirror sites in `ProtocolProxyEmitter.Vtables.cs` (field names / order philosophy only).  
4. Cross-check unit tests: `VtableLayoutBuilderTests`, `ProtocolVtableMembersInvariantTests`, `ObjCExistentialFailClosedTests` (layout rows), `ArtifactParityGateTests` Gate 3, related proxy/extension pins.  
5. Tag already-known BindingAudit / roadmap items; do not re-file closed overload-collapse or Materials/RoomPlan as novel P0s.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `src/Swift.Bindings/src/Emitter/StringEmitter/VtableLayout.cs` | SSOT model + `Classify*` + `Build` + `GetWidth` + `MethodSlotIndexByKey` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolVtableMembers.cs` | Thin bool view over `Classify*` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | `EmitProtocolVtableStruct` (~749–800), extension method index (~1726–1845), `EnumerateIndexedSubscripts` / `EnumerateProtocolMethodsForDispatch`, `ComputeMethodEmissionPlans` field gate (~1316–1393), `EmitMethodVtableField` (~2829–2885), `MethodEmitsVtableField` (~5282–5321), `CountVtableSlots` / `GetMethodKey` / `EmitsRealAsyncWitness`, conformance ladder (`hasImplementableMembers`) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Vtables.cs` | C# `*SwiftVTable` / `*LocalVTable` layout walks (names / IncludedSlots) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs` | Index source only (`MethodSlotIndexByKey` + Includes*) — fill deferred A5c |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | Index source only (`MethodSlotIndexByKey`) — fill deferred A5b |
| `src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs` | Confirm **separate** forward `GetMethodKey` (label-blind) — not reverse layout |
| `src/Swift.Bindings/src/Reporting/SuppressedProxyReporting.cs` | Compile-but-dead taxonomy (L3 already-known) |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/VtableLayoutBuilderTests.cs` | Index / overload / width / map contracts |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolVtableMembersInvariantTests.cs` | Flag-matrix struct ↔ predicate |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ObjCExistentialFailClosedTests.cs` | Nested @objc existential lockstep + skip-but-consume |
| `src/Swift.Bindings/tests/UnitTests/ArtifactParityGateTests.cs` | Gate 3 vtable field-name parity (Finding 8) |
| `src/docs/Design/reverse-dispatch-lifetime.md` | Defect F (property non-requirement) history |
| `.claude/rules/constraints.md` | VtableLayout SSOT + remaining hand-allocators |
| `src/docs/roadmap.md` | Inout ObjC dead slot; overload-collapse FIXED |

---

## 3. Architecture inventory (current SSOT)

### 3.1 Single layout model

`VtableLayoutBuilder.Build(protocol)` produces one ordered `VtableLayout` of `VtableSlot` records:

| Kind | Identity | Numeric index | Pre-skip (no index) | Skip-but-consume (index, no field) | Field when Included |
|------|----------|---------------|---------------------|------------------------------------|---------------------|
| Property | name | always `-1` (name-keyed `func_{name}_get/set`) | static, @objc optional | non-requirement, non-dispatchable closure, Self, mixed-generic, nested @objc existential | yes |
| Subscript | `subscript:{n}` | position among non-static | static | Self / mixed-generic / nested @objc existential | yes |
| Method | `GetMethodKey` (labels + raw types + `:async`) | sequential | ctor, static, @objc optional | non-dispatchable closure, method-level generic, Self, mixed-generic, nested @objc existential; **raw-key duplicate → collapse** | yes |

**Width** (methods only): per non-debug, non-empty-tuple value param → `CountVtableSlots` (1, or 2 for dispatchable / async-dispatchable closure); `EmitsRealAsyncWitness` → `+3` trailing pointers.

### 3.2 Who renders `IncludedSlots` (must stay lockstep)

| Site | Role |
|------|------|
| `EveryProtocolEmitter.EmitProtocolVtableStruct` | Swift `{P}_vtable` fields |
| `ProtocolProxyEmitter.EmitSwiftVtableStruct` | C# sequential IntPtr mirror |
| `ProtocolProxyEmitter.EmitLocalVtableStruct` | C# managed-delegate mirror (arity from `slot.Width`) |

All three walk `layout.IncludedSlots` in declaration order. **Layout does not consult projected C# keys or ProtocolHandler skip sets** — fillability is separate (A5b/c).

### 3.3 Slot key domain (reverse vs projected)

- **Reverse slot key:** `EveryProtocolEmitter.GetMethodKey` = name + **argument labels** + raw `SwiftTypeSpec` + async effect.  
- **Projected overload key:** AF05 `BuildProjectedMethodKey` — interface/dedup domain only.  
- **Forward SBW key:** `WitnessDispatchEmitter.GetMethodKey` — **label-blind** (with disambiguator opt-in). Documented as a **different axis**; must not be conflated with reverse layout.

### 3.4 Index lookup for non-layout walks

Extension body, Receivers, StaticInit (same- and cross-module) take **index values** from `layout.MethodSlotIndexByKey[GetMethodKey(m)]` after pre-skipping ctor/static/@objc-optional. Pre-skipped methods are **absent** from the map (`SlotIndex == -1`).

### 3.5 Membership oracle chain

```
VtableLayoutBuilder.Classify{Property,Subscript,Method}
        ↑
ProtocolVtableMembers.Includes*  ==  (Classify* == Included)
```

Membership is pure/stateless; path-independent for same-module vs cross-module parent walks.

### 3.6 Defense-in-depth tests (good)

| Test surface | What it pins |
|--------------|--------------|
| `VtableLayoutBuilderTests` | pre-skip vs skip-but-consume; async overload split; raw-key collapse; static subscript no-index; `MethodSlotIndexByKey` omit pre-skip; real-async `+3` width; Classify ↔ Includes |
| `ProtocolVtableMembersInvariantTests` | 16-cell flag matrix: Includes ↔ emitted Swift struct fields (Defect F class) |
| `ObjCExistentialFailClosedTests` | nested @objc existential: Classify exclude + Build skip-but-consume + MemberGate lockstep |
| `ArtifactParityGate` Gate 3 | C# `*SwiftVTable` field stems vs Swift `{P}_vtable` field stems on generated artifacts |
| `ProtocolProxyEmitterTests.EmitProxyClass_ClosureSkippedMethod_OmitsVtableSlotEntirely` | C# omit field + skip-but-consume index for non-dispatchable closure |

---

## 4. Hunt results (summary table)

| Hunt question | Result |
|---------------|--------|
| Membership / index / skip-but-consume wrong in `VtableLayoutBuilder`? | **No defect found.** Rules match comments + unit pins; ctor/static/@objc-optional pre-skip; Self/generics/closures/ObjC-nested skip-but-consume. |
| Layout gated on projected key? | **Refuted.** Layout uses `GetMethodKey` only; Vtables.cs explicitly forbids projected collapse on layout. |
| ctor/static/@objc-optional wrongly consuming index? | **Refuted** (unit-tested). |
| Dual hand-allocators diverging from `VtableLayout`? | **L4 residual.** `EnumerateProtocolMethodsForDispatch` / `EnumerateIndexedSubscripts` still hand-allocate; index **rules match** layout today (constraints.md already flags as follow-up). |
| Compile-but-dead proxies | **Already-known** BindingAudit (Materials throw / RoomPlan silent delegate) + `SuppressedProxyReporting` taxonomy — residual product theme, not a new layout SSOT bug. |
| L3 empty/wrong proxy vs honest skip | Conformance ladder + suppressed-proxy degrade paths exist; still product-visible dead surface when proxy class missing — already-known. |
| L4 remaining hand-allocators | Enumerate* pair; plus **MethodEmitsVtableField** and **EmitMethodVtableField width** dual oracles (new findings below). |

---

## 5. Findings

### DA-W2-A5a-001: `MethodEmitsVtableField` drifted from layout membership (missing nested-@objc existential)

- **Severity**: P2  
- **Status**: confirmed  
- **Confidence**: high  
- **Lenses**: L4, L5, L1-latent  
- **Reachability**: latent (body path stubs nested-@objc methods and does not read the fan-out plan; fan-out only references fields when `Emit*Implementation` runs)  
- **Claim**: After nested-@objc-existential fail-closed landed on `VtableLayoutBuilder.ClassifyMethod` / `MemberGateEvaluator`, the older fan-out membership helper `MethodEmitsVtableField` was **not** updated. It still returns `true` for a method whose only exclusion is `ExcludedUnsupportedObjCExistential`, while `IncludedSlots` **omits** the field. Comments at `EveryProtocolEmitter.cs:1316–1320` and `:5292–5305` still claim this predicate is the same gate `EmitProtocolVtableStruct` uses for field emission — that is **false** today (struct walks `IncludedSlots`). Unit comment in `ProtocolProxyEmitterTests` still equates `MethodEmitsVtableField` with `IncludesMethod` after pre-skip; they diverge on nested @objc existential.  
- **Evidence**:  
  - `VtableLayout.cs:252–257` — ClassifyMethod returns `ExcludedUnsupportedObjCExistential`.  
  - `EveryProtocolEmitter.cs:5307–5320` — `MethodEmitsVtableField` checks closure / method-generics / Self / mixed-generic only; no ObjC-existential arm.  
  - `EveryProtocolEmitter.cs:772–789` — struct emits from `layout.IncludedSlots` only.  
  - `EveryProtocolEmitter.cs:1322–1323, 1374–1375` — fan-out branches filtered by `MethodEmitsVtableField`.  
  - `ObjCExistentialFailClosedTests` pins Classify/Build/MemberGate, **not** MethodEmitsVtableField parity.  
- **Probe**: Unit: assert `MethodEmitsVtableField(m, …) == ProtocolVtableMembers.IncludesMethod(m, …)` for nested-@objc method (should fail today). Or replace fan-out gate with `IncludesMethod` / `slot.Included` and re-run extension emission unit tests.  
- **Suggested fixture**: None required for the dual-oracle pin (synthetic decl unit test).  
- **Suggested simplification (L4)**: Delete `MethodEmitsVtableField` (or make it `IncludesMethod` after pre-skip filters) so fan-out and layout share one membership oracle. Risk class: behavior-preserving for all shapes where predicates already agreed; nested-@objc fan-out list shrinks to match layout (safer).  
- **Prior art**: ObjC existential fail-closed work; not listed as open dual-oracle on roadmap.

---

### DA-W2-A5a-002: Swift `EmitMethodVtableField` arity vs `VtableLayoutBuilder.GetWidth` (debug / empty-tuple skip)

- **Severity**: P2  
- **Status**: confirmed (dual-oracle existence); reachability **low**  
- **Confidence**: high on code divergence; medium on emission-live protocol methods carrying debug/empty-tuple params  
- **Lenses**: L1, L4  
- **Reachability**: latent / fixture-reachable only if a reverse-dispatch requirement retains a debug-default or `()` param on the ABI signature  
- **Claim**: `GetWidth` (drives C# `LocalVTable` delegate arity via `slot.Width`) **skips** `IsDebugParameter` and empty-tuple params. Swift `EmitMethodVtableField` walks **every** `CSSignature` param after self and always adds at least one `UnsafeRawPointer` — it does **not** skip those shapes. Real-async witness bodies correctly skip debug/empty when building call args (`:4559`), so a divergent slot type would be a Swift/C# `@convention(c)` arity mismatch if such a param ever appears on an **Included** reverse-dispatch method.  
- **Evidence**:  
  - `VtableLayout.cs:329–337` — GetWidth skip + `CountVtableSlots`.  
  - `EveryProtocolEmitter.cs:2843–2860` — no debug/empty skip in field type builder.  
  - `ProtocolProxyEmitter.Vtables.cs:225–244` — LocalVTable uses `slotCount` from layout width.  
- **Probe**: Synthetic protocol method with a trailing debug-named default or empty-tuple param; emit Swift field type arity vs `layout.IncludedMethods.Single().Width`.  
- **Suggested fixture**: Only if a real ABI JSON shows debug params surviving on protocol requirements (likely rare).  
- **Suggested simplification (L4)**: Make `EmitMethodVtableField` call the same width/slot-type builder as `GetWidth` / `CountVtableSlots` (one function returning both count and per-slot Swift types). Risk: byte-identical for ordinary methods; behavior-preserving fix if a debug-param protocol ever appears.  
- **Prior art**: none specific; same dual-oracle class as Wave 1 cdecl phase tables.

---

### DA-W2-A5a-003: Hand-allocators `EnumerateProtocolMethodsForDispatch` / `EnumerateIndexedSubscripts` still parallel to `VtableLayout`

- **Severity**: P2 (L4)  
- **Status**: confirmed (existence); **refuted** as currently diverging on index rules  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: integrity-gate / maintainability — index rules currently match; future edit risk  
- **Claim**: constraints.md already records these two enumerators as **still hand-allocating** the reverse index axis (closure-thunk naming / fan-out plan helpers). Spot-check: both pre-skip the same three method categories; both assign sequential indices for first-seen raw keys; subscript enum skips static and consumes for all other instance subscripts (including skip-but-consume shapes). No index skew found vs `Build()`. Residual risk is **re-divergence on edit**, not a live Bug #21.  
- **Evidence**:  
  - `EveryProtocolEmitter.cs:1062–1071`, `:5756–5774`.  
  - `VtableLayout.cs:159–221`.  
  - constraints.md “Still hand-allocating… EnumerateProtocolMethodsForDispatch and EnumerateIndexedSubscripts”.  
- **Probe**: Property test: for random method/subscript flag grids, `Enumerate*.Index == layout.MethodSlotIndexByKey[key]` / subscript SlotIndex.  
- **Suggested simplification**: Yield from `VtableLayout` slots (or `MethodSlotIndexByKey` + first Included/unique key walk) instead of a second counter. Risk class: behavior-preserving with unit parity fixture.  
- **Prior art**: constraints.md; Finding-8 / Bug #21 overhaul notes.

---

### DA-W2-A5a-004: Stale SSOT comments on field-emission membership

- **Severity**: P3  
- **Status**: confirmed  
- **Confidence**: high  
- **Lenses**: L5  
- **Reachability**: integrity-gate (AI/human edit hazard)  
- **Claim**: Multiple comments still name `MethodEmitsVtableField` as the field-emission oracle shared with `EmitProtocolVtableStruct`. Post-VtableLayout migration, the field oracle is **`slot.Included` / `IncludedSlots`**. Leaving the old name in place invites “fix the SSOT” edits on the wrong function (exactly how DA-W2-A5a-001 survived).  
- **Evidence**: `EveryProtocolEmitter.cs:1316–1320`, `:5292–5305`; `ProtocolProxyEmitterTests.cs:5419–5420`.  
- **Probe**: Doc/comment audit only.  
- **Prior art**: none.

---

### DA-W2-A5a-005: Compile-but-dead reverse-dispatch surface (suppressed proxy / partial stub)

- **Severity**: P1 (product impact when hit)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: emission-live (BindingAudit RealityFoundation Materials, RoomPlan view delegate, et al.)  
- **Claim**: When EveryProtocol conformance or proxy synthesis is skipped, C# still emits interfaces/proxies that **throw** (produce-throw), **silently no-op callbacks** (consume-degraded), or **fail-fast receivers** while **retaining** vtable slots for layout parity. Layout SSOT is not the root cause; product degradation honesty is. Reporting improved via `SuppressedProxyReporting` (`produce-throw` / `consume-degraded` / `receiver-failfast`) — residual is consumer-visible dead API, not slot-shift.  
- **Evidence**: BindingAudit `_SUMMARY.md`; `SuppressedProxyReporting.cs:1–65`; Entity-inherited forward-only path `EveryProtocolEmitter.cs:2663–2674`.  
- **Prior art**: BA-SUM; BSA-05 P0 EveryProtocol; do not re-chase as new layout P0.

---

### DA-W2-A5a-006: Inout ObjC-bridgeable reverse-dispatch retains dead vtable slot

- **Severity**: P3 (cosmetic dead slot)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3, L4  
- **Reachability**: fixture-reachable (unit pin exists); rare product shape  
- **Claim**: Roadmap medium item: trap stub keeps slot deliberately so Swift/C# buffers stay lockstep. `ClassifyMethod` does **not** exclude this shape (Included=true); extension emits `EmitInOutObjCBridgeableMethodStub`. Pattern-inconsistent with other stub categories but **not** a slot-shift bug.  
- **Evidence**: `roadmap.md` medium row; `EveryProtocolEmitter.cs:5297–5299`, tests `EmitProtocolVtableStruct_InOutObjCBridgeableParam_RetainsVtableSlot`.  
- **Prior art**: roadmap — do not re-file as novel.

---

### DA-W2-A5a-007: Property non-requirement exclusion (Defect F) — fixed

- **Severity**: n/a  
- **Status**: refuted (as open defect) / already-known historical  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Design doc Defect F described struct vs IncludesProperty divergence on `!IsProtocolRequirement`. **Current code:** `ClassifyProperty` returns `ExcludedNonRequirement` when `!IsProtocolRequirement` (`VtableLayout.cs:270–271`); struct walks IncludedSlots; invariant matrix pins agreement. Method path intentionally does **not** filter `IsProtocolRequirement` (design doc method-side check) — all three method walks agree.  
- **Evidence**: `VtableLayout.cs:264–290`; `ProtocolVtableMembersInvariantTests`; `reverse-dispatch-lifetime.md:370–423`.  
- **Prior art**: Defect F closed at emitter layer.

---

### DA-W2-A5a-008: Layout never collapses on projected C# key

- **Severity**: n/a  
- **Status**: refuted (as defect)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: No layout walk gates membership or index on AF05 projected keys. Overload-collapse **fill** bugs were fillability-side (roadmap FIXED for orphan receivers); surplus **slots** for raw-distinct existentials that project to one C# method remain intentional layout behavior (slot kept, fill may leave null).  
- **Evidence**: `VtableLayout.cs:64–67, 317–322`; `ProtocolProxyEmitter.Vtables.cs:40–44`; roadmap FIXED overload-collapse entry.  
- **Prior art**: FIXED roadmap row; A5b owns residual fillability.

---

## 6. Dual-oracle / hand-allocator inventory (A5a)

| Oracle A | Oracle B | Agreement today? | Finding |
|----------|----------|------------------|---------|
| `VtableLayoutBuilder.Classify*` | `ProtocolVtableMembers.Includes*` | Yes (delegate) | — |
| `IncludedSlots` walks (Swift + 2× C#) | each other | Yes (same model) | — |
| `GetMethodKey` / `GetSlotKey` | projected key | **Must differ** by design | DA-W2-A5a-008 |
| `MethodEmitsVtableField` | `IncludesMethod` / Included | **No** on nested @objc existential | **001** |
| `EmitMethodVtableField` arity | `GetWidth` | **No** on debug/empty-tuple | **002** |
| `EnumerateProtocolMethodsForDispatch` | `MethodSlotIndexByKey` | Yes (index rules) | **003** L4 only |
| `EnumerateIndexedSubscripts` | subscript SlotIndex | Yes | **003** L4 only |
| `WitnessDispatchEmitter.GetMethodKey` | reverse `GetMethodKey` | Intentional different axis | out of A5a |

---

## 7. L3 graceful degradation (layout-adjacent)

| Pattern | Behavior | Verdict |
|---------|----------|---------|
| Unsupported reverse member shape | Skip field + skip-but-consume index + Swift fatalError stub | Correct degrade for layout integrity |
| Nested @objc existential | Interface drop + slot drop (skip-but-consume) + stub | Fail-closed lockstep (good) |
| Full EveryProtocol skip | No SetVtable; proxy suppressed / forward-only; members throw or silent | **Compile-but-dead** — already-known product theme |
| Inout ObjC-bridgeable | Keep dead slot + trap stub | Cosmetic dead surface (roadmap) |
| Fan-out references missing field | Guarded by MethodEmitsVtableField (stale) | Residual dual-oracle; body stubs protect nested-@objc today |

---

## 8. Test honesty notes (L2)

| Strength | Gap |
|----------|-----|
| Strong unit coverage of index semantics + ObjC lockstep + flag matrix | No pin that `MethodEmitsVtableField ≡ IncludesMethod` |
| ArtifactParity Gate 3 field **names** | Does not assert per-field **function-pointer arity** (width) |
| Real-async +3 width unit | Debug/empty-tuple width dual-oracle untested |
| Closure skip-but-consume proxy pin | Good end-to-end of omit-field philosophy |

---

## 9. Recommended backlog seeds (not implementing)

| Priority | Item | Status |
|----------|------|--------|
| Medium L4 | Collapse `MethodEmitsVtableField` → `IncludesMethod` (+ unit lockstep for nested @objc) | confirmed **001** |
| Medium L4 | Single width/arity builder shared by Swift field + `GetWidth` | confirmed **002** |
| Low L4 | Route Enumerate* through `VtableLayout` | confirmed **003** / constraints |
| Low L5 | Rewrite stale MethodEmitsVtableField SSOT comments | confirmed **004** |
| Watch | Suppressed-proxy consumer UX (G1 / Wave 7) | already-known **005** |
| Do not chase as new | Materials/RoomPlan; overload-collapse FIXED; Defect F fixed; inout dead slot | already-known |

---

## 10. Bottom line

| Metric | Value |
|--------|------:|
| **Risk** | **2 / 5** |
| **#confirmed** (new defects / dual oracles) | **4** (001–004; 003 is L4 residual with matching rules) |
| **#candidate** | **0** open candidates (hollow-vtable edge for all-@objc-optional not elevated) |
| **#already-known / refuted useful** | **005–008** |
| **Headline** | **Reverse-dispatch layout SSOT (`VtableLayout` + three IncludedSlots walks) is sound and well-pinned; residual risk is fan-out/width dual oracles + hand-enumerators, not projected-key slot shift.** |

Wave 2 A5b should treat fillability filters (`_skippedMethodKeys`, projected-key receiver dedup, async CT) as the next crash-class surface; A5a does not need to re-open layout membership rules without a new emission site.
