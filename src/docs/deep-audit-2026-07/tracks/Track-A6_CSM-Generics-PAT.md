# Track A6 — Concrete specialization / generics / PAT

| Field | Value |
|-------|--------|
| **Wave** | 3 |
| **Track** | A6 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (crash-class CSM result/Self paths largely closed; residual = undercount filters + dual-path hygiene + already-known multi-PAT / multi-parent caps) |
| **Confidence** | **high** on fixed crash-class items (carrier wrap, alloc/free, Self sub, multi-constraint intersection) with BindingTests; **medium** on residual SameType sugar / method-where composition reachability |
| **Lenses** | L1 (result ownership, Self, arity), L2 (fixture honesty), L3 (engine reject vs swiftc fail), L4 (sync/async bridge dual helpers), L5 (roadmap drift) |

## Scope (A6 only)

**In:** Concrete Specialization Mechanism (CSM) engine + emitter, bound-generics translation, ConformanceGraph, PInvokeHelper metadata/PWT, method-generic bridges (light), multi-PAT boxing, BindingTests Generics + unit `ConcreteSpecialization*` coverage.

**Out:** Full reverse-dispatch (A5*), TypeDB/projection SSOT (M3), async harness merge proposals (rejected by roadmap), pure protocol vtable layout.

---

## 1. Method

1. Read methodology, codebase map, prior-art, Wave 2 synthesis, roadmap CSM medium/latent rows.  
2. Deep-read `ConcreteSpecializationEngine`, `ConcreteProtocolSpecializationEmitter*`, `BoundGenericsHandler`, `ConformanceGraph`, `MethodGenericBridgeEmitter` / `AsyncMethodGenericBridgeEmitter` (eligibility + result path), `PInvokeHelperEmitter` (light), multi-PAT path in `TypeHandlerHelpers`.  
3. Cross-check BindingTests Generics (composition, class-conformer return, dependent-member, CSM generic parent) + unit `ConcreteSpecializationEngineTests` / `BoundGenericsHandlerTests`.  
4. Tag roadmap rows as **already-known**, **fixed-in-code (roadmap stale)**, or **confirmed residual**. Prefer under-claim for NEW findings.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs` | Conformer index, multi-constraint intersect, ParentTuple method-where filter, SameType / dependent-member |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` | Sync emit, Self sub, resultPtr ownership, class carrier, CanEmit preflight, generic-parent extensions |
| `…ConcreteProtocolSpecializationEmitter.{Sync,Async,AsyncGenericParent}.cs` | Parent-only async, extension routing |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodGenericBridgeEmitter.cs` | Open-generic method bridge + result ownership |
| `…AsyncMethodGenericBridgeEmitter.cs` | Parallel eligibility (L4 dual path) |
| `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` | C# name translation, nested arity, constraint validation |
| `src/Swift.Bindings/src/TypeDatabase/ConformanceGraph.cs` | TypeWitness store for associated types |
| `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` | ParseSignature / requirement model (Finding 19) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` | Metadata + PWT flatten (light) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` | Multi-PAT `typeof(object)` guard |
| `src/Swift.Bindings/src/Marshaler/RouteCSortShapeEligibility.cs` | Multi-generic-parent gate |
| `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs` | `BuildSwiftCallArgLabel` (empty external `_`) |
| BindingTests `RuntimeTestsApp/Generics/*` | Composition, class carrier, dependent-member, CSM parent |
| Unit `ConcreteSpecializationEngineTests`, `BoundGenericsHandlerTests`, bridge emitter tests | Pins |

---

## 3. Architecture inventory (current)

### 3.1 CSM pipeline

```
IndexModuleConformances (+ hints JSON)
  → FindSpecializableMethods / ResolveParentSpecializableParams
      → multi-constraint intersect (ConformerSatisfiesAllConstraints / F20 fail-closed)
      → couplings + associated-type filters
  → ConcreteProtocolSpecializationEmitter
      → CanEmitConcreteOverloadForPairing (ISwiftObject / UUID / Self / optional / …)
      → ParentTupleSatisfiesMethodConstraints (per-method where)
      → Emit Swift @_cdecl + C# CallConvCdecl overload (or *CsmExtensions for generic parent)
```

Open-generic **MethodGenericBridge** is a **sibling** path: class-bound single-protocol params without PAT/Self, **not** CSM closed overloads. Multi-protocol param → bridge rejects (`protocolConformances.Count != 1`); CSM intersects.

### 3.2 Dual paths that must agree (intentional vs hazard)

| Pair | Intent | Hazard |
|------|--------|--------|
| CSM resultPtr ownership vs MethodGenericBridge ownership | Both three-way NewFromPayload (direct-wrap / destroy-wire / free) | Drift if one gains a fourth arm and the other doesn't |
| CSM class carrier (`MarshalOwnedClassFromSlot`) | CSM only (`returnsGenericParam`); bridge rejects generic returns | OK if documented |
| MethodGenericBridge vs AsyncMethodGenericBridge eligibility | Parallel copies of `FindEligibleGenericParam` / Self-PAT checks | L4 edit hazard |
| `GenericTypeEmitter.GetWhereClause` ISwiftObject seed vs `PInvokeHelperEmitter` resolvable | Must mirror PAT/Self/method-Self flags | constraints.md trap; not re-audited line-by-line here |
| CollectAllProtocolConstraints (param GenericConformances) vs ParseMethodLevelConstraints (RawGenericSig Target) | Different sources for multi-constraint | Composition split on param path; method-where may still see opaque `P & Q` Target |

### 3.3 L3 posture (engine reject vs swiftc)

| Gate | Role |
|------|------|
| `ConformerSatisfiesAllConstraints` / F20 | Engine-side reject unprovable secondary constraints |
| `ParentTupleSatisfiesMethodConstraints` | Engine-side reject illegal parent×method pairings (report `csmConformerRejections`) |
| `CanEmitConcreteOverloadForPairing` | Engine-side reject unmarshallable shapes |
| `DoesPairingSatisfyAssociatedTypeConstraints` | Engine-side assoc floors |
| Wrapper strip / co-gater | Defense-in-depth if something still emits bad Swift |

Major historically emit-then-swiftc classes (wrong where, Self bare, bad result free) have **engine-side** or **ownership-correct** fixes. Residual undercount/false-reject is L3 under-emit, not hard package fail.

---

## 4. Hunt results (summary table)

| Hunt question | Result |
|---------------|--------|
| Wrong generic arity (CS0305) | **Partial FIXED + residual already-known.** Nested InnerType args + Self bare-parent close + `ResolvePublicCSharpType` recurse. Roadmap CS0305 validate reds still **already-known** until re-probed; no new emission site proven here. |
| Self not substituted | **FIXED** (`SubstituteSelfAndPairingGenericsInTypeSpec` + unit tests). Roadmap CS0246 Self row **stale** unless a non-CSM path still leaks bare Self. |
| SameType sugar `Data?` vs Optional | **Partial FIXED.** Dependent-member SameType uses `NormalizeTypeForComparison`. **Direct** SameType branch still raw `string.Equals` + SwiftLiteral only — residual undercount risk. |
| Protocol composition `T:P&Q` opaque | **FIXED on primary path** (param GenericConformances + intersection + BindingTests `CompositionMethodConstraintTests`). Residual: method-where `RawGenericSig` Target may still be a single composite string. |
| Dependent-member where skipped | **FIXED single-hop** via `DependentMemberClauseSatisfied` + AssosiatedTypeConformances; multi-hop fail-closed (engine skip, not silent emit). |
| MethodGenericBridge resultPtr alloc/free | **FIXED** (NativeMemory.Alloc ownership-transfer vs AllocHGlobal+free + wire destroy). Roadmap latent **stale**. |
| Class-conformer `returnsGenericParam` carrier | **FIXED** (`MarshalOwnedClassFromSlot` + BindingTests `CsmClassConformerReturnTests`). Roadmap latent **stale**. |
| Multi-PAT boxing | **Already-known** intentional skip of `IExistentialBoxable` / `typeof(object)` when `CountPatConformances > 1` → explicit `InvalidCastException`. |
| Emit-then-swiftc vs engine reject | **Mostly engine-side now** for CSM pairings; residual undercount > false-emit. |
| Dual paths that must agree | Documented above; **L4** shared eligibility helpers for method-generic bridges. |
| Availability on CSM extension **class** header | Methods get `[SupportedOSPlatform]`; class still bare — **partial** residual of roadmap availability row (method-level likely sufficient for CA1416). |
| Multi-generic-parent MusicKit | **Already-known** low-pri (`RouteC` + filter gate single parent generic). |
| CS0315 / Guid `GetSwiftTypeSize` | **Fail-closed** via `IsISwiftObject` allowlist for UUID; Data admitted. Residual frozen value structs admitted only when `ProjectsAsBlittableValueStruct`. |
| Argument label `name(: arg)` | **FIXED** (`BuildSwiftCallArgLabel` returns `""` for `_` / empty). |
| CS0311 / Foundation missing type | **Already-known** validate reds; no new probe this track. |

---

## 5. Findings

### DA-W3-A6-001: Direct SameType method-where filter still lacks sugar normalization

- **Severity**: P2  
- **Status**: confirmed (code divergence); reachability **low/fixture** (validate emit counts went *up* after Commit C; no current BindingTests red)  
- **Confidence**: high on code; medium on live bite  
- **Lenses**: L3 (silent undercount), L5 (roadmap partial residual)  
- **Reachability**: fixture-reachable  
- **Claim**: `ParentTupleSatisfiesMethodConstraints` **direct** SameType arm (`memberPath` empty) still compares `SwiftQualifiedName` / `SwiftLiteral` with raw `string.Equals`. The dependent-member arm **does** call `NormalizeTypeForComparison` (TypeSpecParser round-trip). A method where `τ_0_0 == Data?` against conformer `Foundation.Data` / canonical Optional form can still false-reject.  
- **Evidence**:  
  - `ConcreteSpecializationEngine.cs:1423–1434` — direct SameType without normalize.  
  - `ConcreteSpecializationEngine.cs:1509–1516` — dependent-member SameType with `NormalizeTypeForComparison`.  
  - `NormalizeTypeForComparison` at `:1529–1545`.  
  - Roadmap medium row "CSM per-method SameType filter: no sugar canonicalization" — **partially outdated** (dep-member fixed; bare-param residual remains).  
- **Probe**: Unit: parent method `where τ_0_0 == Data?` (or `Swift.Optional<Foundation.Data>`) vs conformer printed as the other form → assert admit.  
- **Suggested fixture**: `DataResponsePublisher`-style `Value == Data?` with ABI-sugared RHS.  
- **Prior art**: roadmap SameType sugar row.

---

### DA-W3-A6-002: Method-where composition Target may still be opaque in `ParseSignature`

- **Severity**: P2  
- **Status**: candidate (primary multi-constraint path is fixed; residual only if digester emits single RHS with `&`)  
- **Confidence**: medium  
- **Lenses**: L3, L5  
- **Reachability**: fixture-reachable / latent  
- **Claim**: Param-level multi-constraint is **fixed**: ABI fills multiple `GenericConformances`; `CollectAllProtocolConstraints` + F20 intersection; BindingTests `CompositionMethodConstraintTests` prove live emit+runtime for `T: P & Q`. The **separate** method-where path stores `GenericRequirement.Target` verbatim. If a method-level clause is still a single string `Module.P & Module.Q`, `IsKnownConformerOfConstraint(..., FromModuleQualifiedName(target))` mis-parses (split on `.` → wrong Module/Name) and can false-reject or throw.  
- **Evidence**:  
  - Fixed path: `ConcreteSpecializationEngine.cs:798–816`, tests `FindSpecializableMethods_MultiConstraintParam_*`, BindingTests composition.  
  - Residual path: `ParseMethodLevelConstraints` → `ParseSignature` Target verbatim (`GenericSignatureParser.cs:264–270`); use at `:1374–1390` with `FromModuleQualifiedName(target)`.  
  - `SwiftTypeName.FromModuleQualifiedName` rejects `<` but accepts `&` as part of dotted path (`SwiftTypeName.cs:42–57`).  
- **Probe**: Construct method RawGenericSig with single composite conformance Target; run `ParentTupleSatisfiesMethodConstraints`.  
- **Prior art**: roadmap composition opaque-target row — **mark primary fixed, residual method-where only**.

---

### DA-W3-A6-003: Dual eligibility helpers — MethodGenericBridge vs AsyncMethodGenericBridge

- **Severity**: P2 (maintainability / dual-oracle)  
- **Status**: simplification  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: integrity-gate (edit hazard)  
- **Claim**: Sync and async method-generic bridges each own copies of `FindEligibleGenericParam`, `HasSelfOrAssociatedTypeRequirements`, `AreNonGenericParamsCompatible` (and related gates). Roadmap already rejected full async-emitter merge; extracting **exact** eligibility helpers is the safe L4 survivor class.  
- **Evidence**:  
  - `MethodGenericBridgeEmitter.cs:202+` and `AsyncMethodGenericBridgeEmitter.cs:222+` parallel records/helpers.  
  - Comments claim structural identity.  
- **Suggested simplification**: Shared static helper type for eligibility only (not emission). Risk: behavior-preserving if byte-identical today.  
- **Prior art**: roadmap "async-emitter consolidation: investigated, not pursued" — Tier-1 exact-duplicate extraction allowed.

---

### DA-W3-A6-004: Multi-PAT boxing still keys single-PAT on `typeof(object)`

- **Severity**: P2 (product limitation)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L1 (wrong PWT if unguarded), L3 (fail loud)  
- **Reachability**: emission-live rare  
- **Claim**: Single-PAT conformers register `IExistentialBoxable` + dictionary entry `{typeof(object), descriptor}`. Multi-PAT skips boxing entirely so call sites throw `InvalidCastException` rather than pick the wrong PWT. Correct fail-closed product posture; not a new defect.  
- **Evidence**: `TypeHandlerHelpers.cs:1227–1236`, `:1375–1393`, `:1455+` `CountPatConformances`.  
- **Prior art**: roadmap medium "Multi-PAT existential boxing".

---

### DA-W3-A6-005: Multi-generic-parent CSM / Route C still capped at one parent param

- **Severity**: P2 (surface gap)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3 (empty / tombstone surface)  
- **Reachability**: emission-live (MusicKit sectioned request)  
- **Claim**: `RouteCSortShapeEligibility` requires `GenericParameters.Count == 1`. MusicKit `MusicLibrarySectionedRequest<Section, Item>` remains 0/17 members. Engine can cartesian multi-parent params in principle (`CartesianPairings`) but per-method filters + Route C do not complete the product surface.  
- **Evidence**: `RouteCSortShapeEligibility.cs:76–81`; roadmap low-pri MusicLibrarySectionedRequest row.  
- **Prior art**: roadmap.

---

### DA-W3-A6-006: Extension class header omits platform attributes (methods have them)

- **Severity**: P3  
- **Status**: already-known (partial residual)  
- **Confidence**: high on code; medium on consumer impact  
- **Lenses**: L3 / CA1416 ergonomics  
- **Reachability**: emission-live  
- **Claim**: `EmitConcreteSpecializationsForGenericParent` opens `public static unsafe partial class {Type}{Conformer}CsmExtensions` with **no** `[SupportedOSPlatform]`. Per-method emission merges conformer availability and emits attributes on P/Invoke + public method (`EmitSupportedOSPlatformsFromAnnotations`). Index tests pin conformer availability collection. Residual is class-level only.  
- **Evidence**: `ConcreteProtocolSpecializationEmitter.cs:3490–3491` vs `:1614–1636`; engine tests `IndexModuleConformances_PropagatesAvailabilityToConformers`.  
- **Prior art**: roadmap availability floor row (partially closed at method level).

---

### DA-W3-A6-007: BoundGenericsHandler `Math.Min` arity when validating constraints

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L1/L3  
- **Reachability**: latent  
- **Claim**: `TryValidateGenericTypeConstraints` iterates `Math.Min(declArity, boundArity)` only. Extra bound args or extra decl params beyond the min are not flagged as shape mismatch here — CS0305 can still appear later at emit/compile if a different path renders wrong arity. Nested placement fix addresses a known CS0305 class; this min-loop does not itself emit C# type args.  
- **Evidence**: `BoundGenericsHandler.cs:1344–1365`. Nested CS0305 fix at `:1166–1187` + unit regression.  
- **Probe**: Bound type with arity mismatch that still passes other gates.  
- **Prior art**: roadmap CS0305 unknown root cause — do not claim this is *the* root without validate repro.

---

## 6. Roadmap CSM rows — status re-tag (Wave 3)

| Roadmap item | Wave 3 status |
|--------------|---------------|
| CS0305 generic-argument shape mismatch | **already-known** residual; several sites fixed; no new confirmed emission root |
| CS0234 missing Foundation type | **already-known** (TypeDB / emission surface) |
| CS0246 unresolved Self | **fixed-in-code** for CSM (`SubstituteSelf*`); re-open only with non-CSM Self leak |
| CS0315 value-type / ISwiftObject GetSwiftTypeSize | **mitigated** (reject UUID; admit Data / blittable ISwiftObject structs) |
| @available floor on extension **class** | **partial** — methods annotated; class header bare |
| CSM call site `name(: arg)` | **fixed-in-code** (`BuildSwiftCallArgLabel`) |
| CS0311 FastDatabaseValueCursor interfaces | **already-known** |
| Method-where `P & Q` opaque | **primary fixed**; residual candidate on RawGenericSig Target only |
| SameType sugar Data? | **partial** — dep-member normalized; direct branch not |
| Dependent-member clauses skipped | **fixed** single-hop evaluate; multi-hop fail-closed |
| MethodGenericBridge alloc+free antipattern | **fixed-in-code** |
| Class-conformer returnsGenericParam carrier | **fixed-in-code** + BindingTests |
| Multi-PAT boxing | **already-known** intentional |
| Multi-generic-parent / MusicLibrarySectioned | **already-known** low-pri |
| Cross-module conformer enumeration | **already-known** low-pri |
| Multi-protocol compositions (existential) | **already-known** blocked low-pri (distinct from CSM multi-constraint) |

---

## 7. Test landscape (L2 notes)

**Strong:**  
- BindingTests: composition multi-constraint, class-conformer carrier UAF/leak, dependent-member property drop, CSM generic parent family (`CsmGenericParent*`, `PatParent*`, DataProtocol, KeyPath).  
- Units: multi-constraint F20, ParentTuple SameType coupling shapes, Self substitution CS0305 bare-parent, availability index, BoundGenerics nested InnerType CS0305.

**Gaps:**  
- No unit pin that **direct** SameType uses sugar normalize (asymmetry with dep-member).  
- No unit for method-where composite `P & Q` Target string.  
- MethodGenericBridge ownership discrimination unit-covered; class-carrier is CSM-only (correct).  
- Dual bridge eligibility not cross-asserted equal.

---

## 8. L4 simplification catalog (do not implement in audit)

| ID | Shape | Risk class | Do not do if… |
|----|-------|------------|---------------|
| S-A6-1 | Extract shared method-generic eligibility helpers | behavior-preserving | emission bodies start to diverge on purpose |
| S-A6-2 | Call `NormalizeTypeForComparison` on direct SameType arm | behavior-preserving / more admits | sugar normalize maps distinct types together (validate with fixtures) |
| S-A6-3 | Split composition Target on `&` in ParseMethodLevelConstraints | behavior-preserving / more admits | digester never emits composite Target (dead) |

Do **not** merge CSM closed-emit with MethodGenericBridge open-existential path (different jobs).  
Do **not** re-propose full async emitter merge (roadmap rejected).

---

## 9. Counts & headline

| Metric | Count |
|--------|------:|
| Findings total | **7** |
| NEW confirmed (code residual not fully closed) | **1** (A6-001 direct SameType sugar) |
| NEW candidate | **2** (A6-002 method-where composition; A6-007 Math.Min arity) |
| Simplification (L4) | **1** (A6-003 dual bridge eligibility) |
| Already-known / intentional | **3** (A6-004 multi-PAT; A6-005 multi-parent; A6-006 avail class header residual) |
| New live P0 | **0** |
| Roadmap rows re-tagged fixed-in-code (stale) | **≥5** (Self, labels, carrier, bridge free, primary composition) |

### Headline

**CSM crash-class regressions (carrier wrap, result free, bare Self, multi-constraint emit-then-swiftc) are largely closed and BindingTests-pinned.** Residual risk is **undercount** (direct SameType sugar; possible method-where composition Target) and **product caps** (multi-PAT, multi-generic-parent), not silent wrong-ABI on the main closed-specialization path. **Risk 2/5; 0 new live P0.**

---

## 10. Wave 3 handoff notes

- M3 (TypeDB/projection) should own CS0234 / CS0311 / interface-list completeness if reopened.  
- G1 graceful-degradation: CSM rejection report (`RejectedPairings`) is a good partial-binding story — keep fail-closed engine rejects over wrapper-compile failure.  
- Do not re-chase MethodGenericBridge double-free or class-carrier without a regression fixture proving reintroduction.
)
