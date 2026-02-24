# Class Inheritance Implementation Plan

**Created**: February 2026
**Status**: Sessions 1-5 complete, Session 6 next
**Prerequisite for**: ObjC Binding Integration, Self-return handling, polymorphic collections

---

## Problem Statement

The generator emits every Swift class as a flat, independent C# class with no base class relationship. Swift's `class Derived : Base` becomes two unrelated C# classes that both independently implement `ISwiftObject, IDisposable`. This causes:

- **1,184 inherited members missing** across 60 derived classes in 12 of 19 validated libraries
- **Broken fluent builder chains** (SnapKit, Alamofire) where base-class methods are inaccessible
- **No polymorphic assignment** — `DataRequest` cannot be assigned to `Request` variable
- **Incorrect protocol conformance** — inherited conformances not properly resolved
- **Redundant infrastructure** — every derived class gets its own `_payload`, `Dispose()`, finalizer

---

## Impact on Library Scores

### Direct Score Improvements (Inheritance Alone)

| Library | Current | Projected | Delta | Reason |
|---------|---------|-----------|-------|--------|
| **Alamofire** | 2.90 | ~3.40 | +0.50 | 399 inherited members restored across Request hierarchy (Id, State, Cancel, Resume, Progress, etc.). DataRequest/UploadRequest/DownloadRequest become fully functional. |
| **SnapKit** | 3.20 | ~3.65 | +0.45 | Fluent chain unbroken: `ConstraintMakerExtendable` gains `equalTo`/`lessThanOrEqualTo` from `ConstraintMakerRelatable`; `ConstraintMakerEditable` gains `priority`/`labeled` from base chain. |
| **Lottie** | 3.85 | ~4.05 | +0.20 | `AnimatedButton`/`AnimatedSwitch` gain 19 members from `AnimatedControl` (Animation, AnimationSpeed, SetValueProvider, etc.). |
| **StripePayments** | 3.20 | ~3.30 | +0.10 | `STPPaymentIntentAction` gains 18 members from `STPIntentAction`. |
| **RxSwift** | 2.30 | ~2.45 | +0.15 | `HistoricalScheduler` gains 13 members from `VirtualTimeScheduler`. Subjects gain `AsObservable()` from `Observable`. |
| **CryptoSwift** | 3.33 | ~3.40 | +0.07 | `CBCMAC` gains 3 members from `CMAC`. |
| **GRDB** | 2.90 | ~2.95 | +0.05 | `TableAlias` gains 3 members from `TableAliasBase`. |

**Estimated grand average lift**: 3.37 → ~3.50 (+0.13 from inheritance alone)

### Indirect Score Improvements (Enables Other Fixes)

| Future Fix | How Inheritance Helps | Libraries Benefited |
|-----------|----------------------|---------------------|
| **Self-returning methods** | With hierarchy modeled, `Request.resume()` returning `Self` resolves to `DataRequest` on the derived type. Without hierarchy, `Self` is unknowable. | Kingfisher (+0.6), SnapKit (+0.3), KeychainAccess (+0.3) |
| **Protocol conformance quality** | Inherited conformances emit correctly. Fixes empty `""` conformance symbols (currently 10+ across RxSwift/Alamofire). `ProtocolConformanceValidator` can check inherited members. | RxSwift (+0.1), Alamofire (+0.1), all |
| **Polymorphic collections** | `IReadOnlyList<Request>` can contain `DataRequest`, `UploadRequest`, etc. Factory methods returning base types work with derived instances. | Alamofire (+0.1), Stripe (+0.1) |
| **ObjC binding integration** | NSObject hierarchy modeling is prerequisite for proper UIKit type bridging. Without it, `UIView` extensions (SkeletonView, SnapKit `snp`) remain unresolvable. | SkeletonView (+0.5), all UIKit-dependent libraries |
| **Dispose correctness** | Derived classes share base `_payload` — no double-dispose risk, correct finalizer chain. | All 12 affected libraries (correctness, not scores) |

### Combined Impact (Inheritance + Subsequent Sessions)

With inheritance as foundation, the subsequent quick-win and closure sessions become more effective:

| Library | Current | After Inheritance | After All Sessions | Total Delta |
|---------|---------|-------------------|-------------------|-------------|
| Alamofire | 2.90 | 3.40 | ~3.90 | +1.00 |
| SnapKit | 3.20 | 3.65 | ~4.10 | +0.90 |
| Lottie | 3.85 | 4.05 | ~4.30 | +0.45 |
| Kingfisher | 3.10 | 3.10 | ~3.80 | +0.70 |
| GRDB | 2.90 | 2.95 | ~3.70 | +0.80 |
| Stripe | 3.20 | 3.30 | ~3.70 | +0.50 |
| RxSwift | 2.30 | 2.45 | ~3.00 | +0.70 |
| **Grand Avg** | **3.37** | **~3.50** | **~3.95** | **+0.58** |

---

## Current State (After Sessions 1-5)

### Parser & Model ✅
- `Node` record deserializes `superclassUsr`, `superclassNames`, `inheritsConvenienceInitializers`, `hasMissingDesignatedInitializers` from ABI JSON
- `ClassDecl` has `SuperclassUsr`, `SuperclassNames`, `DirectSuperclassName`, `InheritsConvenienceInitializers`, `HasMissingDesignatedInitializers`, `ResolvedSuperclass`, `HasResolvedSuperclass`, `HasExternalSuperclass`
- `ModuleProcessor.ResolveClassHierarchy()` resolves same-module superclass references with cycle detection

### TypeRecord & Module Database ✅
- `TypeRecord.SuperclassTypeName` persists direct superclass for cross-module support
- `ModuleDatabaseEmitter` serializes `superclass` attribute; `TypeDatabase` deserializes it
- Generic superclass names (e.g., `VirtualTimeScheduler<Converter>`) guarded — stored as null

### Emitter (`ClassHandler`) — inheritance emission ✅ (Session 3)
- Topological sort ensures base classes are emitted before derived
- Derived classes emit `class Derived : Base, [new protocols only]`
- Derived classes inherit `_payload` (now `protected`), `Dispose()`, `~Destructor()`, equality from base
- Derived classes re-emit `ISwiftObject` members (`GetTypeMetadata`, `NewFromPayload`, `MarshalToSwift`, `GetProtocolConformanceDescriptor`) with their own type metadata
- Private constructor on derived uses root base type for `SwiftSafeHandle<T>` (VWT Destroy / swift_release operates on isa pointer, ignoring T)
- Disposal `<remarks>` emitted on all class XML doc comments
- **Ownership analysis (Session 3)**: Generated class return path is correct — each Swift P/Invoke return provides +1 ARC retain, wrapper Dispose provides exactly -1. No ARC code changes needed.
- **Deferred to Session G**: Container/optional factory paths (`SwiftMarshal.MarshalFromSwift<T>` beyond generated class returns), manual `NewFromPayload` calls with aliased pointers

### Validation (`ProtocolConformanceValidator`) — ancestor walking ✅ (Session 5)
- `GetEmittableAncestors(TypeDecl)` walks the `ResolvedSuperclass` chain, stopping at non-emittable ancestors
- `FindMatchingProperty`, `FindMatchingSubscript`, `FindMatchingMethod` iterate ancestors — a derived class satisfies protocol requirements via inherited base class members
- Non-class types (structs, enums) only check own members

### Conformance dictionary inheritance ✅ (Session 5)
- Derived classes inherit conformance dictionary entries from ancestors (via `CollectAllConformancesWithResolvedSymbols`)
- Empty conformance symbols (`""`) resolved from ancestor's non-empty symbol, or omitted entirely (safety net in `GenerateProtocolConformanceDictionaryEntries`)
- `IsEffectivelyDerived(ClassDecl)` is the canonical predicate — used consistently in `ClassHandler.Emit`, `ClassISwiftObjectMethodWriter`, and `GetRootBaseTypeNameWithGenerics`

### Type ordering — topological sort ✅ (Session 3)
`BaseHandler.TopologicallySortTypes()` applies Kahn's algorithm in `HandleBaseDecl` before the emission loop. Handles both top-level types (`ModuleHandler.Emit` → `HandleBaseDecl`) and nested types (`ClassHandler.Emit` → `HandleBaseDecl`). Original index used as tie-breaker for stable ordering. `ReferenceEqualityComparer.Instance` used for ClassDecl identity.

---

## Implementation Sessions

### Session 1: Parse & Model ✅ COMPLETE

**Goal**: Get superclass data from ABI JSON into the model. Pure data plumbing — no emission changes.

**Changes**:

1. **`Node` record** (`SwiftABIParser.cs:63-67`): Added 4 nullable fields matching ABI JSON keys for automatic Newtonsoft.Json deserialization.

2. **`ClassDecl` model** (`ClassDecl.cs`): Added inheritance properties. `DirectSuperclassName` is a computed property from `SuperclassNames[0]` (not stored separately as originally planned).

3. **`CreateClassDecl`** (`SwiftABIParser.cs:705-708`): Populated from Node fields.

4. **Unit tests** (`ClassInheritanceParserTests.cs`): 8 tests covering superclass parsing, ObjC USRs, initializer flags, and default values.

**Results**: 4023 unit tests pass (18 new), 32/32 validation, golden files unchanged.

---

### Session 2: Class Hierarchy Resolution ✅ COMPLETE

**Goal**: Resolve superclass names to actual ClassDecl references. Persist superclass metadata on TypeRecord for cross-module support.

**Changes**:

1. **`ClassDecl`** (`ClassDecl.cs`): Added `ResolvedSuperclass`, `HasResolvedSuperclass`, `HasExternalSuperclass`.

2. **`ModuleProcessor.ResolveClassHierarchy()`**: Same-module resolution by `SwiftTypeName.ModuleQualifiedName` lookup. Cross-module and ObjC bases left unresolved (`HasExternalSuperclass = true`). Cycle detection via Floyd's tortoise-and-hare with full-cycle participant cleanup (all members of a cycle cleared, not just the detection node).

3. **`TypeRecord.SuperclassTypeName`** (`TypeRecord.cs`): Added `SwiftTypeName?` property. Populated in `RegisterClassType()` with guard for generic superclass names (`Contains('<')` → null, since `SwiftTypeName.FromModuleQualifiedName` rejects generics).

4. **Module database serialization**: `ModuleDatabaseEmitter` writes optional `superclass` attribute. `TypeDatabase.ReadVersion1_0` reads it back with matching generic-name guard.

5. **Unit tests**: `ClassHierarchyResolutionTests.cs` (6 tests: 3-level chain, ObjC base, cross-module base, root class, multiple hierarchies, same-module resolution). `SuperclassModuleDatabaseTests.cs` (4 tests: TypeRecord storage, XML emission, omission for root class, round-trip).

**Results**: 4023 unit tests pass, 32/32 validation, golden files unchanged, no emission changes.

**What was deferred to Session 3**: Topological sort of emission ordering. Originally planned for Session 2, but reordering types changes generated output (golden files, validation line counts), which contradicts Session 2's "no output changes" constraint. The sort is only needed once Session 3 adds `class Derived : Base` syntax (C# requires base class declared before derived in the same file). **Must also handle nested types** — not just top-level `moduleDecl.Types`, but also the `HandleBaseDecl(..., type.Types, ...)` calls for nested class hierarchies.

---

### Session 3: Core Emission — Topological Sort, Base Class Syntax, Shared Infrastructure ✅ COMPLETE

**Goal**: Topologically sort emission ordering (moved from Session 2), emit `class Derived : Base`, share infrastructure (payload, Dispose, ISwiftObject) between base and derived classes, document ownership model.

**Changes**:

0. **Topological sort** (`BaseHandler.TopologicallySortTypes` in `IHandler.cs`):
   - Kahn's algorithm applied in `HandleBaseDecl` before the emission loop — handles all contexts (top-level via `ModuleHandler.Emit`, nested via `ClassHandler.Emit`)
   - `ReferenceEqualityComparer.Instance` for ClassDecl identity. Original index tie-breaking for stability.
   - Cycle safety net: if any nodes remain with non-zero in-degree after Kahn's loop, they are appended in original order (not silently dropped) and a `Debug.WriteLine` warning is emitted.

1. **`ClassHandler.Emit`** — class declaration syntax:
   - Derived: `class Derived : BaseClass, ISwiftObject, [new protocols only]` — keeps ISwiftObject (needed for explicit interface re-implementation), omits IDisposable (inherited), filters base-class protocols.
   - Root: unchanged `class X : ISwiftObject, IDisposable, [protocols]`.
   - **Fallback guard**: `isDerived` requires both `HasResolvedSuperclass` AND that the base class won't be skipped during emission (`!GenericTypeEmitter.TryGetUnsupportedConstraint`). If the base has unsupported constraints (SwiftUI/Combine generics), derived falls back to flat emission.

2. **Derived class payload/Dispose/equality sharing**:
   - `_payload` is `protected` on base classes, inherited by derived.
   - `_payloadSize` is private per class (each class needs its own metadata size).
   - Derived classes inherit `Dispose()`, `~Destructor()`, `Payload` property from base.
   - `IEquatable<T>` is parameterized per type — both base and derived emit their own equality if they conform to Equatable.

3. **ISwiftObject re-implementation on derived classes**:
   - All ISwiftObject static abstract members (`GetTypeMetadata`, `NewFromPayload`, `MarshalToSwift`, `GetProtocolConformanceDescriptor`) re-emitted on derived with their own type metadata. Without this, `SwiftMarshal.MarshalFromSwift<Derived>()` would resolve to Base's `NewFromPayload`.
   - Private constructor on derived uses **root base type** for `SwiftSafeHandle<T>` (VWT Destroy / swift_release operates on the isa pointer, ignoring T). Root type found by walking `ResolvedSuperclass` chain in both `ClassHandler` and `MethodMarshalPlanBuilder`.

4. **Constructor chaining via `SwiftInheritanceChain` sentinel**:
   - Challenge: derived class constructors must chain to base, but base classes may not have a parameterless constructor (all-defaults constructors with unsupported types get skipped).
   - Solution: `SwiftInheritanceChain` marker struct in `Swift.Runtime/SwiftHandle.cs` — a unique empty struct that cannot conflict with any Swift-generated constructor parameter type.
   - All classes emit `protected ClassName(SwiftInheritanceChain _swiftObject)` constructor.
   - All derived class constructors (private SwiftHandle, protected sentinel, and public user-facing) chain to `base(default(SwiftInheritanceChain))`.
   - Three emission sites: `ClassISwiftObjectMethodWriter.EmitPrivateConstructor`, `ClassHandler.Emit` (sentinel), `WrapperEmitter.Signature.EmitSignatureConstructor`.
   - `HasParameterlessConstructor` check uses `CSSignature.Count <= 1` only (not all-defaults, which causes false positives for skipped constructors).

5. **Ownership analysis (documentation only, no ARC code changes)**:
   - Generated class return path is correct: each Swift P/Invoke return provides +1 ARC retain, wrapper's `Dispose()` → `VWT->Destroy` → `swift_release` provides exactly -1.
   - Disposal `<remarks>` emitted on all class XML doc comments.
   - **Deferred to Session G**: Container/optional factory paths, manual `NewFromPayload` with aliased pointers, Roslyn analyzer for compile-time dispose enforcement.

6. **Tests** (`ClassInheritanceEmissionTests.cs`, 34 tests):
   - Topological sort: no classes, single root, already correct, reversed, 3-level chain, mixed types, cyclic dependency (safety net).
   - Declaration syntax: derived starts with base, keeps ISwiftObject, omits IDisposable, new protocols emitted.
   - Payload sharing: no `_payload` field, no Dispose, no finalizer on derived; protected `_payload` on base.
   - ISwiftObject: own `GetTypeMetadata`, own `NewFromPayload`, root base type for `SwiftSafeHandle<T>`, 3-level hierarchy root base.
   - Fallback: external superclass flat emission, no resolved superclass flat emission, skipped base class (unsupported constraints) flat emission.
   - Disposal remarks on both base and derived.
   - Equality: derived with Equatable gets own equality; derived without Equatable inherits none.

**Results**: 4049 unit tests pass (34 new), 700 integration, 221 runtime. 32/32 validation libraries (up from 22/32 mid-session due to inheritance compilation fixes across Alamofire, SnapKit, Stripe hierarchies). Golden files updated. CompileCheck 0 errors. CS0108/CS0109 member-hiding warnings suppressed in TestFramework projects (resolved by Session 4 virtual/override dispatch).

---

### Session 4: Virtual/Override Method Dispatch ✅ COMPLETE

**Goal**: Emit proper `virtual`/`override`/`sealed override` keywords on class instance methods and properties. Remove CS0108/CS0109 warning suppressions.

**Key discovery**: The original plan assumed ABI JSON includes ALL members (inherited + own) requiring dedup. This is wrong — ABI JSON only includes a class's own members plus overridden members. The `"overriding": true` field and `"Override"` in `declAttributes` explicitly mark overrides. No member dedup filtering needed — the real work is proper `virtual`/`override` keyword emission.

**Scale**: Test library has 1 override. Real libraries: Lottie 56, Stripe 73, Alamofire 5.

**Changes**:

1. **Parser** (`SwiftABIParser.cs`, `MethodDecl.cs`, `PropertyDecl.cs`):
   - Added `overriding` (nullable bool) to `Node` record for ABI JSON deserialization
   - Added `IsOverride` and `WasEmitted` to `MethodDecl` — `IsOverride` set from `node.overriding == true || declAttributes.Contains("Override")`
   - Added `IsOverride`, `IsFinal`, and `WasEmitted` to `PropertyDecl` — set from Var node's overriding field and declAttributes
   - `WasEmitted` is set to `true` at the 4 actual emission points: `MethodHandler` (constructor + regular method), `PropertyHandler` (AsyncStream + normal property)

2. **Method emission** (`WrapperEmitter.Signature.cs`):
   - Dispatch modifier computed for class instance methods (excludes static, constructors, async constructors, accessors)
   - `IsOverride` alone is NOT sufficient — must also verify the overridden member exists in C# output via `HasMethodInResolvedAncestors`
   - `IsOverride && ancestorMatch && IsFinal` → `sealed override`, `IsOverride && ancestorMatch` → `override`, else `!classIsFinal && !methodIsFinal` → `virtual`

3. **Override safety — `HasMethodInResolvedAncestors`/`HasPropertyInResolvedAncestors`** (`WrapperEmitter.Signature.cs`):
   - Walks the `ResolvedSuperclass` chain looking for a matching emitted member
   - **Methods**: matches by Swift name + parameter count + `SwiftTypeSpec.ToString()` for each parameter + `WasEmitted == true`. Parameter-type matching prevents false positives with overloaded methods (e.g., base has `foo(Int)` emitted and `foo(String)` skipped — derived overriding `foo(String)` must not match `foo(Int)`)
   - **Properties**: matches by Swift name + `WasEmitted == true`
   - Both abort early if an ancestor has unsupported constraints (`GenericTypeEmitter.TryGetUnsupportedConstraint`)
   - Prevents CS0115 ("no suitable method found to override") for: (1) external ancestors (NSObject, UIView — no C# base class), (2) methods skipped by validation gates in the base class, (3) mixed chains with in-module parent but external grandparent, (4) overloaded methods where only a different-signature overload was emitted
   - Relies on topological sort (Session 3) to guarantee base classes emit before derived — `WasEmitted` is always set before derived class override checks run

4. **Property emission** (`PropertyHandler.cs`):
   - Same dispatch modifier logic adapted for properties using `PropertyDecl.IsOverride`/`IsFinal` + `HasPropertyInResolvedAncestors`
   - `ClassDecl.IsFinal` gate prevents virtual on final class properties

5. **Warning suppression removal** (`CompileCheck.csproj`, `RuntimeTestsApp.csproj`):
   - Removed `CS0108;CS0109` from `<NoWarn>` — these are now resolved by proper virtual/override emission

6. **Tests**:
   - **Parser tests** (`ClassInheritanceParserTests.cs`, 5 new): `overriding: true`, `"Override"` declAttribute, regular method, property override, property final
   - **Emission tests** (`ClassInheritanceEmissionTests.cs`, 17 new): virtual on non-final class, no virtual on final class, no virtual on final method, override with resolved base, sealed override with resolved base, override with external base (virtual fallback), static (no virtual), constructor (no virtual/override), accessor (no virtual/override), property virtual/override/sealed override/final/static, property override with external base (virtual fallback), overloaded method with skipped base overload (ancestor check returns false), overloaded method with emitted base overload (ancestor check returns true)

**Edge cases handled**:
- Accessor methods do NOT get virtual/override — they're private helpers, the public property declaration carries the modifier
- Constructors cannot be virtual/override in C# — `IsOverride` is parsed but not used by `EmitSignatureConstructor`
- `final override` (e.g., Lottie `CompatibleAnimationView.contentMode`) correctly emits `sealed override`
- Cross-module inheritance: if base is unresolved, derived falls back to flat emission — no override needed
- External ancestors (NSObject, UIView): override → virtual fallback (no C# base class to override)
- Skipped base methods: validation gates may prune base method → `WasEmitted` prevents false override
- Overloaded methods: `SwiftTypeSpec.ToString()` per parameter prevents matching wrong overload

**Acceptance gate**: Zero CS0108/CS0109 after removing suppressions. 32/32 validation. Golden files updated.

**Results**: 4071 unit tests pass (22 new: 5 parser + 17 emission), 700 integration, 221 runtime. 32/32 validation. Golden files updated.

---

### Session 5: Protocol Conformance Inheritance ✅ COMPLETE

**Goal**: Fix protocol conformance resolution for inherited conformances. Fix empty conformance symbols. Ensure consistent "effectively derived" predicate across all class emission.

**Changes**:

1. **Empty conformance symbol guard** (`TypeHandlerHelpers.cs:570-575`):
   - Added `string.IsNullOrEmpty(protocolConformanceSymbol)` guard in `GenerateProtocolConformanceDictionaryEntries`
   - Applies to all types (class/struct/enum) — an empty symbol would crash at runtime via `LoadFromSymbol("lib", "")`

2. **`IsEffectivelyDerived` predicate** (`ClassHandler.cs:281-289`):
   - Extracted `internal static bool IsEffectivelyDerived(ClassDecl)` — canonical check: `HasResolvedSuperclass && !TryGetUnsupportedConstraint(base)`
   - Updated `ClassHandler.Emit` and `ClassISwiftObjectMethodWriter` constructor to use it (previously inconsistent — Emit checked constraints, ISwiftObjectMethodWriter didn't)
   - **P1 fix**: `GetRootBaseTypeNameWithGenerics` also stops at non-emittable ancestors, preventing `SwiftSafeHandle<T>` type mismatch when a class falls back to flat emission

3. **Ancestor member walking in `ProtocolConformanceValidator`** (`ProtocolConformanceValidator.cs:348-417`):
   - `GetEmittableAncestors(TypeDecl)` yields the type itself, then walks `ResolvedSuperclass` chain stopping at the first non-emittable ancestor (unsupported generic constraints). For non-class types, yields only self.
   - Updated `FindMatchingProperty`, `FindMatchingSubscript`, `FindMatchingMethod` to iterate `GetEmittableAncestors` — a derived class satisfies protocol requirements via inherited base class members
   - **Safety**: stops at non-emittable ancestors to prevent relying on members from a non-emitted base class

4. **Inherited conformances in class descriptor dictionary** (`ClassHandler.cs:617-687`):
   - `CollectAllConformancesWithResolvedSymbols()` yields own conformances first (resolving empty symbols from ancestors via `with` expression), then ancestor conformances not already seen. Deduplicates by `Protocol.ModuleQualifiedName`. Gated on `_isDerived`.
   - `FindConformanceSymbolInAncestors(SwiftTypeName)` walks the chain for a non-empty symbol
   - Existing `ShouldEmitConformance` filter still applies to inherited conformances (same module, same protocol, same rules)

5. **Tests** (18 new across 3 files):
   - **ProtocolConformanceValidatorTests** (11 new): derived finds method in base, derived finds property in base, three-level chain finds method in grandparent, member not in base or self, struct only checks self, skipped base ancestor members not counted, `GetEmittableAncestors` unit tests (non-class, root class, deep chain, stops at non-emittable)
   - **ClassInheritanceEmissionTests** (6 new): derived inherits conformance dictionary entries, empty symbol resolves from base, own+inherited merged, empty symbol with no resolution omitted, own non-empty takes priority, skipped base no ancestor conformances, P1 constructor handle type mismatch regression test
   - **TypeHandlerHelpersTests** (1 new): empty conformance symbol excluded

**Edge cases handled**:
- Cross-module base (unresolved): ancestor walk terminates immediately → current flat behavior
- Base with unsupported generic constraints (skipped): `IsEffectivelyDerived` returns false → flat emission, no ancestor walk in validator, no ancestor conformances in dictionary
- Deep hierarchy (3+ levels): `GetEmittableAncestors` walks full chain; `CollectAll` deduplicates
- Same protocol at multiple levels: own conformance wins (iterated first); ancestor conformances deduped
- Equatable inherited from base: dictionary gets base's Equatable symbol; `ClassEqualityMethodsWriter` does NOT emit `Equals(Derived)` unless own conformance exists (correct — `IEquatable<Derived>` is different from `IEquatable<Base>`)
- Flat emission handle type (P1): `GetRootBaseTypeNameWithGenerics` stops at non-emittable ancestors, so `SwiftSafeHandle<T>` in the private constructor matches the `_payload` field type

**Results**: 4089 unit tests pass (18 new), 700 integration, 32/32 validation. Golden files unchanged. CompileCheck 0 errors.

---

### Session 6: Validation, Edge Cases & Polish (Medium)

**Goal**: Full validation pass. Handle edge cases. Update tests and golden files.

**Changes**:

1. **Full validation**: `./validate-libraries.sh` — all 32 targets must pass (0 compile errors)

2. **Edge cases to handle**:
   - **Generic base classes**: `HistoricalScheduler : VirtualTimeScheduler<RxTime>` — bound generics in inheritance
   - **Actor inheritance**: Actors with base classes (actors are classes in Swift)
   - **ObjC base classes**: `NSObject`, `UIView`, `UIViewController` — mark as external, no C# base class emitted (until ObjC binding integration). Derived classes remain flat for ObjC bases.
   - **Cross-module inheritance**: Class in StripePaymentSheet inheriting from class in StripeCore — needs module database resolution
   - **Final classes**: `IsFinal` classes cannot be subclassed — no special handling needed, but verify no class tries to inherit from a final class
   - **Classes inheriting from classes that inherit from ObjC**: e.g., `LottieAnimationView : LottieAnimationViewBase : UIView` — model the in-module chain, stop at ObjC boundary

3. **TestFramework**: Add a simple inheritance test case (if `Animal`/`Dog` hierarchy exists in SwiftBindingsTestLib). Verify generated C# compiles and base members accessible.

4. **Golden files**: Regenerate `golden/` files. They will change (inheritance syntax, virtual/override keywords).

5. **Unit test updates**: Existing tests may need adjustment for new emission patterns. Update expected output in `ClassHandlerOutputTests`, `ThirdPartyValidationFixTests`, etc.

6. **Run full test suite**: `./run-tests.sh`, `cd TestFramework && ./build-and-test.sh`

**Acceptance gate**: 32/32 validation libraries pass. Unit tests pass (updated as needed). Golden files regenerated. TestFramework coverage maintained.

**Commit**: "Session 6: Class inheritance — validation, edge cases, golden file updates"

---

## Scope Boundaries

### In Scope
- In-module Swift class inheritance (same module) ✅
- Cross-module Swift class inheritance (via module database) ✅ (falls back to flat for unresolved)
- Base class declaration syntax (`: BaseClass`) ✅
- Shared Dispose/payload infrastructure ✅
- Virtual/override keyword emission (`virtual`, `override`, `sealed override`) ✅
- Override detection from ABI JSON (`overriding` field, `Override`/`Final` declAttributes) ✅
- Protocol conformance inheritance ✅
- Empty conformance symbol fixes ✅
- Topological sort for emission ordering ✅

### Out of Scope (Deferred)
- **ObjC base classes** (NSObject, UIView hierarchy) — requires ObjC binding integration (separate project)
- **Runtime polymorphic unwrapping** — when Swift returns a base type that is actually a derived type, creating the correct C# wrapper. Deferred to runtime improvements.
- **Constructor chaining** — ✅ Resolved in Session 3 via `SwiftInheritanceChain` sentinel pattern.
- **Multiple inheritance** — Swift is single-inheritance for classes. Not applicable.
- **Property/method override ABI differences** — if a derived class overrides a property with different ABI layout. Edge case — defer if encountered.

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| CS0115 from incorrect override on edge case methods | Very Low | Medium | `HasMethodInResolvedAncestors` checks `WasEmitted` + name + arity + parameter types; `HasPropertyInResolvedAncestors` checks `WasEmitted` + name. Topological sort guarantees base emits first. |
| Dispose/payload sharing causes memory corruption | Low | Critical | Test with runtime tests on iOS simulator. SafeHandle prevents most issues. |
| Topological sort fails for circular references | Very Low | Medium | Cycle detection with clear error message |
| Cross-module base classes not resolved | Medium | Low | Fall back to flat emission (current behavior) when base is unresolved |
| Override detection misses edge cases | Very Low | Medium | ABI JSON `overriding` field + `Override` declAttribute checked redundantly; ancestor walk validates emitted member exists with matching signature |
| Existing tests break from emission changes | High | Low | Expected — update test expectations as part of each session |

---

## Validation Checkpoints

**IMPORTANT: Validation runs ONCE at the very end of each session — not after each sub-task.** A session may involve 5-10 sub-tasks (parse this, update that, write tests, etc.). Do NOT run `run-tests.sh`, `validate-libraries.sh`, or `build-and-test.sh` after each sub-task. Run them all once as the final step of the session.

**Output capture**: Always pipe slow commands to a temp file so results can be re-read without re-running. Use the Read tool on the temp file (with offset/limit if needed) instead of re-running with different `tail`/`grep` arguments.

### End-of-session validation (run once, at the very end):
```bash
./run-tests.sh 2>&1 | tee /tmp/test-results.txt                                   # Unit tests (~2 min)
./validate-libraries.sh 2>&1 | tee /tmp/validation-results.txt                     # Compile gate (~5 min)
cd TestFramework && ./build-and-test.sh 2>&1 | tee /tmp/testframework-results.txt  # Integration + coverage (~5 min)
golden/check-golden-files.sh                                                       # Determinism (fast)
```

Sessions I1-I2 produced zero emission changes (model/resolution only) ✅. Session I3 added inheritance syntax. Session I4 added virtual/override dispatch keywords. Session I5 added protocol conformance inheritance (golden files unchanged). Session I6 may change emission output further.
