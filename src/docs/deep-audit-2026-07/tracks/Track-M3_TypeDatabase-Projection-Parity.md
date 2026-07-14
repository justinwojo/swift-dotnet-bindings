# Track M3 — TypeDatabase / Projection Parity

| Field | Value |
|-------|--------|
| **Wave** | 3 |
| **Track** | M3 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (parity traps mostly closed by shared predicates; residual dual-oracle surface + intentional degrade paths, no emission-live wrong-ABI found in code review) |
| **Confidence** | **high** on shared-predicate F15 fix, visitor exhaustiveness, seed-drop mirror, XML enum hygiene sample; **medium** on unprobed reverse-dispatch ObjCRooted setter arm and corpus-wide AnyType counts |
| **Lenses** | L1 (projection ABI shape), L3 (AnyType / object degrade), L4 (projection-only marshaler deferred), L5 (dual-oracle maintainability) |

## Scope

Type resolution and type projection parity across:

1. `TypeDatabase` + `TypeDatabaseExtensions` + `TypeResolver` strategy chain  
2. `TypeProjectionFactory` + all `ITypeProjection` impls  
3. `AppleFrameworkRegistry` + `apple-frameworks.json` + Runtime `*Database.xml` stubs  
4. `IsOptionalObjCBridged` / `IsObjCPrefixBridgeCandidate` vs factory/wrapper optional reference oracles  
5. `IProjectionVisitor` exhaustiveness  
6. `GetWhereClause` ISwiftObject seed-drop vs `PInvokeHelperEmitter` `isResolvable`  
7. Conceptual AnyTypeFallback / L3 public-surface honesty (no full `nuke validate`)

---

## 1. Method

1. Read methodology, codebase map, prior-art index, and constraints.md projection traps.  
2. Inventory TypeDatabase, resolver strategies, factory, projections, visitors, registry JSON, Runtime XML.  
3. Cross-check dual readers that constraints.md claims must agree.  
4. Sample XML `kind=` usage for enum-vs-struct mistakes.  
5. Tag already-known roadmap / post-1.0 rows; file only residual or newly confirmed items.

### Primary files read

| Area | Paths |
|------|--------|
| TypeDB core | `TypeDatabase.cs`, `TypeDatabaseExtensions.cs`, `TypeRecord.cs`, `ModuleDatabase.cs` |
| Registry | `AppleFrameworkRegistry.cs`, `Data/apple-frameworks.json` |
| Resolver | `TypeResolver.cs` + strategies under `TypeDatabase/Resolver/Strategies/` |
| Factory / projections | `TypeProjectionFactory.cs`, all 24 `ITypeProjection` impls, `IProjectionVisitor.cs`, `ITypeProjection.cs` |
| Parity oracles | `MarshallingHelpers.IsOptionalObjCBridged` / `IsObjCPrefixBridgeCandidate`, `WrapperValidation.IsOptionalWithReferenceInner` |
| Visitors | `AccessorConversionVisitors.cs`, `ReceiverConversionVisitors.cs` |
| Seed-drop | `GenericTypeEmitter.GetWhereClause`, `PInvokeHelperEmitter` `isResolvable`, `MetatypeHelperEmitter` |
| XML stubs | `src/Swift.Runtime/src/Swift/*Database.xml` (enum sample + Foundation/UIKit/CG) |
| Prior art | roadmap AnyTypeFallback row, post-1.0 projection-only marshaler, Design/binding-typedatabase.md |

---

## 2. Architecture inventory

### 2.1 Type resolution (single seam)

`TypeDatabaseExtensions.TryGetTypeRecord` / `GetTypeRecordOrAnyType` / `IsTypeProcessed` / `TryGetAnyTypeFallbackInfo` all project through **`TypeResolver.Default`** — ordered strategies, first match wins.

**Default strategy order** (`TypeResolver.cs:85–111`):

| # | Strategy | Role |
|---|----------|------|
| 1 | `DynamicSelfStrategy` | `Self` |
| 2 | `GenericParameterStrategy` | `τ_*` / generic params |
| 3 | `PrimitiveAliasStrategy` | Foundation aliases → primitives |
| 4 | `MetatypeStrategy` | `.Type` chains (before existential heuristic) |
| 5 | `ExistentialStrategy` | `any` + synthetic fallback tag |
| 6 | `SwiftAnyAnyObjectStrategy` | intentional Any / AnyObject resolution |
| 7 | `PointerStrategy` | IntPtr mapping |
| 8 | `UnsupportedAppleModuleStrategy` | SwiftUI/Combine-class modules |
| 9 | `BareGenericGuardStrategy` | bare container names |
| 10 | `BoundGenericSimdAliasStrategy` | SIMD2/3/4 → simd.simd_float* |
| 11 | `AppleSupplementStrategy` | hand-rolled supplement identities |
| 12–15 | **DatabaseCascade** | module DB, out-of-module, cross-module alias, Swift.Error |
| 16 | `ObjCBridgingStrategy` | synthetic ObjCBridged Class records (last resort) |

**F10 Stage 18 note (good):** raw `SwiftTypeName` callers use `DatabaseCascade` only; `NamedTypeSpec` entry points use the full chain. Comments document that this closed prior dual-universe drift (`IsTypeProcessed` vs `TryGetTypeRecord` on supplements).

### 2.2 Type projection inventory (24 kinds)

| Projection | Typical PublicType | P/Invoke / wire |
|------------|--------------------|-----------------|
| `BlittableProjection` | primitives / frozen POD / generic params | same / direct |
| `BoolProjection` | `bool` | U1 |
| `StringProjection` | `string` | `SwiftString` / buffer |
| `SimpleEnumProjection` | C# enum | raw int |
| `ClassProjection` | Swift class | IntPtr / SafeHandle payload |
| `ObjCRootedClassProjection` | NSObject-rooted Swift class | Handle + stackalloc buffer param |
| `ObjCBridgedProjection` | ObjC class (UIImage…) | IntPtr Handle |
| `ObjCBridgeableProjection` | Swift value ↔ NS* (URL) | IntPtr bridge |
| `NativeRemappedProjection` | remapped native wrappers | SafeHandle native |
| `NonFrozenStructProjection` | resilient / complex enum | SafeHandle / MarshalFromSwift |
| `FrozenWithMemoryProjection` | frozen + ARC fields | `.Buffer` by value |
| `Array` / `Dictionary` / `Set` | idiomatic collections | SwiftArray/Dict/Set or ObjC NS* bridge |
| `OptionalProjection` | `T?` | SwiftOptional or nullable IntPtr |
| `TupleProjection` | ValueTuple / multi-element | per-element |
| `ClosureProjection` | Action/Func | callback trampoline |
| `AsyncProjection` | Task / Task\<T\> | callback wrap |
| `ExistentialProjection` | interface / object / union | ExistentialContainerN or ObjC ptr |
| `DataProjection` | `byte[]` | Foundation.Data |
| `DateProjection` | `DateTimeOffset` | double seconds |
| `ResultProjection` | Result-shaped | SwiftResult |
| `KeyPathProjection` | Swift.KeyPath… | class pointer |

### 2.3 Visitor family (compile-time exhaustive)

`IProjectionVisitor<T>` declares **one `Visit` per concrete projection** (24 arms). Every production visitor implements the full interface:

| Visitor | Location | Role |
|---------|----------|------|
| `AccessorGetterConversionVisitor` | AccessorConversionVisitors.cs | property/subscript getters |
| `OptionalAccessorGetterVisitor` | same | Optional inner getter |
| `AccessorSetterConversionVisitor` | same | setters |
| `ReceiverGetterConversionVisitor` | ReceiverConversionVisitors.cs | reverse-dispatch getters |
| `ReceiverSetterConversionVisitor` | same | reverse-dispatch setters |
| `ReceiverClassCopyOutVisitor` | same | borrowed class slot materialization |
| `ReceiverParamNeedsObjectMarshalVisitor` | same | collection NewFromPayload vs Unsafe.Read |

**Verdict:** missing Visit arm = **C# compile error**, not silent `_ => null` fallthrough. Constraints.md claim **holds**.

**Caveat (not exhaustiveness failure):** many arms intentionally return `null`/passthrough. Completeness of *behavior* is still per-arm correctness, not interface shape. Notably:

- `ReceiverSetterConversionVisitor.Visit(ObjCRootedClassProjection)` → **null** while getter **retains** via `Arc.UnknownObjectRetain` and ObjCBridged/Bridgeable setter uses `FormatObjCBridgeCall` (see finding DA-W3-M3-004).

### 2.4 AppleFrameworkRegistry SSOT

Single load from embedded `apple-frameworks.json` + `objc-type-mappings.json`:

| Concern | Flag / table |
|---------|--------------|
| Module auto-bridge | `autoBridge` |
| Optional ObjC prefix fallback | `optionalFallback` |
| Non-ObjC Swift class fallback | `concreteClassFallback` |
| Value-only modules | `valueTypesOnly` (e.g. `simd`) |
| Known value types | `valueTypes[]` → `Module.Name` |
| Type remaps | `typeRemaps` |
| XML exclusions | `excludeFromXml` (e.g. `NSUnderlineStyle`) |
| Per-module ObjC prefixes | `objcPrefixes` |
| Platform unavailable | `platformUnavailable` |

**Documented intentional split oracles** (not bugs if used on the right path):

| Predicate | Breadth | Used for |
|-----------|---------|----------|
| `IsObjCModuleType` / `IsObjCClassSwiftType` | `autoBridge` − known value types | synthetic TypeRecord creation, broad marshalling |
| `IsObjCExistentialBridgedProtocol` | per-module `objcPrefixes` | existential filter (don’t drop Swift-only protocols) |
| `IsObjCPrefixBridgeCandidate` | `optionalFallback` + prefix + !value + !nested | Optional/collection ObjC heuristic |
| `IsConcreteClassFallbackModule` | RealityFoundation/RealityKit/SceneKit | ClassProjection for non-ObjC Swift classes |

---

## 3. Hunt results — parity checks

### 3.1 F15 / IsOptionalObjCBridged vs factory — **CLOSED (shared core)**

**Status:** `already-known` trap; **current code is the fix**, not a live drift.

Shared four-clause core:

```csharp
// MarshallingHelpers.IsObjCPrefixBridgeCandidate
AppleFrameworkRegistry.IsOptionalFallbackModule(named.Module) &&
!AppleFrameworkRegistry.IsNestedType(named.Name) &&
!TypeDatabaseExtensions.IsKnownAppleValueType(named) &&
AppleFrameworkRegistry.HasObjCClassPrefix(named.Name);
```

Readers:

| Site | How it uses the core |
|------|----------------------|
| `IsOptionalObjCBridged` | TypeRecord ObjC first, else `IsObjCPrefixBridgeCandidate` |
| `TypeProjectionFactory.TryProjectObjCPrefixBridged` | same candidate + extra container/pointer/generic guards + remap + report |
| Collection `TryProjectObjCElement` | delegates to `TryProjectObjCPrefixBridged` then concrete-class branch |
| `WrapperValidation.IsOptionalWithReferenceInner` Path 2 | delegates to `IsOptionalObjCBridged` |

**Intentional second oracle** (broader): `IsOptionalWithReferenceInner` = TypeRecord class/ObjC **+** ObjC prefix path **+** concrete-class Path 3.  
`IsOptionalObjCBridged` deliberately **excludes** ObjCRooted and concrete-class (Handle vs Payload shapes). Wrapper return path uses both:

```csharp
// WrapperEmitter.Return.cs — Optional class/ObjC-rooted but NOT ObjC-bridged Handle path
IsOptionalWithReferenceInner(...) && !IsOptionalObjCBridged(...)
```

Unit coverage: `MarshallingHelpersTests` IsOptionalObjCBridged region; `TypeProjectionFactoryTests` Optional ObjC / RealityFoundation paths.

### 3.2 Concrete-class Path 3 — **parity OK; duplication remains (L4)**

Factory Optional Path 3 and `TryProjectObjCElement` Branch 2 are **byte-similar** copies of:

- `!ContainsGenericParameters`, `!IsStdlibContainer`, `!IsPointerType`, `!IsNestedType`, `!IsKnownAppleValueType`, `IsConcreteClassFallbackModule` → `ClassProjection` (+ remap).

`WrapperValidation` Path 3 mirrors the same guards (module extracted from dotted name).

**No disagreement found.** Residual L4: extract one `TryProjectConcreteClassFallback(NamedTypeSpec)` shared by Optional + collections + wrapper path docs.

### 3.3 XML kind=enum vs struct — **sample clean; intentional exceptions documented**

Sampled all `kind="enum"` across Runtime `*Database.xml` (**84** occurrences):

| Pattern | Count / note |
|---------|----------------|
| `kind="enum"` + `simpleEnum="true"` + `rawValueType` | Vast majority — correct for ObjC/Swift raw enums |
| `Swift.Result` `kind="enum"` **without** simpleEnum | Intentional complex enum → SafeHandle class path |
| `URLError.Code` | **`kind="struct"`** mapping to `nint` with explicit comment (not a real .NET enum) |
| `Foundation.URL` etc. | `kind="struct"` + `objcBridgeable` — correct value-type bridge |

**NSUnderlineStyle:** in `apple-frameworks.json` as `valueTypes` + `typeRemaps` + **`excludeFromXml`** — matches constraints.md (XML would poison SwiftTypeName resolution / tuple raw mismatch).

**No mistaken `kind="enum"` without simpleEnum for a value that should be blittable struct** found in the sample. Residual risk is future hand-edits, not current corpus corruption.

### 3.4 ISwiftObject seed-drop vs isResolvable — **LOCKED mirror**

`PInvokeHelperEmitter` / `MetatypeHelperEmitter`:

```csharp
bool isResolvable =
    !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
    !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
```

`GenericTypeEmitter.GetWhereClause` comments and control flow **explicitly mirror** this:

| Protocol flag | Seed behavior | Matches isResolvable? |
|---------------|---------------|------------------------|
| `HasAssociatedTypes` | drop seed (descriptor path) | unresolvable → yes |
| `HasSelfRequirement` | drop seed | unresolvable → yes |
| `HasMethodSelfTypeParams` only | **keep** seed (conservative) | still **resolvable** PWT path → yes |
| Both SelfRequirement + method Self | drop seed (SelfRequirement checked first) | yes |

**Verdict:** constraints.md ISwiftObject seed-drop trap **holds in code**. Not a live bug.

### 3.5 AnyTypeFallback (conceptual — no validate run)

Roadmap **already-known** (~614 corpus hits): PAT / bare `Any` / ObjC delegate protocols / cross-library — “Not Worth Addressing” for bulk reduction; single-module gaps claimed ~0.

**Live generator behaviors (L3 honesty):**

| Path | Behavior |
|------|----------|
| Property resolves to `*AnyType*` | **Skip** `SkipReason.AnyTypeFallback` (`PropertyHandler`, `MemberGateEvaluator`) |
| Optional existential → public `"object"` | Skip (protocol + class sides agree — CS0535 prevention) |
| Method generic arg / return with AnyType | Gate skip |
| Factory PAT existential (no union allow) | public `"object"` by design (S12 ruling) |
| Open user generic shell | factory returns null → CSM closed specializations recover (MusicKit Items pattern) |
| `TryGetAnyTypeFallbackInfo(ProtocolListTypeSpec)` | **always false** — documented KNOWN GAP (Finding 21 / pinned unit test) |

**Report hygiene residual (already-known BSA):** open-generic rows still list AnyTypeFallback after CSM recovery (MusicKit).

### 3.6 Projection-only marshaler (L4 deferred)

`post-1.0-architecture-roadmap.md`:

> **Projection-only Marshaler.** Promote `IProjectionVisitor<T>` to be the only dispatcher; decompose ClosureHandler / BoundGenericsHandler / ExistentialHandler.

**Current state:** hybrid — factory + `GetParameterPlan`/`GetReturnPlan` on projections coexist with large handlers and string-level visitors for accessors/receivers. Not a correctness defect; **simplification inventory only**. Do not re-propose as 0.18 P0.

---

## 4. Findings

### DA-W3-M3-001: Optional ObjC four-clause heuristic is single-cored (F15 closed)

- **Severity**: P2 (was P1 trap class; residual is documentation/maintenance)  
- **Status**: `refuted` as live drift; trap **`already-known`** and **fixed**  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Reachability**: emission-live (Optional ObjC BindingTests)  
- **Claim**: `IsOptionalObjCBridged` and factory Optional/collection ObjC fallbacks share `IsObjCPrefixBridgeCandidate`; wrapper Path 2 delegates to `IsOptionalObjCBridged`.  
- **Evidence**: `MarshallingHelpers.cs:204–242`, `TypeProjectionFactory.cs:615–647`, `WrapperValidation.cs:919–928`  
- **Probe**: unit tests in MarshallingHelpersTests / TypeProjectionFactoryTests; BindingTests OptionalObjCClassProperty  
- **Prior art**: constraints.md “IsOptionalObjCBridged parity”; codebase-map dual-oracle #6  

### DA-W3-M3-002: Dual optional oracles (ObjC Handle vs broad reference) are intentional

- **Severity**: P3  
- **Status**: `confirmed` (design, not defect)  
- **Confidence**: high  
- **Lenses**: L5  
- **Reachability**: emission-live  
- **Claim**: `IsOptionalObjCBridged` ≠ `IsOptionalWithReferenceInner`. The second includes TypeRecord classes + concrete-class Path 3; the first is Handle-extraction only. Wrapper return code **depends** on the difference for Optional class/ObjCRooted NewSome/NewNone.  
- **Evidence**: `WrapperEmitter.Return.cs:684–695`, `WrapperEmitter.Marshalling.cs:658–667` (comments on RealityKit.Entity CS1061)  
- **Suggested simplification (L4)**: name the pair in constraints.md as “two oracles, one matrix” table; optional thin wrappers `IsOptionalObjCHandleAbi` / `IsOptionalNullableClassAbi`  
- **Prior art**: none as bug  

### DA-W3-M3-003: ProtocolList composition invisible to AnyTypeFallback triage

- **Severity**: P2  
- **Status**: `already-known` (documented KNOWN GAP in code + unit pin)  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: fixture-reachable  
- **Claim**: `TryGetAnyTypeFallbackInfo` only classifies `NamedTypeSpec`. Degrading `any P & Q` compositions that become `object` do not get SWIFTBIND023 / UnsupportedSwiftType via this path.  
- **Evidence**: `TypeDatabaseExtensions.cs:220–242` (comments reference Finding 21 / TypeDatabaseExtensionsTests pin)  
- **Probe**: existing unit test asserts `ReturnsFalse` for ProtocolList  
- **Suggested fixture**: protocol composition property that degrades + assert diagnostic if owner promotes  
- **Prior art**: Finding 21 resolution dual-universe notes  

### DA-W3-M3-004: ReceiverSetter ObjCRooted arm is passthrough while getter retains

- **Severity**: P2  
- **Status**: `candidate`  
- **Confidence**: medium  
- **Lenses**: L1  
- **Reachability**: fixture-reachable (reverse-dispatch ObjC-rooted class property setter)  
- **Claim**: `ReceiverGetterConversionVisitor` retains `ObjCRootedClassProjection` via `Arc.UnknownObjectRetain(.Handle)`, but `ReceiverSetterConversionVisitor.Visit(ObjCRootedClassProjection)` returns null. ObjCBridged/Bridgeable setters use `FormatObjCBridgeCall`. If reverse-dispatch setters receive raw IntPtr for ObjCRooted, missing conversion could be wrong or rely on a side path.  
- **Evidence**: `ReceiverConversionVisitors.cs:44–46` vs `:103`  
- **Probe**: BindingTests reverse-dispatch property set on ObjC-rooted class; or unit visitor probe  
- **Prior art**: none  

### DA-W3-M3-005: Concrete-class Path 3 duplicated (Optional vs collection)

- **Severity**: P3  
- **Status**: `simplification`  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: emission-live (RealityFoundation/SceneKit collections)  
- **Claim**: Optional Path 3 (`TypeProjectionFactory.cs:227–241`) and `TryProjectObjCElement` Branch 2 (`:684–697`) re-list the same guard matrix. Today they agree; future edits can drift.  
- **Suggested simplification**: single private `TryProjectConcreteClassFallback(NamedTypeSpec)`; risk class **byte-identical** if extracted carefully  
- **Prior art**: constraints.md concrete-class / optional parity theme  

### DA-W3-M3-006: EnumHandler ObjC checks use prefix-only (narrower dual)

- **Severity**: P3  
- **Status**: `candidate`  
- **Confidence**: medium  
- **Lenses**: L5  
- **Reachability**: latent (enum associated values with value types that carry ObjC prefixes)  
- **Claim**: `EnumHandler.CaseConstruction.IsObjCBridgedTypeSpec` / generic-arg walk use `HasObjCClassPrefix` **without** `IsOptionalFallbackModule` + `!IsKnownAppleValueType`. Value types with ObjC-looking prefixes (e.g. PassKit.PKPaymentNetwork — listed in valueTypes) could be over-classified as ObjC for CS0311 avoidance. Direction is fail-closed (avoid ISwiftObject helper), not wrong ABI emit.  
- **Evidence**: `EnumHandler.CaseConstruction.cs:1411–1419`, `1364–1382`  
- **Probe**: enum case with associated PKPaymentNetwork / similar  
- **Prior art**: none  

### DA-W3-M3-007: ISwiftObject seed-drop / isResolvable parity holds

- **Severity**: P3 (trap class)  
- **Status**: `refuted` as current defect; **`already-known`** load-bearing invariant  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Reachability**: emission-live (generics with PAT constraints)  
- **Claim**: `GetWhereClause` seed-drop classification mirrors `isResolvable = !HasAssociatedTypes && !HasSelfRequirement`; method-Self-only keeps seed.  
- **Evidence**: `GenericTypeEmitter.cs:227–271`, `PInvokeHelperEmitter.cs:301–303`, `MetatypeHelperEmitter.cs:160–162`  
- **Prior art**: constraints.md ISwiftObject seed-drop mirror  

### DA-W3-M3-008: Factory user-generic bail → AnyType tombstone vs CSM recovery

- **Severity**: P2  
- **Status**: `already-known` / by design  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: emission-live (MusicKit Items, generic shells)  
- **Claim**: `TypeProjectionFactory` returns null for user-defined named types with generic parameters (`:342–348`) so open shells become AnyTypeFallback skips; `ConcreteSpecializationEngine` recovers closed specializations. Report can still list open-shell skips (BSA MusicKit hygiene).  
- **Evidence**: `TypeProjectionFactory.cs:342–348`, `ConcreteSpecializationEngine.cs` comments ~859+  
- **Prior art**: BSA-06 MusicKit report hygiene; roadmap AnyTypeFallback row  

### DA-W3-M3-009: Projection-only marshaler is deferred architecture (not a bug)

- **Severity**: P3  
- **Status**: `simplification` / post-1.0  
- **Confidence**: high  
- **Lenses**: L4  
- **Reachability**: N/A  
- **Claim**: Hybrid plan/visitor/handler architecture is intentional pre-1.0 state. Full projection-only marshaler is catalogued post-1.0; do not treat as Wave 3 correctness work.  
- **Evidence**: `src/docs/Future/post-1.0-architecture-roadmap.md:47`  
- **Prior art**: prior-art index Pipeline structure  

### DA-W3-M3-010: XML kind hygiene for curated enums is good; NSUnderlineStyle exclusion correct

- **Severity**: P3  
- **Status**: `refuted` as current mistake class in sample  
- **Confidence**: high on sample; medium on never-mistaken future edits  
- **Lenses**: L5  
- **Reachability**: emission-live for Apple stubs  
- **Claim**: Enum XML entries that map to .NET enums carry `simpleEnum`+`rawValueType`; complex/result and primitive-mapped cases use non-enum kinds with comments; NSUnderlineStyle stays registry-only.  
- **Evidence**: UIKit/Foundation/CoreGraphics XML samples; `apple-frameworks.json` UIKit `excludeFromXml` / valueTypes / typeRemaps  
- **Prior art**: constraints.md XML value-type remap / NSUnderlineStyle  

---

## 5. Counts

| Bucket | Count |
|--------|------:|
| Findings total | **10** |
| Confirmed design / intentional | 2 (002, parts of 008) |
| Refuted live defects (trap holds / sample clean) | 3 (001 F15 closed, 007 seed-drop, 010 XML) |
| Already-known residual | 3 (003 ProtocolList gap, 008 CSM, F15 as trap class) |
| Candidates | 2 (004 ObjCRooted setter, 006 EnumHandler prefix-only) |
| Simplification / deferred L4 | 2 (005 Path3 dup, 009 projection-only) |
| P0 confirmed | **0** |
| P1 confirmed | **0** |
| Emission-live wrong projection ABI found | **0** (code review; no validate run) |
| ITypeProjection kinds | **24** |
| Production IProjectionVisitor implementers | **7** (all exhaustive interface) |
| TypeResolver strategies (Default) | **16** (incl. DatabaseCascade 4) |
| Runtime *Database.xml stubs | **28** XML files under `Swift.Runtime/src/Swift/` |
| Sampled `kind="enum"` entries | **84** |
| Shared ObjC optional core sites | **≥3** (MarshallingHelpers + factory + WrapperValidation Path2) |

---

## 6. Dual-oracle matrix (maintainability map)

| Decision | Oracle A | Oracle B | Must agree? | Status |
|----------|----------|----------|-------------|--------|
| Optional ObjC prefix heuristic | `IsObjCPrefixBridgeCandidate` | factory `TryProjectObjCPrefixBridged` | Yes (core) | **Shared** |
| Optional nullable reference (wrapper) | `IsOptionalWithReferenceInner` | factory Optional paths | Same *classification set* | **Mirrored** (3 paths) |
| Optional ObjC Handle extraction | `IsOptionalObjCBridged` | Wrapper marshalling B12 | Yes | **Shared** |
| Synthetic ObjC TypeRecord | `IsObjCModuleType` | — | Different purpose from prefix | Intentional broader |
| Existential ObjC filter | `IsObjCExistentialBridgedProtocol` | — | Narrower | Intentional |
| Seed drop vs PWT resolvable | `GetWhereClause` filters | `isResolvable` | Yes | **Mirrored** |
| KeyPath family name | `TypeProjectionFactory.IsKeyPathFamily` | MethodClosureBridge consumers | Yes | Single SSOT dict |
| Pointer names | `AppleFrameworkRegistry.IsPointerType` | TypeDatabaseExtensions wrapper | Yes | **Delegates** |

---

## 7. L3 notes — AnyType public surface vs skip

Desired product behavior for partial bindings:

| Surface | Desired | Observed |
|---------|---------|----------|
| Unprojectable property | Skip + report | **Yes** (AnyTypeFallback) |
| Unprojectable method | Skip or honest poison | Gate skip common; some UnsupportedSwiftType emit-then-throw paths exist elsewhere (closure tombstone pattern) |
| Existential degrade to `object` | Prefer skip or loud attribute | Mixed: optional existential property skips; bare factory PAT → `object` on some surfaces by S12 design |
| Open generic shell | Skip shell, recover closed CSM | **Yes**; report may still list shell skips |
| Composition existential degrade | Loud triage | **Gap** (DA-W3-M3-003) |

**Headline L3:** property AnyType path is fail-closed skip (good). Composition existential triage still soft. Bulk AnyTypeFallback is architecturally deferred (roadmap), not a Wave 3 fire drill.

---

## 8. L4 notes — simplification without capability loss

| Item | Risk class | Do not do if… |
|------|------------|----------------|
| Extract `TryProjectConcreteClassFallback` | byte-identical | tests for RealityFoundation Optional + Array diverge |
| Rename dual optional oracles for clarity | docs-only / behavior-preserving | rename without updating all WrapperEmitter sites |
| Projection-only marshaler | large behavior-preserving program | owner promotes post-1.0; **not** Wave 3 |
| Type IR under TypeResolver | large | AnyTypeFallback plateau trigger (post-1.0 litmus) |
| EnumHandler adopt `IsObjCPrefixBridgeCandidate` | behavior-preserving if valueTypes complete | valueTypes list incomplete for prefix-colliding values |

---

## 9. File coverage (M3 touch set)

| Path | Ledger suggestion |
|------|-------------------|
| `TypeDatabase/*.cs` (core) | `reviewed-deep` / `hazard` (resolver dual surfaces) |
| `TypeDatabase/Resolver/**` | `reviewed-deep` |
| `Marshaler/Projection/**` | `reviewed-deep` (visitors + factory) |
| `MarshallingHelpers.cs` (ObjC optional section) | `reviewed-deep` |
| `WrapperValidation.IsOptionalWithReferenceInner` | `reviewed-deep` |
| `GenericTypeEmitter.GetWhereClause` | `reviewed-deep` |
| `PInvokeHelperEmitter` isResolvable | `reviewed-deep` |
| `AppleFrameworkRegistry` + JSON | `reviewed-deep` |
| Runtime `*Database.xml` | `reviewed` (sample; not every line of FoundationDatabase) |
| Accessor/Receiver conversion visitors | `reviewed-deep` / hazard note on ObjCRooted setter |

---

## 10. Headline

**TypeDatabase / projection parity is in good shape after the F15 shared-predicate work: Optional ObjC, concrete-class fallback, visitor exhaustiveness, and ISwiftObject seed-drop are single-cored or intentionally dual with documented shape differences. Residual risk is maintainability (duplicated Path 3, EnumHandler prefix-only, ProtocolList triage gap) and one medium-confidence reverse-dispatch ObjCRooted setter candidate — not a wave of emission-live wrong-ABI projections.**

**Risk: 2/5 · Findings: 10 (0 P0, 0 P1 confirmed) · Headline: F15 closed; dual optional oracles intentional; residual L3 composition triage + L4 Path3/projection-only debt.**
